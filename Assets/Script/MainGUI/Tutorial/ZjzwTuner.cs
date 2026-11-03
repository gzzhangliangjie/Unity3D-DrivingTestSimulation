using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 【直角转弯自动调参器】与 DemoDriver 配套使用，把"改代码 → 重编译 → 演示被打断"
/// 的调参循环改成：参数全部走外部 JSON 热加载 + 自动连续重试，全程只开一次工程。
///
/// 用法：
/// 1. （可选）运行 `python Tuning/tune_zjzw.py gen` 生成候选参数到 Tuning/zjzw_tuning.json；
///    也可以直接手改该 JSON（在 Assets 外，Unity 不会导入、不会触发重编译）
/// 2. 打开工程 → 考试模式 → 直角转弯单项练习 → 按 T 开始批量调参
/// 3. 每轮自动执行：重读 JSON（外部改文件下一轮立即生效）→ 注入参数 → 自动从起点
///    演示 → 结果（含轨迹采样）追加写入 Tuning/zjzw_results.jsonl → 自动重载 Exam Mode
///    （分数、压线墙、项目计时器随场景全部复位）→ 下一轮，直到候选 × repeat 跑完
/// 4. 结束后运行 `python Tuning/tune_zjzw.py best` 分析结果并导出最优参数到
///    Tuning/zjzw_best.json；`python Tuning/tune_zjzw.py plot` 画轨迹对比图
///
/// 设计要点：
/// - 不改任何 .cs 就能调参：所有可调参数在 Tuning/zjzw_tuning.json 里热加载，
///   避免脚本改动触发域重载打断正在跑的演示轮
/// - 每轮结束重载场景而不是原地复位：压线墙被撞后已销毁、分数/结算面板/项目计时器
///   只有重载场景才能 100% 复位（SelectPass 是跨场景单例，sites 保持直角转弯）
/// - 失败判定用真实考试反馈：扣 100 分（压线/中途停车/超时）→ MyGameManager 结算
///   暂停 → 本轮记失败；不依赖任何旁路判定
/// </summary>
public class ZjzwTuner : MonoBehaviour
{
    private enum BatchState { Idle, WaitDriver, Running, WaitScene }

    private BatchState _state = BatchState.Idle;
    private int _doneRounds;        // 已完成轮数（域重载后从 results 文件行数恢复）
    private ZjzwTuneBatch _batch;   // 本轮使用的批次配置（每轮开始热重载）
    private ZjzwTune _curTune;      // 当前候选
    private int _curRep;            // 当前候选第几次重复（1 起）
    private bool _sceneReloaded;    // LoadScene 完成标志
    private float _waitTimer;       // 场景就绪等待计时
    private float _roundStartTime;  // 本轮开始时刻（兜底超时用）
    private float _driverWaitStart = -1f; // 找不到 DemoDriver 的等待起点（超时中止）
    private DemoDriver _driver;           // 当前场景的演示组件缓存（场景重载后自动失效重查）
    private bool _autoMode;               // 全自动模式（Tuning/zjzw_autostart.flag 存在时）
    private bool _autoStarted;            // 自动模式已发起（防重复）
    private readonly List<ZjzwRoundResult> _batchResults = new List<ZjzwRoundResult>();

    /// <summary>取当前场景的 DemoDriver（带缓存；场景重载后旧引用变伪 null 会自动重查）</summary>
    private DemoDriver GetDriver()
    {
        if (_driver != null) return _driver;
        _driver = FindObjectOfType<DemoDriver>();
        return _driver;
    }

    /// <summary>调参文件目录：项目根/Tuning（Application.dataPath = 项目根/Assets）</summary>
    private static string TuningDir
    {
        get { return Path.Combine(Application.dataPath, "..", "Tuning").Replace('\\', '/'); }
    }

    // ============================================================
    //  常驻安装（域重载后自动重建，进度从结果文件恢复）
    // ============================================================
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        if (FindObjectOfType<ZjzwTuner>() != null) return;
        GameObject go = new GameObject("ZjzwTuner");
        DontDestroyOnLoad(go);
        go.AddComponent<ZjzwTuner>();
        SceneManager.sceneLoaded += OnSceneLoadedStatic;
    }

    private void Awake()
    {
        // 自动模式开关 = 项目根/Tuning/zjzw_autostart.flag 文件存在（外部脚本控制）
        try { _autoMode = File.Exists(Path.Combine(TuningDir, "zjzw_autostart.flag")); }
        catch { _autoMode = false; }
        if (_autoMode) Debug.Log("[ZjzwTuner] 自动模式已启用（检测到 autostart.flag）");
    }

    private static void OnSceneLoadedStatic(Scene scene, LoadSceneMode mode)
    {
        ZjzwTuner t = FindObjectOfType<ZjzwTuner>();
        if (t != null) t.OnSceneLoaded(scene);
    }

    private void OnSceneLoaded(Scene scene)
    {
        if (scene.name == "Exam Mode" && _state == BatchState.WaitScene)
        {
            _sceneReloaded = true;
            _waitTimer = 1.0f; // 等场景组件 Start/EnsureUI 跑完再注入
        }
    }

    // ============================================================
    //  主循环
    // ============================================================
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

    /// <summary>
    /// 全自动模式入口：补齐 SelectPass(sites=Zjzw) → 加载 Exam Mode → 开批次。
    /// 等价于人工流程「主菜单选直角转弯 → 进考试模式 → 按 T」，全程无按键。
    /// </summary>
    private void AutoStart()
    {
        Application.runInBackground = true; // 无人值守：切出焦点也要继续跑
        SelectPass sp = FindObjectOfType<SelectPass>();
        if (sp == null)
        {
            GameObject go = new GameObject("SelectPass(ZjzwTuner)");
            DontDestroyOnLoad(go);
            sp = go.AddComponent<SelectPass>();
        }
        sp.sites = SelectPass.Sites.Zjzw;

        // 无论当前在哪个场景都重载 Exam Mode：
        // 若直接打开的就是 Exam Mode（无 SelectPass），MyGameManager.Start 已经跑过、
        // 车没摆到 Zjzw 起点，必须重载一次让摆位逻辑重跑
        Debug.Log("[ZjzwTuner] 自动模式：加载 Exam Mode（sites=Zjzw）");
        SceneManager.LoadScene("Exam Mode");
        StartBatch();
    }

    private void StartBatch()
    {
        _batch = LoadBatch();
        if (_batch == null || _batch.candidates == null || _batch.candidates.Length == 0)
        {
            ShowCaption("调参启动失败：Tuning/zjzw_tuning.json 无候选（先运行 python Tuning/tune_zjzw.py gen）", 8f);
            WriteDoneMarker("启动失败：无候选参数");
            return;
        }
        _doneRounds = CountResultLines(); // 域重载/中断后从文件行数恢复进度
        int total = _batch.candidates.Length * Mathf.Max(1, _batch.repeat);
        if (_doneRounds >= total)
        {
            ShowCaption("调参批次已全部完成（" + total + " 轮）。删掉/清空 zjzw_results.jsonl 可重跑", 8f);
            WriteDoneMarker("批次此前已完成：" + total + " 轮");
            return;
        }
        _batchResults.Clear();
        _state = BatchState.WaitDriver;
        _waitTimer = 0.2f;
        Debug.Log("[ZjzwTuner] 批次开始：候选 " + _batch.candidates.Length + " 组 × " + _batch.repeat + " 次，已完成 " + _doneRounds + " 轮（文件恢复）");
    }

    private void UpdateWaitDriver()
    {
        _waitTimer -= Time.unscaledDeltaTime;
        if (_waitTimer > 0f) return;

        // 每轮热重载 JSON：外部改参数文件后无需任何重启，下一轮生效
        ZjzwTuneBatch fresh = LoadBatch();
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
            // DemoDriver 由看门狗在 0.5 秒内装上；持续等不到说明当前不是单项练习场景
            if (_driverWaitStart < 0f) _driverWaitStart = Time.time;
            _waitTimer = 0.5f;
            if (Time.time - _driverWaitStart > 10f)
            {
                AbortBatch("调参中止：10 秒内找不到演示组件（确认在直角转弯单项练习场景中）");
            }
            return;
        }
        _driverWaitStart = -1f;

        Time.timeScale = _batch.timeScale > 0.05f ? _batch.timeScale : 1f;
        string caption = string.Format(
            "调参 {0}/{1}　组#{2}（第{3}次）　look={4:F1} R={5:F2} cornerX={6:F1} gain={7:F1} v={8:F0}/{9:F0}",
            _doneRounds + 1, _batch.candidates.Length * repeat, _curTune.id, _curRep,
            _curTune.lookForward, _curTune.radius, _curTune.cornerX, _curTune.steerGain, _curTune.vApproach, _curTune.vCorner);
        _roundStartTime = Time.time;
        driver.StartTuningRound(_curTune, caption);
        _state = BatchState.Running;
    }

    private void UpdateRunning()
    {
        DemoDriver driver = GetDriver();
        if (driver == null)
        {
            // 场景被外部重载（非本工具发起）：回到等待状态，不打断批次
            _sceneReloaded = false;
            _state = BatchState.WaitScene;
            _waitTimer = 1.0f;
            return;
        }

        // 兜底超时：DemoDriver 异常未回报（正常失败路径都在 DemoDriver 内部兜住）
        float limit = (_curTune != null ? _curTune.maxRoundSeconds : 45f) + 20f;
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

    /// <summary>收尾一轮：写结果文件 → 重载场景进下一轮（或结束批次）</summary>
    private void EndRound(DemoDriver driver, string forceOutcome)
    {
        ZjzwRoundResult r = driver.lastResult;
        if (forceOutcome != null)
        {
            r = new ZjzwRoundResult { outcome = forceOutcome, seconds = Time.time - _roundStartTime, finalScore = Car_Information.GetScore() };
        }
        r.id = _curTune != null ? _curTune.id : -1;
        r.rep = _curRep;
        r.tune = _curTune;
        _batchResults.Add(r);
        AppendResult(r);
        _doneRounds++;
        Debug.Log(string.Format("[ZjzwTuner] 轮 {0} 结束：组#{1} rep{2} outcome={3} {4:F1}s score={5} maxDev={6:F2} end=({7:F1},{8:F1})",
            _doneRounds, r.id, r.rep, r.outcome, r.seconds, r.finalScore, r.maxDev, r.endX, r.endZ));

        Time.timeScale = 1f; // 重载场景前必须恢复（结算暂停可能把它置 0）

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

        // 简单汇总（完整分析/导出最优参数用 python Tuning/tune_zjzw.py best）
        int pass = 0;
        ZjzwRoundResult best = null;
        for (int i = 0; i < _batchResults.Count; i++)
        {
            ZjzwRoundResult r = _batchResults[i];
            if (r.outcome == "pass")
            {
                pass++;
                if (best == null || r.maxDev < best.maxDev || (Mathf.Abs(r.maxDev - best.maxDev) < 0.01f && r.seconds < best.seconds)) best = r;
            }
        }
        string summary = best != null
            ? string.Format("调参完成：{0}/{1} 轮合格，最优组#{2}（maxDev={3:F2}m 用时{4:F1}s）。运行 python Tuning/tune_zjzw.py best 导出最优参数",
                pass, _batchResults.Count, best.id, best.maxDev, best.seconds)
            : "调参完成：没有合格轮。放宽候选范围（改 zjzw_tuning.json）后按 T 重跑（先清空 zjzw_results.jsonl）";
        if (driver != null) driver.ShowExternalCaption(summary, 0f);
        Debug.Log("[ZjzwTuner] " + summary);
        WriteDoneMarker("批次完成：" + summary);
    }

    private void AbortBatch(string message)
    {
        _state = BatchState.Idle;
        Time.timeScale = 1f;
        DemoDriver driver = FindObjectOfType<DemoDriver>();
        if (driver != null) driver.tuningMode = false;
        if (driver != null) driver.ShowExternalCaption(message, 8f);
        Debug.Log("[ZjzwTuner] " + message);
        WriteDoneMarker("批次中止：" + message);
    }

    /// <summary>自动模式写完成标记（ZjzwAutoRunner 据此停 Play 并退出 Unity）</summary>
    private void WriteDoneMarker(string reason)
    {
        if (!_autoMode) return;
        try
        {
            File.WriteAllText(Path.Combine(TuningDir, "zjzw_autostart.done"), reason);
        }
        catch (Exception e) { Debug.LogWarning("[ZjzwTuner] 写 done 标记失败：" + e.Message); }
    }

    // ============================================================
    //  文件读写（JSON 热加载 + JSONL 结果追加）
    // ============================================================
    private static ZjzwTuneBatch LoadBatch()
    {
        try
        {
            string path = Path.Combine(TuningDir, "zjzw_tuning.json");
            if (!File.Exists(path)) return null;
            ZjzwTuneBatch b = JsonUtility.FromJson<ZjzwTuneBatch>(File.ReadAllText(path));
            return b;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ZjzwTuner] 读取 zjzw_tuning.json 失败：" + e.Message);
            return null;
        }
    }

    private static int CountResultLines()
    {
        try
        {
            string path = Path.Combine(TuningDir, "zjzw_results.jsonl");
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

    private static void AppendResult(ZjzwRoundResult r)
    {
        try
        {
            Directory.CreateDirectory(TuningDir);
            using (StreamWriter sw = new StreamWriter(Path.Combine(TuningDir, "zjzw_results.jsonl"), true))
            {
                sw.WriteLine(JsonUtility.ToJson(r));
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ZjzwTuner] 写入结果失败：" + e.Message);
        }
    }

    private void ShowCaption(string text, float duration)
    {
        DemoDriver driver = FindObjectOfType<DemoDriver>();
        if (driver != null) driver.ShowExternalCaption(text, duration);
        else Debug.Log("[ZjzwTuner] " + text);
    }
}

/// <summary>一组直角转弯候选参数（JsonUtility 反序列化；缺省字段用这里的初始值）</summary>
[Serializable]
public class ZjzwTune
{
    public int id = 0;                  // 组编号（分析结果时对号）
    public string note = "";            // 备注（可选）

    // ---- 路径几何（世界坐标 X/Z，见 DemoDriver.BuildZjzwPath 注释）----
    public float entryZ = -33.5f;       // 入口直线 z（车道中线）
    public float cornerX = 33.0f;       // 弧线起点 x（到转角横线才开始打方向）
    public float radius = 3.33f;        // 圆弧半径（不可小于 3.33=轴距2.6/tan38°，否则物理打不出）
    public float arcStep = 30f;         // 圆弧步进角（度，越小弧线越顺滑）
    public float exitEndZ = -19.2f;     // 出口终点 z（End 触发器前刹停）

    // ---- 纯跟踪控制参数 ----
    public float lookForward = 3.0f;    // 前瞻距离（米）；太大会提前切弯内侧
    public float steerGain = 2.4f;      // 转向增益（角度误差 → 归一化转向）
    public float steerSpeedFwd = 2.4f;  // 方向盘转动速率（归一化/秒）
    public float arriveDist = 2.2f;     // 路径点到达判定距离（米）
    public float throttleMax = 0.8f;    // 油门上限

    // ---- 转角打满控制（驾校打法：打死→保持→转正回正）----
    // R=3.33 是满舵最小转弯半径（零余量），纯跟踪给不出满舵，转角段必须显式打死
    public bool lockArc = true;         // 启用转角打满控制（false=全程纯跟踪，对照用）
    public float lockLead = 0.8f;       // 提前打方向量：车头距转角横线多少米开始打死（米）
    public float lockSpeed = 4.0f;      // 打死过程方向盘速率（归一化/秒）
    public float lockSteer = -1.0f;     // 保持阶段转向量（-1=打死；绝对值小则弧变宽）
    public float releaseYaw = 6f;       // 车身与出口方向夹角小于该角度时回正（度）
    public bool dynamicRoute = true;    // 几何闭环路线（每帧反解半径；false=老的固定提前量方案）
    public float routeEntryOffset = 0f; // 入口段贴外侧走（米，正=向南/外线偏移，补偿满舵内切）
    public float routeTurnBias = 0f;    // 转弯落点向东留余量（米，正=少打方向、落点偏东）
    public float routeRadiusMax = 3.6f; // 转弯窗口半径上限（越小=越晚开始打方向）

    // ---- 分段目标车速（KM/H）----
    public float vApproach = 9f;        // 入口直道
    public float vCorner = 6f;          // 弯中
    public float vExit = 9f;            // 出口直道

    public float maxRoundSeconds = 45f; // 单轮兜底超时（秒；项目计时 30s 会先触发扣分）
}

/// <summary>批次配置：Tuning/zjzw_tuning.json 的根结构</summary>
[Serializable]
public class ZjzwTuneBatch
{
    public int repeat = 1;              // 每组候选重复轮数（滤除偶发物理抖动，结果里按 rep 区分）
    public float timeScale = 1.0f;      // 调参轮加速倍率（1=真实速度；1.5~2 可加速，压测后再用）
    public ZjzwTune[] candidates = new ZjzwTune[0];
}

/// <summary>单轮结果（JSONL 每行一个；trace 为 0.25s 采样的 [x,z,x,z,...]）</summary>
[Serializable]
public class ZjzwRoundResult
{
    public int id;
    public int rep;
    public string outcome;              // pass / deduct / settlement / timeout / stuck / no-start / watchdog
    public float seconds;
    public int finalScore;
    public float endDist;               // 结束位置距路径终点的距离（米）
    public float maxDev;                // 轨迹与参考路径的最大横向偏差（米）
    public float endX;
    public float endZ;
    public float endYaw;
    public float[] trace;
    public ZjzwTune tune;               // 本轮完整参数（结果自包含，分析不依赖 tuning.json）
}
