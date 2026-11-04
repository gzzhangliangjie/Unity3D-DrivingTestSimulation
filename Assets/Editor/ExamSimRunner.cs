
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 【科目二·单项模拟自动运行器】把"中断当前流程 → 重新 Play → 模拟指定项目"
/// 固化成一次可靠调用，解决两大痛点：
///
/// 1. 旧看门狗（ZjzwWatchRunner）只在"非 Play"状态响应 flag：Play 中断在哪，
///    就得手动停 Play 再手动 Play。本运行器在任何状态下都能触发：
///    Play 中 → 自动停 Play → 等域重载结束 → 重新进 Play；
/// 2. 不合格结算死锁：MyGameManager 判不合格后 timeScale=0、GamePlayingFlag=false，
///    游戏内 R 键/重开按钮走 GameContinue() 会被拦，加载界面卡死。
///    本运行器根本不走游戏内重开——直接重进 Play，场景重载即 100% 复位
///    （Car_Information.Start() 会把静态分数重置为 100）。
///
/// 触发方式（三选一，等价）：
/// - 菜单：Tools/科目二模拟/模拟：倒车入库 等六个入口（含全图考试）；
/// - flag 文件：写 Tuning/examsim_run.flag（内容=项目名，支持中英文，
///   如 zjzw / 直角转弯 / qxxs / 曲线行驶 / default / 全图考试），写完即删；
/// - 代码：ExamSimRunner.Run("Zjzw")（供其他编辑器脚本调用）。
///
/// 流程（状态存 SessionState，跨域重载不丢）：
///   收到请求 → 清理残留触发 flag（防旧调参批次劫持本次运行）→
///   Play 中则停 Play → 等编辑器空闲 → 进 Play →
///   注入/复用 SelectPass(sites=项目) → 加载 Exam Mode（让 MyGameManager.Start 重跑摆位）→
///   等 DemoDriver 装好 → 自动开演示 → 写 Tuning/examsim_run.done。
///
/// 注意：
/// - 单项模拟（本运行器）与调参批次（tune_*.py 的 autostart 流程）不要同时触发：
///   本运行器启动前会清掉 *_autostart.flag / *_editorplay.flag，防止残留批次劫持；
///   反过来，想跑调参批次就直接用 tune_*.py，不要写 examsim_run.flag。
/// - 总超时 120 秒兜底：任何一步卡住都只会报错并复位状态，绝不会把编辑器吊死。
/// </summary>
[InitializeOnLoad]
public static class ExamSimRunner
{
    private const string SessionKey = "ExamSimRunner.PendingSite";  // 待执行的项目名（跨域重载持久）
    private const string SessionStartKey = "ExamSimRunner.StartTime"; // 请求发起时刻（超时判定）
    private const string SessionLastKey = "ExamSimRunner.LastSite";   // 上次成功请求的项目（"重跑当前项目"用）
    private const string EnteredKey = "ExamSimRunner.PlayOurs";       // 当前 Play 是本运行器发起的（跨域重载持久）
    private const string WatchKey = "ExamSimRunner.WatchEnd";         // 演示结束监视：结算界面自动停 Play（跨域重载持久）
    private const string WatchMaxKey = "ExamSimRunner.WatchMax";      // 本轮监视超时秒数（全科连续路线远长于单科，需放宽）
    private const string TriggerFlag = "examsim_run.flag";
    private const string DoneFile = "examsim_run.done";
    private const double OverallTimeout = 120.0;

    /// <summary>残留触发 flag：进 Play 前清掉，防止旧调参批次劫持本次单项模拟</summary>
    private static readonly string[] StaleFlags =
    {
        "zjzw_autostart.flag", "qxxs_autostart.flag",
        "zjzw_editorplay.flag", "qxxs_editorplay.flag",
        "zjzw_rebuild.flag", "qxxs_rebuild.flag",
    };

    // 运行期子状态（域重载会丢：丢了也能自愈——SessionState 里的请求还在，重新注入一遍）
    private static bool _stopSent;   // 已发出停 Play 指令
    private static bool _entered;    // 已发出进 Play 指令
    private static int _injectStep;  // 注入子步骤：0=等运行时就绪 1=等 Exam Mode 加载 2=等 DemoDriver
    private static int _waitFrames;  // 子步骤内等待帧数
    private static bool _demoKicked; // 已触发过一次演示（失败重试只补一脚）
    private static int _demoWait;    // 触发演示后的确认等待帧数
    private static int _endFrames;   // 监视：演示状态已退出 Driving 的连续帧数
    private static double _tsZeroAt = -1; // 监视：timeScale=0（结算冻结）开始时刻，-1=未冻结

    static ExamSimRunner()
    {
        EditorApplication.update += Update;
        Debug.Log("[ExamSimRunner] 已挂载：菜单 Tools/科目二模拟，或写 Tuning/examsim_run.flag（内容=项目名）");
    }

    // ============================================================
    //  触发入口
    // ============================================================

    [MenuItem("Tools/科目二模拟/模拟：全图考试")]
    private static void MenuDefault() { Run("Default"); }

    [MenuItem("Tools/科目二模拟/模拟：倒车入库")]
    private static void MenuDcrk() { Run("Dcrk"); }

    [MenuItem("Tools/科目二模拟/模拟：侧方停车")]
    private static void MenuCftc() { Run("Cftc"); }

    [MenuItem("Tools/科目二模拟/模拟：半坡起步")]
    private static void MenuBpqb() { Run("Bpqb"); }

    [MenuItem("Tools/科目二模拟/模拟：直角转弯")]
    private static void MenuZjzw() { Run("Zjzw"); }

    [MenuItem("Tools/科目二模拟/模拟：曲线行驶")]
    private static void MenuQxxs() { Run("Qxxs"); }

    /// <summary>重跑上次模拟的项目（Play 中不合格死锁后的标准脱困动作）</summary>
    [MenuItem("Tools/科目二模拟/重跑上次项目")]
    private static void MenuRerun()
    {
        string last = SessionState.GetString(SessionLastKey, "");
        if (string.IsNullOrEmpty(last))
        {
            Debug.LogError("[ExamSimRunner] 重跑失败：本会话还没有模拟过任何项目（先点一次「模拟：xx」或写 examsim_run.flag）");
            return;
        }
        Run(last);
    }

    /// <summary>
    /// 请求模拟指定项目。任何时机调用都安全：Play 中会先中断当前流程再重进。
    /// </summary>
    /// <param name="siteName">项目名：枚举名（Dcrk/Cftc/Bpqb/Zjzw/Qxxs/Default，
    /// 大小写不敏感）或中文名（倒车入库/侧方停车/半坡起步/直角转弯/曲线行驶/全图考试）</param>
    public static void Run(string siteName)
    {
        SelectPass.Sites site;
        if (!TryParseSite(siteName, out site))
        {
            Debug.LogError("[ExamSimRunner] 无法识别项目名：" + siteName
                + "（可用：Dcrk/Cftc/Bpqb/Zjzw/Qxxs/Default 或对应中文）");
            WriteDone("fail: 无法识别项目名 " + siteName);
            return;
        }

        CleanStaleFlags(); // 请求时刻清理残留：此刻的意图是"干净的单项模拟"
        // 清掉"本会话发起"标记：当前 Play 视为残留会话，走中断重进
        SessionState.SetString(EnteredKey, "");
        SessionState.SetString(WatchKey, "");
        _endFrames = 0;
        _tsZeroAt = -1;
        SessionState.SetString(SessionKey, site.ToString());
        SessionState.SetFloat(SessionStartKey, (float)EditorApplication.timeSinceStartup);
        // 监视超时：全科（Default）连续路线约 6~8 分钟，300s 兜底会中途掐断 → 放宽到 900s
        SessionState.SetFloat(WatchMaxKey, site == SelectPass.Sites.Default ? 900f : 300f);
        _stopSent = false;
        _entered = false;
        _injectStep = 0;
        _waitFrames = 0;
        _demoKicked = false;
        _demoWait = 0;
        Debug.Log("[ExamSimRunner] 收到模拟请求：" + site + (EditorApplication.isPlaying ? "（Play 中，将先中断）" : ""));
    }

    // ============================================================
    //  主循环状态机
    // ============================================================

    private static void Update()
    {
        PollTriggerFlag();

        // 演示结束监视：本运行器发起的 Play，演示跑完/结算冻结后自动停 Play，
        // 根治"卡死在考试结束/录像界面"（原游戏判不合格后 timeScale=0 等玩家按键，无人值守即挂死）
        if (SessionState.GetString(WatchKey, "") == "1")
        {
            WatchDemoEnd();
            return;
        }

        string pending = SessionState.GetString(SessionKey, null);
        if (string.IsNullOrEmpty(pending)) return;

        // 总超时兜底：宁可报错放弃，也绝不把编辑器吊死
        double elapsed = EditorApplication.timeSinceStartup - SessionState.GetFloat(SessionStartKey, 0f);
        if (elapsed > OverallTimeout)
        {
            Finish(false, "超时（" + (int)elapsed + "s）未完成模拟启动，已放弃");
            return;
        }

        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

        if (EditorApplication.isPlaying)
        {
            // 只有"本运行器发起的 Play"才继续注入流程；否则视为残留会话（不合格死锁/
            // 旧调参劫持/域重载前的旧状态），一律中断重进。
            // 注意：进 Play 本身会域重载、静态字段全清空，所以这个标记必须存 SessionState，
            // 否则会在"进 Play → 重载 → 误判残留 → 停 Play → 再进"里死循环（已踩坑）。
            if (SessionState.GetString(EnteredKey, "") == "1")
            {
                InjectInPlayMode(pending);
                return;
            }
            if (!_stopSent)
            {
                _stopSent = true;
                Debug.Log("[ExamSimRunner] 中断当前 Play（将重新进入并模拟 " + pending + "）");
                EditorApplication.isPlaying = false;
            }
            return;
        }

        // 正在切换 Play 状态（退出中/进入中）：等切完
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;

        // 空闲：进 Play（域重载会重置 _entered，SessionState 里的请求还在，流程自动续上）
        if (!_entered)
        {
            // 边界：编辑器最后打开的场景若是 Loading，直接进 Play 会因
            // Globe.nextSceneName 为空而加载失败——先切到 Exam Mode
            if (UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().name == "Loading")
            {
                UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scene/Exam Mode.unity");
                Debug.Log("[ExamSimRunner] 当前场景为 Loading，已切换到 Exam Mode 再进 Play");
                return; // 等场景打开完成，下轮 Update 再进 Play
            }
            _entered = true;
            // 先立"本会话是我们发起的"标记再进 Play：进 Play 会域重载清空静态字段，
            // 该标记必须在重载后依然可见，重载后的 Update 才会走注入而不是停 Play
            SessionState.SetString(EnteredKey, "1");
            Debug.Log("[ExamSimRunner] 进入 Play 模式（sites=" + pending + "）");
            EditorApplication.isPlaying = true;
        }
    }

    /// <summary>Play 中注入：补 SelectPass → 加载 Exam Mode → 自动开演示</summary>
    private static void InjectInPlayMode(string siteName)
    {
        SelectPass.Sites site;
        if (!TryParseSite(siteName, out site))
        {
            Finish(false, "无法解析项目名：" + siteName);
            return;
        }

        // 刚请求进 Play 还没切过去（isPlaying 短暂为 true 的边界）：等 EnteredPlayMode
        Scene s = SceneManager.GetActiveScene();
        if (!s.isLoaded) return;
        _waitFrames++;

        switch (_injectStep)
        {
            case 0:
                // 等运行时脚本初始化（给 5 帧），再补 SelectPass 并加载 Exam Mode。
                // 无论当前打开的是哪个场景都重载 Exam Mode：若直接打开的就是 Exam Mode，
                // MyGameManager.Start 已跑过且没有 SelectPass，车不在项目起点，
                // 必须注入后重载一次让摆位逻辑重跑（与 Tuner.AutoStart 同理）。
                if (_waitFrames < 5) return;
                Application.runInBackground = true; // 无人值守：切出焦点也要继续跑
                SelectPass sp = UnityEngine.Object.FindObjectOfType<SelectPass>();
                if (sp == null)
                {
                    GameObject go = new GameObject("SelectPass(ExamSimRunner)");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    sp = go.AddComponent<SelectPass>();
                }
                sp.sites = site;
                _injectStep = 1;
                _waitFrames = 0;
                SceneManager.LoadScene("Exam Mode");
                Debug.Log("[ExamSimRunner] 已注入 sites=" + site + "，加载 Exam Mode");
                return;

            case 1:
                // 等 Exam Mode 场景加载完且组件 Start 跑完
                if (s.name != "Exam Mode" || _waitFrames < 10) return;
                // 全科（Default）现已支持自动演示：与其它科目一致，等 DemoDriver 装好后自动开演示
                _injectStep = 2;
                _waitFrames = 0;
                return;

            case 2:
            {
                // DemoDriver 由它自己的看门狗在场景加载后 0.5s 内自装，轮询等待
                DemoDriver driver = UnityEngine.Object.FindObjectOfType<DemoDriver>();
                if (driver == null)
                {
                    if (_waitFrames > 60 * 15) Finish(false, "等待 DemoDriver 安装超时（15s）");
                    return;
                }

                if (!_demoKicked)
                {
                    _demoKicked = true;
                    driver.ToggleDemo();
                    Debug.Log("[ExamSimRunner] 已触发演示（" + site + "）");
                    _demoWait = 0;
                    return;
                }

                // 确认演示真的跑起来了：读私有 _state（DemoState.Driving），
                // 没起来（如起点对象缺失导致 StartDemo 失败）补一脚，再不行报错
                _demoWait++;
                if (IsDemoDriving(driver)) { BeginWatch(site); return; }
                if (_demoWait == 90) { driver.ToggleDemo(); return; } // 约 1.5s 后补一脚
                if (_demoWait > 60 * 6) Finish(false, "演示触发后未进入行驶状态（" + site + "），检查 Console");
                return;
            }
        }
    }

    private static bool IsDemoDriving(DemoDriver driver)
    {
        try
        {
            FieldInfo f = typeof(DemoDriver).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return true; // 反射失败就不再为难用户，按已开始处理
            object v = f.GetValue(driver);
            return v != null && v.ToString() == "Driving";
        }
        catch { return true; }
    }

    // ============================================================
    //  演示结束监视：演示跑完或考试结算冻结 → 自动停 Play
    // ============================================================

    /// <summary>演示已开跑：记下上次项目、写 done，转入结束监视（Play 保持运行）</summary>
    private static void BeginWatch(SelectPass.Sites site)
    {
        SessionState.SetString(SessionLastKey, site.ToString());
        SessionState.SetString(SessionKey, "");
        SessionState.SetString(WatchKey, "1");
        _endFrames = 0;
        _tsZeroAt = -1;
        Debug.Log("[ExamSimRunner] 演示已开始：" + site + "（转入结束监视，考试结算后自动停 Play）");
        WriteDone("ok: 演示已开始：" + site);
    }

    /// <summary>
    /// 结束判定（任一满足即停 Play）：
    /// 1. DemoDriver 状态退出 Driving（正常跑完 → Finished）持续约 2 秒；
    /// 2. timeScale=0 持续 3 秒（不合格结算冻结：游戏挂起等玩家按键，无人值守即卡死）；
    /// 3. 监视超时兜底（单科 300 秒 / 全科 Default 900 秒，Run 时按项目写入 SessionState）。
    /// </summary>
    private static void WatchDemoEnd()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (!EditorApplication.isPlaying)
        {
            // Play 被外部停了：监视结束，复位等待下一次请求
            SessionState.SetString(WatchKey, "");
            Debug.Log("[ExamSimRunner] Play 已被外部停止，结束监视");
            return;
        }

        double now = EditorApplication.timeSinceStartup;
        bool ended = false;
        string why = "";

        DemoDriver d = UnityEngine.Object.FindObjectOfType<DemoDriver>();
        if (d == null)
        {
            ended = true;
            why = "DemoDriver 丢失";
        }
        else
        {
            object st = null;
            try
            {
                FieldInfo f = typeof(DemoDriver).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) st = f.GetValue(d);
            }
            catch { }
            bool driving = st != null && st.ToString() == "Driving";
            if (!driving)
            {
                _endFrames++;
                if (_endFrames > 120) { ended = true; why = "演示已结束（状态 " + st + "）"; }
            }
            else
            {
                _endFrames = 0;
            }

            // 结算冻结：timeScale=0 持续 3 秒（放过加载/过场的瞬时冻结）
            if (!ended && Time.timeScale < 0.1f)
            {
                if (_tsZeroAt < 0) _tsZeroAt = now;
                if (now - _tsZeroAt > 3.0) { ended = true; why = "考试结算冻结（timeScale=0）"; }
            }
            else
            {
                _tsZeroAt = -1;
            }
        }

        if (!ended && now - SessionState.GetFloat(SessionStartKey, 0f) > SessionState.GetFloat(WatchMaxKey, 300f))
        {
            ended = true;
            why = "监视超时 " + (int)SessionState.GetFloat(WatchMaxKey, 300f) + "s 兜底";
        }

        if (ended)
        {
            SessionState.SetString(WatchKey, "");
            Debug.Log("[ExamSimRunner] " + why + "，自动停止 Play");
            WriteDone("ok: " + why + "，自动停 Play");
            EditorApplication.isPlaying = false;
        }
    }

    private static void Finish(bool ok, string msg)
    {
        if (ok) SessionState.SetString(SessionLastKey, SessionState.GetString(SessionKey, ""));
        SessionState.SetString(SessionKey, ""); // 清空待执行请求（Get 判 IsNullOrEmpty）
        _stopSent = false;
        _entered = false;
        _injectStep = 0;
        _waitFrames = 0;
        _demoKicked = false;
        _demoWait = 0;
        if (ok)
        {
            Debug.Log("[ExamSimRunner] " + msg);
        }
        else
        {
            Debug.LogError("[ExamSimRunner] " + msg);
        }
        WriteDone((ok ? "ok: " : "fail: ") + msg);
    }

    // ============================================================
    //  flag 文件通道 / 工具
    // ============================================================

    /// <summary>检测 Tuning/examsim_run.flag（内容=项目名），写完即删。任何 Play 状态都响应。</summary>
    private static void PollTriggerFlag()
    {
        string p = Path.Combine(TuningDir, TriggerFlag);
        if (!File.Exists(p)) return;
        string content = "";
        try { content = File.ReadAllText(p).Trim(); } catch { }
        try { File.Delete(p); } catch { }
        Run(content);
    }

    /// <summary>清掉旧调参/看门狗的残留触发 flag 与上次的 done 标记</summary>
    private static void CleanStaleFlags()
    {
        for (int i = 0; i < StaleFlags.Length; i++)
        {
            string p = Path.Combine(TuningDir, StaleFlags[i]);
            if (File.Exists(p))
            {
                try { File.Delete(p); Debug.Log("[ExamSimRunner] 清理残留 flag：" + StaleFlags[i]); } catch { }
            }
        }
        string done = Path.Combine(TuningDir, DoneFile);
        try { if (File.Exists(done)) File.Delete(done); } catch { }
    }

    private static void WriteDone(string content)
    {
        try { File.WriteAllText(Path.Combine(TuningDir, DoneFile), content + " @ " + DateTime.Now.ToString("HH:mm:ss")); } catch { }
    }

    private static string TuningDir
    {
        get { return Path.Combine(Application.dataPath, "..", "Tuning").Replace('\\', '/'); }
    }

    /// <summary>解析项目名：枚举名（大小写不敏感）或中文别名</summary>
    private static bool TryParseSite(string name, out SelectPass.Sites site)
    {
        site = SelectPass.Sites.Default;
        if (string.IsNullOrEmpty(name)) return false;
        name = name.Trim();
        switch (name)
        {
            case "倒车入库": name = "Dcrk"; break;
            case "侧方停车": name = "Cftc"; break;
            case "半坡起步": name = "Bpqb"; break;
            case "直角转弯": name = "Zjzw"; break;
            case "曲线行驶": name = "Qxxs"; break;
            case "全图考试":
            case "全图":
            case "考试": name = "Default"; break;
        }
        try { site = (SelectPass.Sites)Enum.Parse(typeof(SelectPass.Sites), name, true); return true; }
        catch { return false; }
    }
}
