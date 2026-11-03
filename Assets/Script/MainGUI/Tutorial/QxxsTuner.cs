using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 【曲线行驶自动调参器】与 DemoDriver 配套，机制与 ZjzwTuner 相同：
/// 参数全部走 Tuning/qxxs_tuning.json 外部热加载 + 自动连续重试，全程只开一次工程。
///
/// 用法：
/// 1. （可选）运行 `python Tuning/tune_qxxs.py gen` 生成候选参数到 Tuning/qxxs_tuning.json；
///    也可以直接手改该 JSON（在 Assets 外，Unity 不会导入、不会触发重编译）
/// 2. 打开工程 → 考试模式 → 曲线行驶单项练习 → 按 T 开始批量调参
///    （T 键只在曲线行驶场景响应，直角转弯场景的 T 由 ZjzwTuner 接管，互不干扰）
/// 3. 每轮自动执行：重读 JSON → 注入参数 → 自动从起点演示 → 结果追加
///    Tuning/qxxs_results.jsonl → 自动重载 Exam Mode 进下一轮
/// 4. 结束后 `python Tuning/tune_qxxs.py best` 分析结果导出最优参数；
///    `python Tuning/tune_qxxs.py plot` 画轨迹对比图
/// </summary>
public class QxxsTuner : MonoBehaviour
{
    private enum BatchState { Idle, WaitDriver, Running, WaitScene }

    private BatchState _state = BatchState.Idle;
    private int _doneRounds;        // 已完成轮数（域重载后从 results 文件行数恢复）
    private QxxsTuneBatch _batch;   // 本轮使用的批次配置（每轮开始热重载）
    private QxxsTune _curTune;      // 当前候选
    private int _curRep;            // 当前候选第几次重复（1 起）
    private bool _sceneReloaded;    // LoadScene 完成标志
    private float _waitTimer;       // 场景就绪等待计时
    private float _roundStartTime;  // 本轮开始时刻（兜底超时用）
    private float _driverWaitStart = -1f; // 找不到 DemoDriver 的等待起点（超时中止）
    private DemoDriver _driver;           // 当前场景的演示组件缓存（场景重载后自动失效重查）
    private bool _autoMode;               // 全自动模式（Tuning/qxxs_autostart.flag 存在时）
    private bool _autoStarted;            // 自动模式已发起（防重复）
    private readonly List<QxxsRoundResult> _batchResults = new List<QxxsRoundResult>();

    private DemoDriver GetDriver()
    {
        if (_driver != null) return _driver;
        _driver = FindObjectOfType<DemoDriver>();
        return _driver;
    }

    /// <summary>调参文件目录：项目根/Tuning</summary>
    private static string TuningDir
    {
        get { return Path.Combine(Application.dataPath, "..", "Tuning").Replace('\\', '/'); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (FindObjectOfType<QxxsTuner>() != null) return;
        GameObject go = new GameObject("QxxsTuner");
        DontDestroyOnLoad(go);
        go.AddComponent<QxxsTuner>();
        SceneManager.sceneLoaded += OnSceneLoadedStatic;
    }

    private void Awake()
    {
        // 自动模式开关 = 项目根/Tuning/qxxs_autostart.flag 文件存在（外部脚本/命令行跑批控制）
        try { _autoMode = File.Exists(Path.Combine(TuningDir, "qxxs_autostart.flag")); }
        catch { _autoMode = false; }
        // 用完即删：残留的 flag 会在之后任何一次 Play 里重新劫持进跑批
        if (_autoMode) { try { File.Delete(Path.Combine(TuningDir, "qxxs_autostart.flag")); } catch { } }
        if (_autoMode) Debug.Log("[QxxsTuner] 自动模式已启用（检测到 autostart.flag）");
    }

    private static void OnSceneLoadedStatic(Scene scene, LoadSceneMode mode)
    {
        QxxsTuner t = FindObjectOfType<QxxsTuner>();
        if (t != null) t.OnSceneLoaded(scene);
    }

    private void OnSceneLoaded(Scene scene)
    {
        if (scene.name == "Exam Mode" && _state == BatchState.WaitScene)
        {
            _sceneReloaded = true;
            _waitTimer = 1.0f;
        }
    }

    private void Update()
    {
        // 全自动模式：外部脚本写好 autostart.flag 后启动 Unity，这里免按 T 自动开批次
        if (_autoMode && !_autoStarted && _state == BatchState.Idle)
        {
            _autoStarted = true;
            AutoStart();
        }

        if (Input.GetKeyDown(KeyCode.T))
        {
            // 只在曲线行驶场景响应 T（其他项目场景把 T 让给对应调参器）
            SelectPass spNow = FindObjectOfType<SelectPass>();
            if (spNow != null && spNow.sites != SelectPass.Sites.Qxxs) return;

            if (_state == BatchState.Idle) StartBatch();
            else AbortBatch("已中止调参批次（已完成 " + _doneRounds + " 轮）");
            return;
        }

        switch (_state)
        {
            case BatchState.WaitDriver: UpdateWaitDriver(); break;
            case BatchState.Running: UpdateRunning(); break;
            case BatchState.WaitScene: UpdateWaitScene(); break;
        }
    }

    /// <summary>全自动模式入口：补齐 SelectPass(sites=Qxxs) → 加载 Exam Mode → 开批次</summary>
    private void AutoStart()
    {
        Application.runInBackground = true;
        SelectPass sp = FindObjectOfType<SelectPass>();
        if (sp == null)
        {
            GameObject go = new GameObject("SelectPass(QxxsTuner)");
            DontDestroyOnLoad(go);
            sp = go.AddComponent<SelectPass>();
        }
        sp.sites = SelectPass.Sites.Qxxs;

        // 无论当前在哪个场景都重载 Exam Mode，保证摆位逻辑重跑
        Debug.Log("[QxxsTuner] 自动模式：加载 Exam Mode（sites=Qxxs）");
        SceneManager.LoadScene("Exam Mode");
        StartBatch();
    }

    private void StartBatch()
    {
        _batch = LoadBatch();
        if (_batch == null || _batch.candidates == null || _batch.candidates.Length == 0)
        {
            ShowCaption("调参启动失败：Tuning/qxxs_tuning.json 无候选（先运行 python Tuning/tune_qxxs.py gen）", 8f);
            WriteDoneMarker("启动失败：无候选参数");
            return;
        }
        _doneRounds = CountResultLines();
        int total = _batch.candidates.Length * Mathf.Max(1, _batch.repeat);
        if (_doneRounds >= total)
        {
            ShowCaption("调参批次已全部完成（" + total + " 轮）。删掉/清空 qxxs_results.jsonl 可重跑", 8f);
            WriteDoneMarker("批次此前已完成：" + total + " 轮");
            return;
        }
        _batchResults.Clear();
        _state = BatchState.WaitDriver;
        _waitTimer = 0.2f;
        Debug.Log("[QxxsTuner] 批次开始：候选 " + _batch.candidates.Length + " 组 × " + Mathf.Max(1, _batch.repeat) + " 次，已完成 " + _doneRounds + " 轮（文件恢复）· 代码版本 " + BuildStamp.Version);
    }

    private void UpdateWaitDriver()
    {
        _waitTimer -= Time.unscaledDeltaTime;
        if (_waitTimer > 0f) return;

        QxxsTuneBatch fresh = LoadBatch();
        if (fresh != null && fresh.candidates != null && fresh.candidates.Length > 0) _batch = fresh;

        int repeat = Mathf.Max(1, _batch.repeat);
        int idx = _doneRounds / repeat;
        if (idx >= _batch.candidates.Length)
        {
            FinishBatch();
            return;
        }
        _curTune = _batch.candidates[idx];
        _curRep = _doneRounds % repeat + 1;

        DemoDriver driver = GetDriver();
        if (driver == null)
        {
            if (_driverWaitStart < 0f) _driverWaitStart = Time.time;
            _waitTimer = 0.5f;
            if (Time.time - _driverWaitStart > 10f)
            {
                AbortBatch("调参中止：10 秒内找不到演示组件（确认在曲线行驶单项练习场景中）");
            }
            return;
        }
        _driverWaitStart = -1f;

        Time.timeScale = _batch.timeScale > 0.05f ? _batch.timeScale : 1f;
        string caption = string.Format(
            "调参 {0}/{1}　组#{2}（第{3}次）　look={4:F1} bias={5:F1} gain={6:F1} v={7:F1}/{8:F1}",
            _doneRounds + 1, _batch.candidates.Length * repeat, _curTune.id, _curRep,
            _curTune.lookForward, _curTune.outerBias, _curTune.steerGain, _curTune.vApproach, _curTune.vCurve);
        _roundStartTime = Time.time;
        driver.StartQxxsTuningRound(_curTune, caption);
        _state = BatchState.Running;
    }

    private void UpdateRunning()
    {
        DemoDriver driver = GetDriver();
        if (driver == null)
        {
            _sceneReloaded = false;
            _state = BatchState.WaitScene;
            _waitTimer = 1.0f;
            return;
        }

        // 兜底超时：DemoDriver 异常未回报（正常失败路径都在 DemoDriver 内部兜住）
        float limit = (_curTune != null ? _curTune.maxRoundSeconds : 90f) + 20f;
        if (Time.time - _roundStartTime > limit * Mathf.Max(1f, _batch.timeScale))
        {
            EndRound(driver, "watchdog");
            return;
        }

        if (driver.roundFinished && driver.lastResult != null)
        {
            EndRound(driver, null);
        }
    }

    /// <summary>收尾一轮：从 DemoDriver.lastResult 拷贝公共字段 + 附上本组参数 → 写文件 → 重载场景</summary>
    private void EndRound(DemoDriver driver, string forceOutcome)
    {
        QxxsRoundResult r = new QxxsRoundResult();
        if (forceOutcome != null)
        {
            r.outcome = forceOutcome;
            r.seconds = Time.time - _roundStartTime;
            r.finalScore = Car_Information.GetScore();
        }
        else
        {
            ZjzwRoundResult src = driver.lastResult; // DemoDriver 公共结果（几何参数字段对本项目无意义，忽略）
            r.outcome = src.outcome;
            r.seconds = src.seconds;
            r.finalScore = src.finalScore;
            r.endDist = src.endDist;
            r.maxDev = src.maxDev;
            r.endX = src.endX;
            r.endZ = src.endZ;
            r.endYaw = src.endYaw;
            r.trace = src.trace;
        }
        r.id = _curTune != null ? _curTune.id : -1;
        r.rep = _curRep;
        r.ver = BuildStamp.Version; // 版本戳：核对每轮结果是哪次编译跑出来的
        r.tune = _curTune;
        _batchResults.Add(r);
        AppendResult(r);
        _doneRounds++;
        Debug.Log(string.Format("[QxxsTuner] 轮 {0} 结束：组#{1} rep{2} outcome={3} {4:F1}s score={5} maxDev={6:F2} end=({7:F1},{8:F1})",
            _doneRounds, r.id, r.rep, r.outcome, r.seconds, r.finalScore, r.maxDev, r.endX, r.endZ));

        Time.timeScale = 1f;

        int repeat = Mathf.Max(1, _batch.repeat);
        if (_doneRounds >= _batch.candidates.Length * repeat)
        {
            FinishBatch();
            return;
        }
        _sceneReloaded = false;
        _state = BatchState.WaitScene;
        _waitTimer = 1.0f;
        SceneManager.LoadScene("Exam Mode");
    }

    private void UpdateWaitScene()
    {
        if (!_sceneReloaded) return;
        _waitTimer -= Time.unscaledDeltaTime;
        if (_waitTimer <= 0f)
        {
            _state = BatchState.WaitDriver;
            _waitTimer = 0.1f;
        }
    }

    private void FinishBatch()
    {
        _state = BatchState.Idle;
        Time.timeScale = 1f;
        DemoDriver driver = FindObjectOfType<DemoDriver>();
        if (driver != null) driver.tuningMode = false;

        int pass = 0;
        QxxsRoundResult best = null;
        for (int i = 0; i < _batchResults.Count; i++)
        {
            QxxsRoundResult r = _batchResults[i];
            if (r.outcome == "pass")
            {
                pass++;
                if (best == null || r.maxDev < best.maxDev || (Mathf.Abs(r.maxDev - best.maxDev) < 0.01f && r.seconds < best.seconds)) best = r;
            }
        }
        string summary = best != null
            ? string.Format("调参完成：{0}/{1} 轮合格，最优组#{2}（maxDev={3:F2}m 用时{4:F1}s）。运行 python Tuning/tune_qxxs.py best 导出最优参数",
                pass, _batchResults.Count, best.id, best.maxDev, best.seconds)
            : "调参完成：没有合格轮。放宽候选范围（改 qxxs_tuning.json）后按 T 重跑（先清空 qxxs_results.jsonl）";
        if (driver != null) driver.ShowExternalCaption(summary, 0f);
        Debug.Log("[QxxsTuner] " + summary);
        WriteDoneMarker("批次完成：" + summary);
    }

    private void AbortBatch(string message)
    {
        _state = BatchState.Idle;
        Time.timeScale = 1f;
        DemoDriver driver = FindObjectOfType<DemoDriver>();
        if (driver != null) driver.tuningMode = false;
        if (driver != null) driver.ShowExternalCaption(message, 8f);
        Debug.Log("[QxxsTuner] " + message);
        WriteDoneMarker("批次中止：" + message);
    }

    private void WriteDoneMarker(string reason)
    {
        if (!_autoMode) return;
        try
        {
            File.WriteAllText(Path.Combine(TuningDir, "qxxs_autostart.done"), reason);
        }
        catch (Exception e) { Debug.LogWarning("[QxxsTuner] 写 done 标记失败：" + e.Message); }
    }

    private static QxxsTuneBatch LoadBatch()
    {
        try
        {
            string path = Path.Combine(TuningDir, "qxxs_tuning.json");
            if (!File.Exists(path)) return null;
            return JsonUtility.FromJson<QxxsTuneBatch>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            Debug.LogWarning("[QxxsTuner] 读取 qxxs_tuning.json 失败：" + e.Message);
            return null;
        }
    }

    private static int CountResultLines()
    {
        try
        {
            string path = Path.Combine(TuningDir, "qxxs_results.jsonl");
            if (!File.Exists(path)) return 0;
            int n = 0;
            using (StreamReader sr = new StreamReader(path))
            {
                while (sr.ReadLine() != null) n++;
            }
            return n;
        }
        catch (Exception) { return 0; }
    }

    private static void AppendResult(QxxsRoundResult r)
    {
        try
        {
            Directory.CreateDirectory(TuningDir);
            using (StreamWriter sw = new StreamWriter(Path.Combine(TuningDir, "qxxs_results.jsonl"), true))
            {
                sw.WriteLine(JsonUtility.ToJson(r));
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[QxxsTuner] 写入结果失败：" + e.Message);
        }
    }

    private void ShowCaption(string text, float duration)
    {
        DemoDriver driver = FindObjectOfType<DemoDriver>();
        if (driver != null) driver.ShowExternalCaption(text, duration);
        else Debug.Log("[QxxsTuner] " + text);
    }
}

/// <summary>一组曲线行驶候选参数（JsonUtility 反序列化；缺省字段用这里的初始值）</summary>
[Serializable]
public class QxxsTune
{
    public int id = 0;                  // 组编号（分析结果时对号）
    public string note = "";            // 备注（可选）

    // ---- 纯跟踪控制参数 ----
    public float lookForward = 3.0f;    // 前瞻距离（米）；太大在弯里会提前切内侧
    public float steerGain = 2.4f;      // 转向增益（角度误差 → 归一化转向）
    public float steerSpeedFwd = 2.4f;  // 方向盘转动速率（归一化/秒）
    public float arriveDist = 2.2f;     // 路径点到达判定距离（米）
    public float throttleMax = 0.8f;    // 油门上限

    // ---- 教学要点参数 ----
    public float outerBias = 0.4f;      // 「左转靠右、右转靠左」贴外偏移（米；0=纯中心线对照）

    // ---- 分段目标车速（KM/H）----
    public float vApproach = 8f;        // 入口直道
    public float vCurve = 6.5f;         // 弯中（不得停车，也别太快）
    public float vExit = 8f;            // 出口直道

    public float maxRoundSeconds = 90f; // 单轮兜底超时（秒；全程约 84m，留足余量）
}

/// <summary>批次配置：Tuning/qxxs_tuning.json 的根结构</summary>
[Serializable]
public class QxxsTuneBatch
{
    public int repeat = 1;              // 每组候选重复轮数（滤除偶发物理抖动）
    public float timeScale = 1.0f;      // 调参轮加速倍率
    public QxxsTune[] candidates = new QxxsTune[0];
}

/// <summary>单轮结果（JSONL 每行一个；trace 为 0.25s 采样的 [x,z,x,z,...]）</summary>
[Serializable]
public class QxxsRoundResult
{
    public int id;
    public int rep;
    public string outcome;              // pass / deduct / settlement / timeout / stuck / no-start / watchdog
    public float seconds;
    public int finalScore;
    public float endDist;
    public float maxDev;
    public float endX;
    public float endZ;
    public float endYaw;
    public float[] trace;
    public string ver;                  // 代码版本戳（BuildStamp），确认结果出自哪次编译
    public QxxsTune tune;
}
