using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 【实操演示控制器】电脑自动驾驶"玩家的车"，按真实考试流程完整跑一遍标准路线。
/// 支持全部五个项目：倒车入库 / 侧方停车 / 半坡起步 / 直角转弯 / 曲线行驶。
///
/// 核心原则——与考试流程完全对齐，不搞特殊：
/// 1. 演示期间考试判定全部正常工作：五个检测器（Det_*）、压线墙（Wall）、
///    出口空气墙、结束触发器（End / Victory）、进度播报（JinDu）、计分结算，
///    一个都不禁用。演示车走合格路线就不压线、不撞墙，走完正常弹出"成绩合格"；
/// 2. 若演示路线有偏差被扣分，结算照常触发——这正是路线需要调校的真实反馈；
/// 3. 唯一接管：演示中暂停外接方向盘/键盘输入（HardwareInputController），
///    相当于教练代开时学员不碰方向盘，演示停止后立刻恢复。
///
/// 操作：
/// - G 键 或 右侧【演示】按钮：开始 / 停止演示
/// - 演示中按 V 切换视角（沿用游戏原有功能）
/// - R 键（用过演示后生效）：重新开始本项练习
///
/// 路径点调校：坐标取自 Exam Mode.unity 场景解析，倒车入库/侧方停车/半坡起步
/// 三个项目的路径为按库位坐标推算的估算值，实测有偏差时直接改本文件
/// BuildDcrkPath() / BuildCftcPath() / BuildBpqbPath() 里的坐标（X/Z 世界坐标）。
/// </summary>
public class DemoDriver : MonoBehaviour
{
    public enum DemoState { Idle, Driving, Finished }

    /// <summary>行驶阶段：正常跟随 / 刹车停稳 / 换档等待 / 点位停车保持</summary>
    private enum DrivePhase { Run, BrakeToStop, ShiftGear, Hold }

    // ---- 路径跟踪参数（原为 const；调参模式下可被 ZjzwTuner 从外部 JSON 热注入）----
    private float _arriveDist = 2.2f;    // 到达路径点的判定距离（米）
    private float _lookForward = 3.0f;   // 前进纯跟踪前瞻距离（米）；太大会提前切弯内侧
    private float _lookReverse = 2.6f;   // 倒车纯跟踪前瞻距离（倒车精度要求高）
    private float _steerGain = 2.4f;     // 纯跟踪转向增益（角度误差 → 归一化转向）
    private float _steerSpeedFwd = 2.4f; // 前进时方向盘转动速率（归一化/秒）
    [System.NonSerialized] public float _crossGain = 1.1f; // Stanley 横向误差增益（偏移米数 → 额外打角），调参器可覆盖
    private float _throttleMax = 0.8f;   // 油门上限（半坡爬坡无力时调大）

    // ---- 路径跟踪增强（参考 Unity Standard Assets CarAIControl / WaypointProgressTracker）----
    private float _lookSpeedGain = 0.5f;  // 速度自适应前瞻增益（米前瞻 / (米/秒)）：直道随车速拉长前瞻压画龙
    private float _lookCurveGate = 0.30f; // 曲率前馈舵量门槛：|ff| 低于它才放行自适应前瞻（弯道保持基础前瞻防切内侧）
    private float _aheadScanDist = 10f;   // 急弯限速的前方扫描距离（米）
    private float _capAngleMin = 20f;     // 前方累计转角低于该值不限速（度）
    private float _capAngleMax = 80f;     // 前方累计转角达到该值时限到最低速（度）
    private float _capSpeedHigh = 12f;    // 转角限速上限（高于所有分段车速 = 缓弯/直道不干预）
    private float _capSpeedLow = 7f;     // 转角限速下限：必须高于恒速目标 6.5，否则会把弯中速度钳回 6 → 内切撞墙（2026-11-04 教训）

    // ---- 直角转弯·转角打满控制（驾校打法：车头到横线打死 → 保持 → 车身转正后回正）----
    // 为什么不用纯跟踪过弯：R=3.33 = 轴距2.6/tan38° 正好是满舵最小转弯半径，零余量；
    // 纯跟踪进弯欠舵（前瞻点在弧线初段几乎正对车头，指令远打不到底）、出弯提前回舵，
    // 两个方向都会出弧线压墙。转角段必须显式打死，跟科目二教学动作一致。
    private bool _zjzwLockArc = true;    // 是否启用转角打满控制（false = 全程纯跟踪，对照用）
    private float _zjzwCornerX = 33.0f;  // 转角横线 x（提前量基准）
    private float _zjzwLockLead = 0.8f;  // 提前打方向量：车头距横线多少米开始打死（米）
    private float _zjzwLockSpeed = 4.0f; // 打死过程方向盘速率（归一化/秒，要快）
    private float _zjzwLockSteer = -1.0f;// 保持阶段转向量（-1=打死；绝对值小则弧变宽）
    private float _zjzwReleaseYaw = 6f;  // 车身与出口方向夹角小于该值时回正（度）
    private bool _inLock;                // 当前处于打死保持阶段

    // ---- 直角转弯·几何闭环路线（替代固定提前量；路线不对就每帧重算）----
    // 为什么：固定 "到 x=cornerX-0.8 就打死" 实测早了 0.87m，弧心西移 1m，
    // 车身左前角蹭内侧直角墙（38 组参数全在 (32.9,-32.9) 扣 100 分）。
    // 现在每帧用 R=(exitX-x)/(1-cos yaw) 反解所需半径，偏离会在下一帧自动拉回。
    private bool _zjzwDynamic = true;    // true=几何闭环；false=老的固定提前量方案（对照用）
    private ZjzwRouteCfg _routeCfg = new ZjzwRouteCfg();
    private float _routeSpeed = -1f;     // 几何闭环给的目标车速（<0 表示本帧没接管）
    private RoutePhase _routePhase;      // 当前阶段（遥测/字幕用）
    private int _routeRecoveries;        // 触发过多少次路线调整（本轮）
    private float _routeLogTimer;        // 路线遥测节流
    private string _routeNote = "";      // 最近一次调整原因

    // ---------------- 引用 ----------------
    private DrivingKernel _kernel;
    private GearsDangWei_Control _gears;
    private Car_HandleBrake _handleBrake;
    private TurnLight _turnLight;
    private SelectPass.Sites _sites;

    // ---------------- 路径数据 ----------------
    private Vector3[] _wps;        // 路径点（x,0,z），按车辆实际轨迹顺序排列
    private float[] _speeds;       // 每个路径点的目标车速（KM/H）
    private bool[] _rev;           // _rev[i]：从 wps[i] 驶向 wps[i+1] 这一段是否倒车（R 档）
    private float[] _hold;         // 到达该点后刹停保持的秒数（看点/换档/定点）
    private bool[] _handbrake;     // 到达该点停车时是否拉手刹（半坡定点用）

    private int _wpIndex;
    private int _stepIndex;

    private class DemoStep
    {
        public int atWp;           // 到达该路径点索引时触发
        public string text;        // 字幕内容
        public bool turnSignalOn;  // 触发时打开左转向灯
        public bool turnSignalOff; // 触发时关闭转向灯
    }
    private DemoStep[] _steps;

    // ---------------- 运行状态 ----------------
    private DemoState _state = DemoState.Idle;
    private DrivePhase _phase = DrivePhase.Run;
    private float _steer;           // 当前归一化转向输入 -1..1
    private float _steerTargetSmooth; // 转向目标低通滤波值（抑制前瞻点跨段跳变导致的方向盘抖动）
    private float _driveTime;         // 本轮演示已行驶时长（起步斜坡用：起步 1s 内限制转向幅度）
    private float _lastFF;            // 最近一帧的曲率前馈舵量（遥测诊断用）
    private float _logTimer = 0f;   // 调试遥测计时（调完删）
    private bool _gearReverse;      // 当前档位是否 R（倒车）
    private bool _leftStart;        // 是否已离开起点区域（防止起步当帧跳点）
    private bool _pendingShift;     // 刹停后需要换档
    private bool _pendingFinish;    // 刹停后结束演示
    private float _holdRemaining;   // 当前停车保持剩余秒数
    private float _shiftTimer;      // 换档等待计时
    private float _stuckTimer;      // 防卡死计时
    private bool _demoUsed;         // 用过演示后 R 键才生效

    // ============================================================
    //  调参模式（ZjzwTuner 驱动；参数从项目根 Tuning/zjzw_tuning.json 热注入，
    //  不改 .cs → 不触发 Unity 重编译 → 演示不会被域重载打断）
    // ============================================================
    [System.NonSerialized] public bool tuningMode;          // 当前是否处于调参轮（由 ZjzwTuner 置位）
    [System.NonSerialized] public ZjzwTune appliedTune;     // 本轮注入的调参参数（null = 用内置默认值）
    [System.NonSerialized] public QxxsTune appliedQxxsTune; // 曲线行驶调参参数（QxxsTuner 注入；null = 用内置默认值）
    [System.NonSerialized] public bool roundFinished;       // 本轮已结束（ZjzwTuner 轮询此标志）
    [System.NonSerialized] public ZjzwRoundResult lastResult; // 本轮结果（结束时机体写入）

    private int _roundStartScore;      // 本轮开始时的分数（检测扣分）
    private float _roundStartTime;     // 本轮开始时刻（Time.time）
    private float _maxDev;             // 本轮轨迹与参考路径的最大横向偏差（米）
    private float _sampleTimer;        // 轨迹采样计时
    private readonly List<float> _trace = new List<float>(); // 轨迹采样 [x,z,x,z,...]

    // 演示中暂停的玩家输入控制器（演示结束立刻恢复，其余考试组件一律不动）
    private readonly List<Behaviour> _pausedInputs = new List<Behaviour>();

    // ---------------- UI（运行时创建，不改场景文件） ----------------
    private Canvas _canvas;
    private Image _captionBg;
    private Text _caption;
    private Button _demoButton;
    private Text _buttonText;
    private float _captionHideAt = -1f;

    // ============================================================
    //  自动安装：进入"考试模式"任一单项练习时挂到玩家的车上
    // ============================================================
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InstallOnSceneLoad()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;

        // 安装看门狗：Play 中域重载（重编译）会丢失静态事件订阅，
        // 用一个常驻对象每 0.5 秒自检兜底，保证按钮永远能装回来
        if (Object.FindObjectOfType<DemoDriverInstaller>() == null)
        {
            GameObject go = new GameObject("DemoDriverInstaller");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<DemoDriverInstaller>();
        }

        TryInstall(SceneManager.GetActiveScene());
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        TryInstall(scene);
    }

    /// <summary>确保当前活动场景（Exam Mode 单项练习）的玩家车上装有 DemoDriver，可重复调用</summary>
    public static void EnsureInstalled()
    {
        TryInstall(SceneManager.GetActiveScene());
    }

    private static void TryInstall(Scene scene)
    {
        if (scene.name != "Exam Mode") return;

        SelectPass sp = Object.FindObjectOfType<SelectPass>();
        if (sp == null) return;
        if (sp.sites == SelectPass.Sites.Default) return; // Default 是全图考试，不装

        DrivingKernel kernel = Object.FindObjectOfType<DrivingKernel>();
        if (kernel == null) return;

        DemoDriver existing = kernel.GetComponent<DemoDriver>();
        if (existing != null)
        {
            if (existing._sites == sp.sites) return;
            // 项目不符（多为 Play 中域重载导致字段清空）：清理旧组件和旧 UI 后重建
            Debug.Log("[DemoDriver] 检测到失效的旧演示组件，重建（旧sites=" + existing._sites + " 新sites=" + sp.sites + "）");
            if (existing._canvas != null) Object.Destroy(existing._canvas.gameObject);
            Object.Destroy(existing);
        }

        Debug.Log("[DemoDriver] 进入 Exam Mode，sites=" + sp.sites);
        DemoDriver driver = kernel.gameObject.AddComponent<DemoDriver>();
        driver._sites = sp.sites;
    }

    // ============================================================
    //  生命周期
    // ============================================================
    private void Start()
    {
        _kernel = GetComponent<DrivingKernel>();
        _gears = FindObjectOfType<GearsDangWei_Control>();
        _handleBrake = FindObjectOfType<Car_HandleBrake>();
        _turnLight = FindObjectOfType<TurnLight>();

        EnsureUI();
        HideCaption();
        ShowCaption("提示：点击右侧【演示】按钮或按 G 键，看电脑按考试标准开一遍", 8f);
    }

    private void Update()
    {
        // 字幕定时隐藏
        if (_captionHideAt > 0f && Time.time >= _captionHideAt)
        {
            HideCaption();
            _captionHideAt = -1f;
        }

        // 调参轮中 G/R 全部忽略（批次由 ZjzwTuner 全自动控制，人工干预会丢一轮数据）
        if (!tuningMode)
        {
            if (Input.GetKeyDown(KeyCode.G))
            {
                ToggleDemo();
            }

            // 用过演示后，R 键快速重开本项练习（与游戏内重开按钮走同一条加载链路）
            if (_demoUsed && Input.GetKeyDown(KeyCode.R))
            {
                Globe.loadM = Globe.LoadMode.Default;
                Globe.nextSceneName = SceneManager.GetActiveScene().name;
                SceneManager.LoadScene("Loading");
            }
        }

        // 调参轮失败检测：扣分（压线/中途停车/超时均扣 100）/结算暂停/兜底超时
        if (tuningMode && _state == DemoState.Driving)
        {
            if (Car_Information.GetScore() < _roundStartScore)
            {
                EndTuningRound("deduct");
                return;
            }
            if (Time.timeScale == 0f)
            {
                EndTuningRound("settlement");
                return;
            }
            if (appliedTune != null && Time.time - _roundStartTime > appliedTune.maxRoundSeconds)
            {
                EndTuningRound("timeout");
                return;
            }
            if (appliedQxxsTune != null && Time.time - _roundStartTime > appliedQxxsTune.maxRoundSeconds)
            {
                EndTuningRound("timeout");
                return;
            }
        }

        if (_state == DemoState.Driving)
        {
            DriveUpdate();
        }
    }

    // ============================================================
    //  演示开关
    // ============================================================
    public void ToggleDemo()
    {
        if (_state == DemoState.Driving)
        {
            CancelDemo();
        }
        else
        {
            StartDemo();
        }
    }

    private void StartDemo()
    {
        if (!BuildPath()) return;

        // 1) 把车传回项目起点（无论玩家刚才开到哪，演示永远从标准起点开始）
        GameObject spawnObj = GameObject.Find(_sites.ToString());
        if (spawnObj == null)
        {
            ShowCaption("演示启动失败：找不到项目起点对象 " + _sites, 5f);
            return;
        }
        Rigidbody rb = _kernel.GetComponentInParent<Rigidbody>();
        if (rb != null)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.position = spawnObj.transform.position;
            rb.rotation = spawnObj.transform.rotation;
            rb.WakeUp();
        }
        Debug.Log(string.Format("[DEMO] 开始 sites={0} 起点=({1:F1},{2:F1},{3:F1}) 起点yaw={4:F0} rb位置=({5:F1},{6:F1},{7:F1}) rb子物={8}",
            _sites, spawnObj.transform.position.x, spawnObj.transform.position.y, spawnObj.transform.position.z,
            spawnObj.transform.eulerAngles.y,
            rb != null ? rb.position.x : -1f, rb != null ? rb.position.z : -1f, 0f,
            rb != null ? rb.name : "null"));

        // 2) 挂 D 档、松手刹
        if (_gears != null) _gears.D();
        if (_handleBrake != null && _handleBrake.Activate)
        {
            try { _handleBrake.ChangeState(); }
            catch { _handleBrake.Activate = false; }
        }

        // 3) 关闭转向灯（保证演示状态干净）
        if (_turnLight != null && _turnLight.CTL != TurnLight.CarTurnLight.Off)
        {
            try
            {
                if (_turnLight.CTL == TurnLight.CarTurnLight.Left) _turnLight.SetLeft();
                else if (_turnLight.CTL == TurnLight.CarTurnLight.Right) _turnLight.SetRight();
                else _turnLight.SetExigency();
            }
            catch { _turnLight.CTL = TurnLight.CarTurnLight.Off; }
        }

        // 4) 只暂停玩家输入；考试检测、压线墙、空气墙、结算全部保持原样
        PausePlayerInput();

        // 5) 初始化控制状态
        _steer = 0f;
        _steerTargetSmooth = 0f;
        _driveTime = 0f;
        _stuckTimer = 0f;
        _wpIndex = 0;
        _stepIndex = 0;
        _gearReverse = false;
        _leftStart = false;
        _inLock = false;
        _routeRecoveries = 0;
        _routeNote = "";
        _routeSpeed = -1f;
        _pendingShift = false;
        _pendingFinish = false;
        _holdRemaining = 0f;
        _shiftTimer = 0f;
        _phase = DrivePhase.Run;
        _state = DemoState.Driving;
        _demoUsed = true;

        FireStepsAt(0);
        SetButtonText("■ 停止演示");
    }

    private void CancelDemo()
    {
        _state = DemoState.Idle;
        if (_kernel != null) _kernel.Hardware_ReleaseControl();
        ResumePlayerInput();
        SetButtonText("▶ 演示");
        ShowCaption("已退出演示，车辆交还给你。按 R 可重新开始本项练习", 8f);
    }

    private void FinishDemo()
    {
        if (tuningMode)
        {
            // 调参轮：到达终点即成功，结果交由 ZjzwTuner 记录（不弹演示结束字幕）
            EndTuningRound("pass");
            return;
        }
        _state = DemoState.Finished;
        if (_kernel != null) _kernel.Hardware_ReleaseControl();
        ResumePlayerInput();
        SetButtonText("▶ 再看一遍");
        ShowCaption("演示结束，成绩按考试流程正常结算。按 G 再看一遍，按 R 自己练", 0f);
    }

    // ============================================================
    //  每帧自动驾驶（按阶段分派）
    // ============================================================
    private void DriveUpdate()
    {
        if (_kernel == null)
        {
            CancelDemo();
            return;
        }

        switch (_phase)
        {
            case DrivePhase.BrakeToStop: UpdateBrakeToStop(); return;
            case DrivePhase.ShiftGear: UpdateShiftGear(); return;
            case DrivePhase.Hold: UpdateHold(); return;
        }
        UpdateRun();
    }

    /// <summary>进入刹停阶段：记录停稳后要做的事（换档 / 点位保持 / 结束演示）</summary>
    private void BeginStop(bool needShift, float holdSeconds, bool finish)
    {
        _pendingShift = needShift;
        _pendingFinish = finish;
        _holdRemaining = holdSeconds;
        _phase = DrivePhase.BrakeToStop;
    }

    /// <summary>正常行驶：路径推进 + 纯跟踪转向（速度自适应前瞻）+ 分段限速/前方转角限速 + 卡死看门狗</summary>
    private void UpdateRun()
    {
        Vector3 pos = transform.position;

        // 0) 段方向检查：下一段需要 R/D 而当前档位不符 → 刹停换档
        if (_wpIndex < _wps.Length - 1 && _rev[_wpIndex] != _gearReverse)
        {
            BeginStop(true, 0f, false);
            return;
        }

        // 1) 离开起点区域后才允许推进（防止起步当帧从 wp0 跳到 wp1）。
        //    注意：离开阈值必须小于到达距离，否则车越过起点后就永远
        //    无法同时满足"已离开"和"在到达范围内"两个条件，wp 卡死在 0。
        if (!_leftStart && FlatDist(pos, _wps[0]) > 1.0f)
        {
            _leftStart = true;
        }

        // 2) 路径点推进：一次只推进一个点，下一帧再处理（不会跳过停车点）。
        //    a) 正常到达：距目标点 < 到达距离；
        //    b) 兜底越过：距下一点比距当前目标更近（车已越过目标点，如被
        //       别的物体顶偏）。含停车保持/换档的点不允许 b)，必须真实到达。
        if (_wpIndex < _wps.Length - 1 && (_wpIndex > 0 || _leftStart))
        {
            float dCur = FlatDist(pos, _wps[_wpIndex]);
            float dNext = FlatDist(pos, _wps[_wpIndex + 1]);
            bool mustStopHere = _hold[_wpIndex] > 0f || _rev[_wpIndex] != _gearReverse;
            if (dCur < _arriveDist || (!mustStopHere && dNext < dCur))
            {
                _wpIndex++;
                FireStepsAt(_wpIndex);

                if (_wpIndex >= _wps.Length - 1)
                {
                    // 到达终点：刹停，成绩结算交给考试系统正常触发
                    BeginStop(false, 0f, true);
                    return;
                }

                bool needShift = _rev[_wpIndex] != _gearReverse;
                if (needShift || _hold[_wpIndex] > 0f)
                {
                    BeginStop(needShift, _hold[_wpIndex], false);
                    return;
                }
            }
        }

        // 3) 转向：直角转弯转角段用「打死→保持→回正」；其余路段纯跟踪
        //    （倒车用镜像转向律：目标在车后方，取 atan2(lx, |lz|)）
        bool steerByLock = false;
        _routeSpeed = -1f;
        if (_sites == SelectPass.Sites.Zjzw && _zjzwDynamic && !_gearReverse)
        {
            // 几何闭环：每帧按当前位姿反解"落进出口道中心"所需半径 → 转向 + 车速
            float yawNow = transform.eulerAngles.y;
            RouteOutput ro = ZjzwRoute.Plan(pos, yawNow, _routeCfg);
            _routePhase = ro.phase;

            if (!string.IsNullOrEmpty(ro.note) && ro.note != _routeNote)
            {
                _routeNote = ro.note;
                _routeRecoveries++;
                Debug.Log(string.Format("[ROUTE] 动态调整#{0}：{1} pos=({2:F2},{3:F2}) yaw={4:F1} Rreq={5:F2} cte={6:F2}",
                    _routeRecoveries, ro.note, pos.x, pos.z, yawNow, ro.radiusReq, ro.cte));
            }
            else if (string.IsNullOrEmpty(ro.note))
            {
                _routeNote = "";
            }

            // 转弯段方向盘要打得快（与老方案一致），其余段用常规速率
            bool inTurn = ro.phase == RoutePhase.Turn || ro.phase == RoutePhase.Overshoot;
            float rate = inTurn ? Mathf.Max(_zjzwLockSpeed, 4f) : _steerSpeedFwd;
            _steer = Mathf.MoveTowards(_steer, ro.steer, rate * Time.deltaTime);
            _kernel.Hardware_SetSteering(_steer);
            _routeSpeed = ro.speed;
            _inLock = inTurn;
            steerByLock = true;
        }
        else if (_sites == SelectPass.Sites.Zjzw && _zjzwLockArc && !_gearReverse)
        {
            float yaw = NormYaw(transform.eulerAngles.y); // 东=90，北=0，左转递减
            if (!_inLock)
            {
                // 触发：车头接近转角横线（提前 _zjzwLockLead 米）且车身还没转起来
                if (pos.x >= _zjzwCornerX - _zjzwLockLead && yaw > 45f)
                {
                    _inLock = true;
                }
            }
            if (_inLock)
            {
                if (yaw <= _zjzwReleaseYaw)
                {
                    _inLock = false; // 车身已基本转正：交回纯跟踪收尾，方向自然回正
                }
                else
                {
                    _steer = Mathf.MoveTowards(_steer, _zjzwLockSteer, _zjzwLockSpeed * Time.deltaTime);
                    _kernel.Hardware_SetSteering(_steer);
                    steerByLock = true;
                }
            }
        }
        if (!steerByLock)
        {
        _driveTime += Time.deltaTime;
        // 横向误差（Stanley 项）：车偏出路径时按偏移量直接反打方向。
        // 0.15m 内线性淡入——贴线时完全交给纯跟踪，避免两套修正互搏形成振荡回路（方向盘大幅甩动）；
        // 偏出 0.15m 后纠偏全权介入，偏差一出现（不用等碰到边线）就开始快速回正
        Vector3 projPt; int projSeg;
        float crossErr = ProjectOnPath(pos, out projPt, out projSeg);
        float errFactor = Mathf.Clamp01(Mathf.Abs(crossErr) / 0.5f); // 偏 0.5m 时响应拉满
        // 曲率前馈先算：既给转向用，也充当速度自适应前瞻的直/弯判据
        float ff = (!_gearReverse && _wps != null && _wps.Length > 1) ? SteerFeedforward(projPt, projSeg) : 0f;
        _lastFF = ff;
        // 速度自适应前瞻（Standard Assets 思路：跑得快看得远）——直道上前瞻随车速拉长压住画龙，
        // 弯道（前馈舵量超门槛）保持基础前瞻，防止前瞻一大提前切弯内侧
        float lookFwd = _lookForward;
        float ffAbs = Mathf.Abs(ff);
        if (!_gearReverse && ffAbs < _lookCurveGate)
        {
            float curveAuth = Mathf.Clamp01((_lookCurveGate - ffAbs) / _lookCurveGate); // 1=直道 → 0=接近门槛
            lookFwd += _lookSpeedGain * Mathf.Max(_kernel.speed / 3.6f, 0f) * curveAuth;
        }
        Vector3 target = LookaheadPoint(pos, _gearReverse ? _lookReverse : lookFwd);
        Vector3 local = transform.InverseTransformPoint(target);
        float ang;
        if (_gearReverse) ang = Mathf.Atan2(local.x, Mathf.Max(-local.z, 0.15f));
        else ang = Mathf.Atan2(local.x, Mathf.Max(local.z, 0.15f));
        float steerTarget = Mathf.Clamp(ang * _steerGain, -1f, 1f);
        if (!_gearReverse && _wps.Length > 1)
        {
            float vMps = Mathf.Max(_kernel.speed / 3.6f, 1.2f); // m/s
            float auth = Mathf.Clamp01(Mathf.Abs(crossErr) / 0.15f);
            steerTarget += -Mathf.Atan2(_crossGain * crossErr, vMps) * auth; // 偏右 → 向左补，偏左 → 向右补
            steerTarget += ff;
            steerTarget = Mathf.Clamp(steerTarget, -1f, 1f);
        }
        // 起步斜坡：起步 1 秒内限制转向幅度，防止瞬态尖峰把车甩出路径（甩出去再拉回来 = 方向盘猛甩）
        if (_driveTime < 1f) steerTarget = Mathf.Clamp(steerTarget, -0.35f, 0.35f);
        // 方向盘稳化（自适应）：贴线巡航重滤波压抖动；主动转向或纠偏时滤波自动加速，
        // 消除相位滞后——滞后正是大角度转向时振荡的根源。
        // resp 关键量 = 方向盘需要走的距离 |目标-当前|：过切点从左舵摆到右舵的瞬间
        // 目标值过零，若按 |目标| 大小定 resp 会在变向时刻反而降到最慢（第二弯跪的根因）
        float swing = Mathf.Abs(steerTarget - _steer);
        float resp = Mathf.Max(errFactor, Mathf.Clamp01(swing * 2.5f));
        float dead = Mathf.Lerp(0.03f, 0.012f, errFactor);
        if (Mathf.Abs(steerTarget) < dead) steerTarget = 0f;
        float lp = Mathf.Lerp(4f, 12f, resp);
        _steerTargetSmooth = Mathf.Lerp(_steerTargetSmooth, steerTarget, 1f - Mathf.Exp(-lp * Time.deltaTime));
        steerTarget = _steerTargetSmooth;
        float steerSpeed = _gearReverse ? 3.2f : Mathf.Lerp(_steerSpeedFwd, 5f, resp); // 主动转向时打舵提速
        _steer = Mathf.MoveTowards(_steer, steerTarget, steerSpeed * Time.deltaTime);
        _kernel.Hardware_SetSteering(_steer);
        }

        // 4) 速度控制（分段目标车速，急弯自动降速；几何闭环阶段用路线给的目标车速）
        float vTarget = _speeds[Mathf.Clamp(_wpIndex, 0, _speeds.Length - 1)];
        if (_routeSpeed >= 0f) vTarget = _routeSpeed;
        vTarget = Mathf.Min(vTarget, CornerSpeedCap(pos)); // 前方转角限速：统一生效（几何闭环管转向，限速管车速，互不冲突）
        if (Mathf.Abs(_steer) > 0.5f) vTarget = Mathf.Min(vTarget, 7f); // 打舵限速兜底：须高于恒速 6.5，否则满舵段被钳回 6 → 低侧偏内切
        float v = _kernel.speed;
        float throttle = 0f;
        float brake = 0f;
        // 油门死区：路线模式（匀速）用 0.2 小死区——1.0 大死区会让匀速滑行掉到 vTarget-1，
        // 直角转弯弯中掉到 5.0 → 侧偏变小 → 弧线收紧内切撞墙（2026-11-03 实测教训）。
        // 路线模式油门增益/下限加大：满舵死弯滚动阻力大，弱油门顶不住 6.5 会重新掉速内切（2026-11-04 实测）
        bool routeMode = _routeSpeed >= 0f;
        float throttleDb = routeMode ? 0.2f : 1f;
        if (v < vTarget - throttleDb)
        {
            throttle = Mathf.Clamp((vTarget - v) * (routeMode ? 0.6f : 0.35f), routeMode ? 0.35f : 0.2f, _throttleMax);
        }
        else if (v > vTarget + 2f)
        {
            brake = Mathf.Clamp((v - vTarget) * 0.3f, 0.15f, 0.7f);
        }
        _kernel.Hardware_SetPedals(throttle, brake);

        // 5) 卡死看门狗：油门在踩但车速接近 0 持续 8 秒 → 放弃演示
        if (throttle > 0.1f && v <= 1f)
        {
            _stuckTimer += Time.deltaTime;
            if (_stuckTimer > 8f)
            {
                if (tuningMode)
                {
                    EndTuningRound("stuck");
                    return;
                }
                ShowCaption("演示中断：车辆受阻（可能碰到了边线）。按 R 重新开始", 8f);
                CancelDemo();
                return;
            }
        }
        else
        {
            _stuckTimer = 0f;
        }

        // 6) 调参轮遥测：每 0.25 秒采样轨迹 + 统计与参考路径的最大偏差
        if (tuningMode)
        {
            _sampleTimer -= Time.deltaTime;
            if (_sampleTimer <= 0f)
            {
                _sampleTimer = 0.25f;
                _trace.Add(pos.x);
                _trace.Add(pos.z);
                float dev = DistanceToPath(pos);
                if (dev > _maxDev) _maxDev = dev;
            }
        }

        // 7) 调试遥测（每 0.5s 一条，调完删）
        _logTimer -= Time.deltaTime;
        if (_logTimer <= 0f)
        {
            _logTimer = 0.5f;
            Debug.Log(string.Format("[DEMO] pos=({0:F1},{1:F1}) yaw={2:F0} wp={3}/{4} steer={5:F2} ff={6:F2} v={7:F1} gearRev={8} distWp={9:F1} throttle={10:F2} lock={11}",
                pos.x, pos.z, transform.eulerAngles.y, _wpIndex, _wps.Length - 1,
                _steer, _lastFF, v, _gearReverse, FlatDist(pos, _wps[_wpIndex]), throttle, _inLock));
        }
    }

    /// <summary>刹停阶段：油门 0 刹车 1，方向缓慢回正，停稳后按待办事项分派</summary>
    private void UpdateBrakeToStop()
    {
        SettleSteering();
        _kernel.Hardware_SetPedals(0f, 1f);
        if (_kernel.speed > 0.5f) return; // speed 已取整，>0.5 即还有车速

        if (_pendingFinish)
        {
            FinishDemo();
            return;
        }
        if (_pendingShift)
        {
            _pendingShift = false;
            if (_gears != null)
            {
                if (_rev[_wpIndex]) _gears.R();
                else _gears.D();
            }
            _gearReverse = _rev[_wpIndex];
            _shiftTimer = 0.8f; // 等档杆动画走完再起步
            _phase = DrivePhase.ShiftGear;
            return;
        }
        _phase = DrivePhase.Hold;
    }

    /// <summary>换档等待：保持刹车，等档位切换稳定</summary>
    private void UpdateShiftGear()
    {
        SettleSteering();
        _kernel.Hardware_SetPedals(0f, 1f);
        _shiftTimer -= Time.deltaTime;
        if (_shiftTimer <= 0f)
        {
            _phase = DrivePhase.Hold;
        }
    }

    /// <summary>点位停车保持：看点/定点停车，半坡定点在此拉手刹，结束自动松开</summary>
    private void UpdateHold()
    {
        SettleSteering();
        if (_holdRemaining > 0f)
        {
            _holdRemaining -= Time.deltaTime;
            if (_handbrake[_wpIndex] && _handleBrake != null && !_handleBrake.Activate)
            {
                try { _handleBrake.ChangeState(); }
                catch { _handleBrake.Activate = true; }
            }
            _kernel.Hardware_SetPedals(0f, 1f);
            return;
        }

        // 保持结束：确保手刹松开再起步
        if (_handleBrake != null && _handleBrake.Activate)
        {
            try { _handleBrake.ChangeState(); }
            catch { _handleBrake.Activate = false; }
        }
        _stuckTimer = 0f;
        _phase = DrivePhase.Run;
    }

    /// <summary>停车/保持期间方向盘缓慢回正（原地打死方向不自然）</summary>
    private void SettleSteering()
    {
        if (Mathf.Abs(_steer) > 0.01f)
        {
            _steer = Mathf.MoveTowards(_steer, 0f, 1.6f * Time.deltaTime);
            _kernel.Hardware_SetSteering(_steer);
        }
    }

    /// <summary>Catmull-Rom 样条重采样：把折线拐点圆滑成连续曲线（0.4m 步长），消除前瞻点跨段跳变</summary>
    private static Vector3[] ResampleCatmullRom(Vector3[] pts, float step)
    {
        int n = pts.Length;
        if (n < 3) return pts;
        var list = new System.Collections.Generic.List<Vector3>(n * 4);
        for (int i = 0; i < n - 1; i++)
        {
            Vector3 p0 = pts[Mathf.Max(0, i - 1)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Mathf.Min(n - 1, i + 2)];
            float segLen = Mathf.Sqrt((p2.x - p1.x) * (p2.x - p1.x) + (p2.z - p1.z) * (p2.z - p1.z));
            int steps = Mathf.Max(1, Mathf.CeilToInt(segLen / step));
            for (int s = 0; s < steps; s++)
            {
                float t = (float)s / steps, t2 = t * t, t3 = t2 * t;
                float x = 0.5f * (2f * p1.x + (-p0.x + p2.x) * t + (2f * p0.x - 5f * p1.x + 4f * p2.x - p3.x) * t2 + (-p0.x + 3f * p1.x - 3f * p2.x + p3.x) * t3);
                float z = 0.5f * (2f * p1.z + (-p0.z + p2.z) * t + (2f * p0.z - 5f * p1.z + 4f * p2.z - p3.z) * t2 + (-p0.z + 3f * p1.z - 3f * p2.z + p3.z) * t3);
                list.Add(new Vector3(x, 0f, z));
            }
        }
        list.Add(pts[n - 1]);
        return list.ToArray();
    }

    /// <summary>
    /// 曲率前馈：沿路径向前取 1.8/3.0/4.2m 三个点，用外接圆（Menger）曲率估算前方弯曲度，
    /// 按稳态转向公式 δ = atan(轴距×曲率) 换算成舵量（除以满舵车轮转角）。
    /// S 弯半径 ~7.5m 需要 δ≈19°（约半舵），纯跟踪反馈天生只给一半——弯中打不够、往外线漂就源于此。
    /// 前瞻 3m 采样 = 提前约 1.5 秒开始给舵，实现「根据弯曲度提前打方向」。
    /// </summary>
    private float SteerFeedforward(Vector3 projPt, int projSeg)
    {
        int n = _wps.Length;
        if (n < 4) return 0f;
        // 航向角增量法：取投影点前方 ~2m 窗口，窗口两端方向向量的夹角 = 该段累计转角。
        // 弯道交接处（左弯→右弯）窗口内左右转角互相抵消，前馈平滑过零——
        // 旧版三点外接圆（1.8/3.0/4.2m）在交接处会横跨符号翻转，算出虚假大曲率，
        // 导致「上一个弯还没转完就猛打下一个弯的方向」。
        const float window = 2.0f;
        Vector3 dir0 = Vector3.zero, dirEnd = Vector3.zero, prev = projPt;
        bool have0 = false;
        float s = 0f;
        for (int i = projSeg; i < n - 1; i++)
        {
            Vector3 cur = _wps[i + 1];
            float d = FlatDist(prev, cur);
            if (d > 0.0001f)
            {
                if (!have0)
                {
                    dir0 = new Vector3((cur.x - prev.x) / d, 0f, (cur.z - prev.z) / d);
                    have0 = true;
                }
                if (s + d >= window)
                {
                    float t = (window - s) / d;
                    dirEnd = new Vector3(Mathf.Lerp(prev.x, cur.x, t) - prev.x, 0f, Mathf.Lerp(prev.z, cur.z, t) - prev.z);
                    float le = dirEnd.magnitude;
                    if (le > 0.0001f) { dirEnd /= le; s = window; }
                    break;
                }
                s += d;
                dirEnd = new Vector3((cur.x - prev.x) / d, 0f, (cur.z - prev.z) / d);
            }
            prev = cur;
        }
        if (!have0 || s < window * 0.75f) return 0f; // 窗口不足 3/4（路径末端）不做前馈，防止末端曲率失真
        float cross = dir0.x * dirEnd.z - dir0.z * dirEnd.x; // 单位向量叉积 = sin(夹角)，>0 = 左转
        float dHeading = Mathf.Asin(Mathf.Clamp(cross, -1f, 1f)); // 窗口内累计转角（弧度）
        float kappa = Mathf.Abs(dHeading) / window;               // 平均曲率 1/m
        if (kappa < 0.005f) return 0f;                            // 近似直线不前馈
        float deg = Mathf.Atan(_kernel.wheelBase * kappa) * Mathf.Rad2Deg;
        return -Mathf.Sign(dHeading) * deg / Mathf.Max(_kernel.steerAngle, 1f);
    }

    /// <summary>把车投影到当前路径段附近，返回最近投影点和段索引（out），并返回带符号横向偏移（正 = 车在路径前进方向右侧）</summary>
    private float ProjectOnPath(Vector3 pos, out Vector3 bestPt, out int bestSeg)
    {
        int firstSeg = Mathf.Max(0, _wpIndex - 1);
        int lastSeg = Mathf.Min(_wps.Length - 2, _wpIndex + 2);
        float bestSqr = float.MaxValue;
        bestPt = _wps[firstSeg];
        bestSeg = firstSeg;
        float signed = 0f;
        for (int i = firstSeg; i <= lastSeg; i++)
        {
            Vector3 a = _wps[i];
            Vector3 b = _wps[i + 1];
            float abx = b.x - a.x, abz = b.z - a.z;
            float len2 = abx * abx + abz * abz;
            float t = 0f;
            if (len2 > 0.0001f)
            {
                t = ((pos.x - a.x) * abx + (pos.z - a.z) * abz) / len2;
                t = Mathf.Clamp01(t);
            }
            float px = a.x + abx * t, pz = a.z + abz * t;
            float dx = pos.x - px, dz = pos.z - pz;
            float sqr = dx * dx + dz * dz;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                bestPt = new Vector3(px, 0f, pz);
                bestSeg = i;
                if (len2 > 0.0001f)
                {
                    float inv = 1f / Mathf.Sqrt(len2);
                    float dirx = abx * inv, dirz = abz * inv; // 段前进方向（归一化）
                    signed = dirz * dx - dirx * dz;           // 叉积：正 = 车在路径右侧
                }
            }
        }
        return signed;
    }

    /// <summary>从车辆当前位置沿路径向前找 look 米处的目标点</summary>
    private Vector3 LookaheadPoint(Vector3 pos, float look)
    {
        // 1) 把车投影到路径折线上（复用 ProjectOnPath，同时拿到最近段索引）
        Vector3 bestPt; int bestSeg;
        ProjectOnPath(pos, out bestPt, out bestSeg);

        // 2) 从投影点沿路径（弧长）向前找 look 米处的目标点
        float remain = look;
        Vector3 from = bestPt;
        for (int i = bestSeg; i < _wps.Length - 1; i++)
        {
            Vector3 b = _wps[i + 1];
            float d = FlatDist(from, b);
            if (d >= remain && d > 0.001f)
            {
                float t = remain / d;
                float x = Mathf.Lerp(from.x, b.x, t);
                float z = Mathf.Lerp(from.z, b.z, t);
                return new Vector3(x, pos.y, z);
            }
            remain -= d;
            from = b;
        }
        return new Vector3(_wps[_wps.Length - 1].x, pos.y, _wps[_wps.Length - 1].z);
    }

    /// <summary>
    /// 前方转角限速（Standard Assets CarAIControl 的 Cautious 思路）：
    /// 沿路径向前扫 _aheadScanDist 米，累计航向变化绝对值，转角越大限速越低。
    /// 分段车速表管的是"到哪一段降到多少"，这里是"看见弯就提前收油"的连续补充：
    /// 上限 _capSpeedHigh 高于所有分段车速（缓弯/直道完全不干预），
    /// 下限 _capSpeedLow 保证弯中仍有爬行速度（压太低会触发中途停车扣分）。
    /// </summary>
    private float CornerSpeedCap(Vector3 pos)
    {
        if (_wps == null || _wps.Length < 3) return _capSpeedHigh;
        Vector3 bestPt; int bestSeg;
        ProjectOnPath(pos, out bestPt, out bestSeg);
        float remain = _aheadScanDist;
        float total = 0f;
        Vector3 prevDir = Vector3.zero;
        bool hasDir = false;
        Vector3 prev = bestPt; // 首段从投影点起算，避免起步位置抖动
        for (int i = bestSeg; i < _wps.Length - 1 && remain > 0f; i++)
        {
            Vector3 b = _wps[i + 1];
            float d = FlatDist(prev, b);
            if (d > 0.001f)
            {
                Vector3 dir = new Vector3((b.x - prev.x) / d, 0f, (b.z - prev.z) / d);
                if (hasDir)
                {
                    float dot = Mathf.Clamp(Vector3.Dot(prevDir, dir), -1f, 1f);
                    total += Mathf.Acos(dot) * Mathf.Rad2Deg; // 相邻段夹角累计
                }
                prevDir = dir;
                hasDir = true;
            }
            remain -= d;
            prev = b;
        }
        float t = Mathf.Clamp01((total - _capAngleMin) / (_capAngleMax - _capAngleMin));
        return Mathf.Lerp(_capSpeedHigh, _capSpeedLow, t);
    }

    // ============================================================
    //  路径与字幕（坐标取自 Exam Mode.unity 场景解析，可在此微调）
    // ============================================================
    private bool BuildPath()
    {
        switch (_sites)
        {
            case SelectPass.Sites.Zjzw: BuildZjzwPath(); return true;
            case SelectPass.Sites.Qxxs: BuildQxxsPath(); return true;
            case SelectPass.Sites.Dcrk: BuildDcrkPath(); return true;
            case SelectPass.Sites.Cftc: BuildCftcPath(); return true;
            case SelectPass.Sites.Bpqb: BuildBpqbPath(); return true;
        }
        ShowCaption("该项目暂不支持演示", 5f);
        return false;
    }

    /// <summary>
    /// 倒车入库（右库）：起点(-109.7,-25.8)朝东 → 沿通道前进驶过库口 → 停车挂R
    /// → 右打倒车入库 → 库内停稳挂D → 前进出库驶向 End(-54.6,-34.8)。
    /// 库口在 z=-34.4 一线（Point_left/-79.5 与 Point_right/-63.7 为左右库口中心），
    /// 底部到 z=-43.1（Point_garage）。本路径按右库（x≈-63.7）演示，坐标为估算值。
    /// </summary>
    private void BuildDcrkPath()
    {
        _wps = new Vector3[]
        {
            new Vector3(-109.7f, 0f, -25.8f),  // 0 起点
            new Vector3(-95.0f, 0f, -27.0f),   // 1
            new Vector3(-84.0f, 0f, -28.5f),   // 2
            new Vector3(-74.0f, 0f, -29.5f),   // 3
            new Vector3(-66.5f, 0f, -30.0f),   // 4 停车点：车头驶过库口（挂R）
            new Vector3(-65.5f, 0f, -32.5f),   // 5 倒车开始，右打死
            new Vector3(-64.5f, 0f, -35.5f),   // 6 车尾进入库口
            new Vector3(-64.3f, 0f, -38.5f),   // 7 车身转正（回正方向）
            new Vector3(-64.4f, 0f, -41.0f),   // 8 继续直倒
            new Vector3(-64.5f, 0f, -42.8f),   // 9 库内停稳（挂D）
            new Vector3(-64.5f, 0f, -37.0f),   // 10 前进出库
            new Vector3(-64.3f, 0f, -32.5f),   // 11 出库口
            new Vector3(-61.0f, 0f, -31.0f),   // 12 向东拐
            new Vector3(-57.0f, 0f, -33.0f),   // 13
            new Vector3(-54.8f, 0f, -34.6f)    // 14 终点（End 前，不撞空气墙）
        };
        _speeds = new float[] { 8, 8, 8, 7, 4, 4, 4, 4, 4, 4, 8, 8, 8, 8, 8 };
        _rev = new bool[] { false, false, false, false, true, true, true, true, true, false, false, false, false, false, false };
        _hold = new float[] { 0, 0, 0, 0, 1.5f, 0, 0, 0, 0, 2f, 0, 0, 0, 0, 0 };
        _handbrake = new bool[_wps.Length];
        _steps = new DemoStep[]
        {
            new DemoStep { atWp = 0, text = "① 起步：挂 D 档沿通道行驶，保持车身与库位边线约 1.5 米距离" },
            new DemoStep { atWp = 2, text = "② 观察右后视镜确认库口位置，控制车速缓慢行驶" },
            new DemoStep { atWp = 4, text = "③ 车头驶过库口后停车，挂 R 档，向右打死方向开始倒车" },
            new DemoStep { atWp = 6, text = "④ 从右后视镜看到车身与库位平行时，迅速回正方向" },
            new DemoStep { atWp = 8, text = "⑤ 观察左后视镜下沿接近库底线时停车——入库完成" },
            new DemoStep { atWp = 10, text = "⑥ 挂 D 档前进出库，驶向出口完成项目" }
        };
    }

    /// <summary>
    /// 侧方停车：起点(-39.9,-28.5)朝东 → 沿车道行驶（库位在右侧，Point_garage -17.6,-32.8）
    /// → 车头驶过库位停车挂R → 右打倒入 → 车身平行停稳挂D → 打左灯左打死出库 → 驶向 End(4.0,-28.4)。
    /// 坐标为估算值。
    /// </summary>
    private void BuildCftcPath()
    {
        _wps = new Vector3[]
        {
            new Vector3(-39.9f, 0f, -28.5f),  // 0 起点
            new Vector3(-33.0f, 0f, -28.8f),  // 1
            new Vector3(-26.0f, 0f, -29.2f),  // 2
            new Vector3(-19.5f, 0f, -29.5f),  // 3
            new Vector3(-14.5f, 0f, -29.8f),  // 4 停车点：车头已驶过库位（挂R）
            new Vector3(-15.5f, 0f, -31.2f),  // 5 倒车右打死
            new Vector3(-16.5f, 0f, -32.5f),  // 6 车尾进库
            new Vector3(-18.0f, 0f, -33.2f),  // 7 回正（左镜见库右前角）
            new Vector3(-19.5f, 0f, -33.4f),  // 8 左打死调整
            new Vector3(-21.0f, 0f, -33.3f),  // 9 库内停稳（挂D）
            new Vector3(-20.0f, 0f, -32.0f),  // 10 左打驶出
            new Vector3(-18.0f, 0f, -30.2f),  // 11
            new Vector3(-14.5f, 0f, -29.2f),  // 12 回正
            new Vector3(-8.0f, 0f, -28.6f),   // 13
            new Vector3(-2.0f, 0f, -28.4f),   // 14
            new Vector3(3.5f, 0f, -28.4f)     // 15 终点（End 前）
        };
        _speeds = new float[] { 8, 8, 8, 7, 4, 4, 4, 4, 4, 4, 6, 7, 8, 8, 8, 8 };
        _rev = new bool[] { false, false, false, false, true, true, true, true, true, false, false, false, false, false, false, false };
        _hold = new float[] { 0, 0, 0, 0, 1.5f, 0, 0, 0, 0, 2f, 0, 0, 0, 0, 0, 0 };
        _handbrake = new bool[_wps.Length];
        _steps = new DemoStep[]
        {
            new DemoStep { atWp = 0, text = "① 起步：挂 D 档沿车道行驶，车身距库位边线约 30 公分" },
            new DemoStep { atWp = 2, text = "② 观察右侧后视镜确认库位，控制车速缓慢行驶" },
            new DemoStep { atWp = 4, text = "③ 车头驶过库位后停车，挂 R 档，向右打死方向倒入" },
            new DemoStep { atWp = 6, text = "④ 左后视镜出现库右前角时，回正方向继续倒" },
            new DemoStep { atWp = 8, text = "⑤ 左后轮即将压库左边线时向左打死，车身平行后停稳" },
            new DemoStep { atWp = 9, text = "⑥ 挂 D 档，打开左转向灯，向左打死方向驶出库位", turnSignalOn = true },
            new DemoStep { atWp = 12, text = "⑦ 车身出库后回正方向，关闭转向灯，驶出项目", turnSignalOff = true }
        };
    }

    /// <summary>
    /// 半坡起步：起点(33.7,-2.8)朝北 → 上坡对正右边线30公分 → 定点停车（Point_garage 33.0,43.6）
    /// → 拉手刹 → 松手刹半联动起步 → 下坡 → 驶向 End(33.8,50.5)。坐标为估算值。
    /// </summary>
    private void BuildBpqbPath()
    {
        _wps = new Vector3[]
        {
            new Vector3(33.7f, 0f, -2.8f),   // 0 起点
            new Vector3(33.6f, 0f, 5.0f),    // 1
            new Vector3(33.5f, 0f, 15.0f),   // 2
            new Vector3(33.4f, 0f, 25.0f),   // 3
            new Vector3(33.2f, 0f, 35.0f),   // 4 开始减速爬坡
            new Vector3(33.0f, 0f, 41.5f),   // 5 缓行接近定点
            new Vector3(32.9f, 0f, 43.6f),   // 6 定点停车（拉手刹）
            new Vector3(32.9f, 0f, 45.5f),   // 7 半联动起步过坡顶
            new Vector3(33.0f, 0f, 47.5f),   // 8
            new Vector3(33.4f, 0f, 49.5f),   // 9
            new Vector3(33.8f, 0f, 50.5f)    // 10 终点（End 前）
        };
        _speeds = new float[] { 9, 9, 8, 7, 5, 4, 4, 5, 7, 8, 8 };
        _rev = new bool[] { false, false, false, false, false, false, false, false, false, false, false };
        _hold = new float[] { 0, 0, 0, 0, 0, 0, 2.5f, 0, 0, 0, 0 };
        _handbrake = new bool[] { false, false, false, false, false, false, true, false, false, false, false };
        _steps = new DemoStep[]
        {
            new DemoStep { atWp = 0, text = "① 起步：对正坡道，挂 D 档上坡，车身距右边线约 30 公分" },
            new DemoStep { atWp = 3, text = "② 接近定点时松油门，轻带刹车控制车速缓行上坡" },
            new DemoStep { atWp = 6, text = "③ 前保险杠对准定点标线——踩死刹车停稳，拉起手刹（定点完成）" },
            new DemoStep { atWp = 7, text = "④ 松手刹，轻踩油门半联动起步，防止车辆后溜" },
            new DemoStep { atWp = 9, text = "⑤ 越过坡顶后控制车速下坡，驶向出口完成项目" }
        };
    }

    /// <summary>
    /// 直角转弯：起点(13.2,-33.7)朝东 → 左转90° → 北向驶出（End 在 36.3,-18.8）。
    /// 路径由 appliedTune 程序化生成（调参模式下由 ZjzwTuner 注入；否则用内置默认值，
    /// 几何=标准直角转弯轨迹：直线到转角 → 四分之一圆弧(R=3.33=轴距2.6/tan38°) → 北向驶出）。
    /// </summary>
    private void BuildZjzwPath()
    {
        // 未注入调参参数时使用与原写死路径一致的默认几何
        ZjzwTune t = appliedTune;
        float entryZ = t != null ? t.entryZ : -33.5f;       // 入口直线 z（车道中线）
        float cornerX = t != null ? t.cornerX : 33.0f;      // 弧线起点 x（到转角横线才开始打方向）
        float radius = t != null ? t.radius : 3.33f;        // 圆弧半径（=轴距/tan38°，打死方向的转弯半径）
        float arcStep = t != null ? t.arcStep : 30f;        // 圆弧步进角（度）
        float exitEndZ = t != null ? t.exitEndZ : -19.2f;   // 出口终点 z（End 触发器前刹停）
        float vApproach = t != null ? t.vApproach : 6.5f;   // 入口直道目标车速（全程恒速 6.5：与 S 弯统一；弧线标定等效速度即 6.5）
        float vCorner = t != null ? t.vCorner : 6.5f;       // 弯中目标车速
        float vExit = t != null ? t.vExit : 6.5f;           // 出口直道目标车速

        // 几何闭环路线的真值（取自场景压线墙实测，比调参参数更可信）：
        //   入口道中心 z = (-35.35 + -31.6)/2 ≈ -33.48；出口道中心 x = (34.41 + 38.44)/2 ≈ 36.42
        //   内侧直角在 (34.4,-31.6)，所以转弯半径只能按下限走，落点必须压在出口道中心
        _routeCfg.entryZ = entryZ;
        _routeCfg.exitX = 36.42f;
        _routeCfg.exitEndZ = exitEndZ;
        _routeCfg.vEntry = vApproach;
        _routeCfg.vCorner = vCorner;
        _routeCfg.vExit = vExit;
        if (_kernel != null)
        {
            if (_kernel.wheelBase > 0.1f) _routeCfg.wheelBase = _kernel.wheelBase;
            if (_kernel.steerAngle > 1f) _routeCfg.maxSteerDeg = _kernel.steerAngle;
        }

        // 圆弧圆心 (cornerX, entryZ + radius)，起切点 (cornerX, entryZ)，终切点 (cornerX + radius, entryZ + radius)
        List<Vector3> wps = new List<Vector3>
        {
            new Vector3(13.2f, 0f, -33.7f),                 // 0 起点
            new Vector3(20.0f, 0f, entryZ + 0.1f),          // 1 直线中继
            new Vector3(26.5f, 0f, entryZ),                 // 2 直线中继
            new Vector3(cornerX, 0f, entryZ)                // 3 弧线起点：到转角横线才开始打方向
        };
        // 圆弧：φ 从 arcStep 到 90°，P(φ) = (cornerX + R·sinφ, entryZ + R - R·cosφ)
        int steps = Mathf.Max(1, Mathf.RoundToInt(90f / Mathf.Max(5f, arcStep)) - 1);
        for (int i = 1; i <= steps; i++)
        {
            float phi = (90f * i / (steps + 1)) * Mathf.Deg2Rad;
            wps.Add(new Vector3(cornerX + radius * Mathf.Sin(phi), 0f, entryZ + radius - radius * Mathf.Cos(phi)));
        }
        // 弧线 90° 终点：转正，开始北向直行
        wps.Add(new Vector3(cornerX + radius, 0f, entryZ + radius));
        float exitX = cornerX + radius;
        wps.Add(new Vector3(exitX + 0.07f, 0f, Mathf.Lerp(entryZ + radius, exitEndZ, 0.55f)));
        wps.Add(new Vector3(exitX + 0.07f, 0f, Mathf.Lerp(entryZ + radius, exitEndZ, 0.78f)));
        wps.Add(new Vector3(exitX + 0.07f, 0f, exitEndZ)); // 终点（End 前，不撞空气墙）

        _wps = wps.ToArray();
        int n = _wps.Length;
        int arcStart = 3, arcEnd = n - 4; // 弧线区间 [3, n-4]（含两端切点）
        _speeds = new float[n];
        _rev = new bool[n];
        _hold = new float[n];
        _handbrake = new bool[n];
        for (int i = 0; i < n; i++)
        {
            if (i < arcStart || i > arcEnd) _speeds[i] = (i >= n - 4) ? vExit : vApproach;
            else _speeds[i] = vCorner;
        }
        _steps = new DemoStep[]
        {
            new DemoStep { atWp = 0, text = "① 起步：挂 D 档缓慢前进，控制车速，沿车道中间偏右行驶" },
            new DemoStep { atWp = 2, text = "② 提前打开左转向灯，转过弯后再关闭", turnSignalOn = true },
            new DemoStep { atWp = arcStart, text = "③ 当车头盖即将盖过转角横线时，向左打死方向盘" },
            new DemoStep { atWp = (arcStart + arcEnd) / 2, text = "④ 慢速转过弯角，注意左后轮不要压到内侧直角" },
            new DemoStep { atWp = n - 3, text = "⑤ 车身即将转正时，迅速回正方向盘，驶出项目", turnSignalOff = true }
        };
    }

    // S 弯演示路径（世界坐标 x,z，行进方向：东入口臂 → 左弧 → 公切线 → 右弧 → 西出口臂）。
    // 由 Tuning/gen_qxxs_arcpath.py 解析生成：对边线真值反推的中心线做双圆弧拟合
    //（左弧 C1=(34.49,142.78) R1=7.01m，右弧 C2=(19.87,142.82) R2=7.21m，RMS<0.015m），
    // 内公切线连接两弧，贴外偏移 0.4m 径向烘焙（入口/出口 5m 坡道渐入渐出），0.4m 步长采样。
    // 解析路径曲率分段恒定，无折线伪影——替代旧「1.2m 折线+样条重采样」链路
    //（旧链路的交接台阶与样条过冲是第二弯入口先错打、出口猛甩舵的根因）。
    // 重新提取边线后：先跑 gen_qxxs_centerline.py 得中心线真值，再跑 gen_qxxs_arcpath.py 粘贴到这里。
    //
    // 【变方向关键边界点（解析切点，s=沿路径里程）】——打方向时机以此为准：
    //   起点  (41.70,111.00) 朝北，全长 86.5m
    //   s= 31.8m  A (41.89,143.19)  直道→左弧：开始向左打（前馈提前~1.5s 给舵）
    //   s= 53.4m  B1(27.28,144.08)  左弧→切线：保持左舵过切点后回正
    //   s= 56.8m  B2(27.17,140.65)  切线→右弧：回正后向右打一圈又1/4
    //   s= 78.9m  D (12.28,143.23)  右弧→直道：出弯回正
    private const string QxxsCenterline =
        "41.70,111.40;41.69,111.80;41.69,112.21;41.69,112.61;41.69,113.01;41.68,113.41;41.68,113.82;41.68,114.22;41.68,114.62;41.67,115.02;" +
        "41.67,115.42;41.67,115.83;41.67,116.23;41.66,116.63;41.66,117.03;41.66,117.44;41.66,117.84;41.65,118.24;41.65,118.64;41.65,119.05;" +
        "41.65,119.45;41.64,119.85;41.64,120.25;41.64,120.65;41.64,121.06;41.63,121.46;41.63,121.86;41.63,122.26;41.63,122.67;41.62,123.07;" +
        "41.62,123.47;41.62,123.87;41.62,124.27;41.61,124.68;41.61,125.08;41.61,125.48;41.61,125.88;41.60,126.29;41.60,126.69;41.60,127.09;" +
        "41.60,127.49;41.59,127.90;41.59,128.30;41.59,128.70;41.59,129.10;41.58,129.50;41.58,129.91;41.58,130.31;41.58,130.71;41.57,131.11;" +
        "41.57,131.52;41.57,131.92;41.57,132.32;41.56,132.72;41.56,133.12;41.56,133.53;41.56,133.93;41.55,134.33;41.55,134.73;41.55,135.14;" +
        "41.55,135.54;41.54,135.94;41.54,136.34;41.54,136.75;41.54,137.15;41.53,137.55;41.54,137.95;41.57,138.35;41.60,138.76;41.63,139.16;" +
        "41.66,139.56;41.69,139.96;41.72,140.37;41.75,140.77;41.78,141.17;41.81,141.57;41.84,141.98;41.87,142.38;41.90,142.78;41.89,143.19;" +
        "41.86,143.59;41.80,143.99;41.72,144.39;41.62,144.79;41.50,145.17;41.36,145.55;41.20,145.93;41.02,146.29;40.81,146.64;40.59,146.98;" +
        "40.35,147.31;40.10,147.62;39.82,147.92;39.53,148.21;39.23,148.48;38.91,148.73;38.58,148.96;38.23,149.18;37.87,149.37;37.51,149.55;" +
        "37.13,149.70;36.75,149.84;36.36,149.95;35.96,150.04;35.56,150.11;35.16,150.16;34.75,150.18;34.35,150.19;33.94,150.17;33.54,150.13;" +
        "33.14,150.07;32.74,149.98;32.35,149.87;31.96,149.75;31.58,149.60;31.22,149.43;30.86,149.24;30.51,149.03;30.17,148.80;29.85,148.56;" +
        "29.54,148.29;29.24,148.01;28.96,147.72;28.70,147.41;28.46,147.08;28.23,146.75;28.02,146.40;27.83,146.04;27.67,145.67;27.52,145.29;" +
        "27.39,144.90;27.28,144.51;27.28,144.08;27.28,143.64;27.28,143.21;27.28,142.77;27.28,142.34;27.27,141.91;27.27,141.47;27.17,140.65;" +
        "27.04,140.27;26.90,139.89;26.73,139.52;26.55,139.17;26.35,138.82;26.13,138.48;25.89,138.16;25.64,137.85;25.37,137.55;25.08,137.27;" +
        "24.78,137.00;24.46,136.75;24.14,136.51;23.80,136.30;23.45,136.10;23.09,135.92;22.72,135.76;22.34,135.62;21.96,135.50;21.57,135.40;" +
        "21.18,135.32;20.78,135.26;20.38,135.22;19.98,135.21;19.58,135.21;19.17,135.24;18.78,135.29;18.38,135.35;17.99,135.44;17.60,135.55;" +
        "17.22,135.68;16.85,135.83;16.48,136.00;16.13,136.19;15.78,136.40;15.45,136.62;15.13,136.86;14.82,137.12;14.53,137.40;14.25,137.69;" +
        "13.99,137.99;13.74,138.31;13.51,138.64;13.30,138.98;13.10,139.33;12.93,139.70;12.77,140.07;12.64,140.45;12.52,140.83;12.43,141.22;" +
        "12.36,141.62;12.30,142.01;12.27,142.42;12.26,142.82;12.28,143.23;12.31,143.65;12.33,144.07;12.36,144.48;12.38,144.90;12.40,145.32;" +
        "12.43,145.73;12.45,146.15;12.48,146.57;12.50,146.98;12.53,147.40;12.55,147.82;12.55,148.22;12.55,148.62;12.55,149.02;12.55,149.42;" +
        "12.55,149.82;12.55,150.22;12.55,150.62;12.55,150.80";

    /// <summary>
    /// 曲线行驶：起点(41.7,111)朝北 → 东入口臂 → 左半圆（第一弯）→ 右半圆（第二弯）→ 西出口臂至 End 前。
    /// 路径 = 中心线 + 教学要点「左转靠右、右转靠左」的贴外偏移（outerBias）：
    /// 贴外走转弯半径大、好修方向，并把后轮内切（内轮差约 0.5m）的余量留在弯道内侧。
    /// 字幕按教练标准打法：左角碰右线左打一圈又1/4 → 中段回正 → 右角碰左线右打一圈又1/4 → 出弯回正。
    /// </summary>
    private void BuildQxxsPath()
    {
        QxxsTune t = appliedQxxsTune;
        float outerBias = t != null ? t.outerBias : 0f;     // 额外贴外偏移（米）：0.4m 已烘焙进解析路径，此参数仅作对照实验用
        float vApproach = t != null ? t.vApproach : 6f;     // 入口直道目标车速（全程恒速 6：与直角转弯统一）
        float vCurve    = t != null ? t.vCurve : 6f;        // 弯中目标车速
        float vExit     = t != null ? t.vExit : 6f;         // 出口直道目标车速

        // 1) 解析中心线
        string[] pairs = QxxsCenterline.Split(';');
        int n = pairs.Length;
        Vector3[] basePts = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            string[] xz = pairs[i].Split(',');
            basePts[i] = new Vector3(float.Parse(xz[0]), 0f, float.Parse(xz[1]));
        }

        // 2) 逐点转向量（叉积>0=左转），±3 点平滑后限幅映射为贴外偏移量。
        //    直线段转向量≈0 自动不偏移；半圆段饱和到 outerBias。
        float[] crossArr = new float[n];
        for (int i = 1; i < n - 1; i++)
        {
            float ax = basePts[i].x - basePts[i - 1].x, az = basePts[i].z - basePts[i - 1].z;
            float bx = basePts[i + 1].x - basePts[i].x, bz = basePts[i + 1].z - basePts[i].z;
            crossArr[i] = ax * bz - az * bx; // >0 = 左转
        }
        float[] smooth = new float[n];
        for (int i = 0; i < n; i++)
        {
            float sum = 0f; int cnt = 0;
            for (int j = Mathf.Max(1, i - 3); j <= Mathf.Min(n - 2, i + 3); j++) { sum += crossArr[j]; cnt++; }
            smooth[i] = cnt > 0 ? sum / cnt : 0f;
        }
        _wps = new Vector3[n];
        float[] amountArr = new float[n];
        for (int i = 0; i < n; i++)
        {
            amountArr[i] = outerBias * Mathf.Clamp(smooth[i] * 8f, -1f, 1f);
        }
        // 2.2) 偏移限斜率：两弯交接处偏移方向翻转（+0.4 → -0.4），瞬间翻转会在路径上留下
        //      0.8m 横向台阶 → 样条成 S 甩尾、交接处前馈虚高（0.74 > 弯中稳态 0.50）、
        //      方向盘在第二弯入口先错打左舵。限制相邻点偏移增量 ≤0.1m（1.2m 步长 →
        //      最大横向坡度 0.083/m），交接处约 10m 平滑过渡，弯道内偏移饱和段不受影响。
        for (int i = 1; i < n; i++)
            amountArr[i] = Mathf.Clamp(amountArr[i], amountArr[i - 1] - 0.1f, amountArr[i - 1] + 0.1f);
        for (int i = 0; i < n; i++)
        {
            int a = Mathf.Max(0, i - 1), b = Mathf.Min(n - 1, i + 1);
            float tx = basePts[b].x - basePts[a].x, tz = basePts[b].z - basePts[a].z;
            float len = Mathf.Max(Mathf.Sqrt(tx * tx + tz * tz), 0.001f);
            // 行进方向右侧法线 = (tz,-tx)/|t|（朝北行驶时指东）；左转弯取正偏移=向右=贴外
            _wps[i] = new Vector3(basePts[i].x + tz / len * amountArr[i], 0f, basePts[i].z - tx / len * amountArr[i]);
        }

        // 2.5) Catmull-Rom 样条重采样到 0.4m：把折线拐点圆滑成连续曲线。
        //      折线（1.2m 步长）的前瞻点跨段跳变是方向盘抖动的源头，样条化从根上消除
        _wps = ResampleCatmullRom(_wps, 0.4f);

        // 3) 相位锚点（与生成脚本同一几何判据），用于分段车速和教学字幕
        int curveStart = 0, topMid = 0, diagMid = 0, secondBend = 0, exitArm = n - 1;
        for (int i = 0; i < n; i++)
        {
            float x = _wps[i].x, z = _wps[i].z;
            if (curveStart == 0 && z >= 144f && x > 38f) curveStart = i;   // 东臂顶：第一弯（左）开始
            if (topMid == 0 && x <= 34.5f && z >= 148f) topMid = i;        // 顶部：沿外线走
            if (diagMid == 0 && x <= 26.5f && z <= 143.5f) diagMid = i;    // 中段：回正换向
            if (secondBend == 0 && x <= 19.5f && z <= 137f) secondBend = i;// 第二弯（右）开始
            if (exitArm == n - 1 && x <= 14.5f && z >= 141f) exitArm = i;  // 西臂：出弯回正
        }

        // 4) 分段车速 / 全程 D 档 / 无停车点（数组长度用重采样后的点数，n 是中心线原始点数）
        int m = _wps.Length;
        _speeds = new float[m];
        for (int i = 0; i < m; i++)
            _speeds[i] = i < curveStart ? vApproach : (i < exitArm ? vCurve : vExit);
        _rev = new bool[m];
        _hold = new float[m];
        _handbrake = new bool[m];
        _steps = new DemoStep[]
        {
            new DemoStep { atWp = 0, text = "① 车头对准弯道入口，略靠右侧边线低速进入——第一弯向左，记住左转靠右" },
            new DemoStep { atWp = curveStart, text = "② 车头左角即将碰到右边线：向左打一圈又四分之一方向" },
            new DemoStep { atWp = topMid, text = "③ 保持车头左角沿着右边线走，有偏差小角度微调，弯中不许停车" },
            new DemoStep { atWp = diagMid, text = "④ 车头左角即将碰到左边线先回正；右角即将碰到左边线时向右打一圈又四分之一" },
            new DemoStep { atWp = secondBend, text = "⑤ 保持车头右角沿左边线行驶（右转靠左），看后视镜留意右后轮别压线" },
            new DemoStep { atWp = exitArm, text = "⑥ 车头出弯，左后轮与边线平行时回正方向，缓慢驶出弯道" }
        };
    }

    private void FireStepsAt(int wpIndex)
    {
        while (_stepIndex < _steps.Length && _steps[_stepIndex].atWp <= wpIndex)
        {
            DemoStep step = _steps[_stepIndex];
            // 调参轮不弹教学字幕（进度字幕由 ZjzwTuner 显示），转向灯照常联动
            if (!tuningMode) ShowCaption(step.text, 0f);

            if (step.turnSignalOn && _turnLight != null && _turnLight.CTL != TurnLight.CarTurnLight.Left)
            {
                try { _turnLight.SetLeft(); } catch { }
            }
            if (step.turnSignalOff && _turnLight != null && _turnLight.CTL == TurnLight.CarTurnLight.Left)
            {
                try { _turnLight.SetLeft(); } catch { }
            }
            _stepIndex++;
        }
    }

    // ============================================================
    //  调参模式接口（由 ZjzwTuner 调用；参数热注入，全程不改代码）
    // ============================================================

    /// <summary>
    /// 开始一轮调参：注入候选参数并立即从标准起点开始自动演示。
    /// 由 ZjzwTuner 在每轮场景就绪后调用（轮结束自动重载场景，分数/压线墙/计时器全部随场景重建）。
    /// </summary>
    public void StartTuningRound(ZjzwTune tune, string caption)
    {
        appliedTune = tune;
        ApplyTune(tune);
        BeginTuningRound(caption);
    }

    /// <summary>开始一轮曲线行驶调参：注入候选参数并立即从标准起点开始自动演示（QxxsTuner 调用）</summary>
    public void StartQxxsTuningRound(QxxsTune tune, string caption)
    {
        appliedQxxsTune = tune;
        ApplyQxxsTune(tune);
        BeginTuningRound(caption);
    }

    /// <summary>调参轮公共启动流程（参数注入完成后调用）</summary>
    private void BeginTuningRound(string caption)
    {
        tuningMode = true;
        roundFinished = false;
        lastResult = null;
        _trace.Clear();
        _maxDev = 0f;
        _sampleTimer = 0f;
        _roundStartScore = Car_Information.GetScore();
        _roundStartTime = Time.time;
        ShowCaption(caption, 0f);
        StartDemo();
        if (_state != DemoState.Driving)
        {
            // 起点对象找不到等异常：直接按失败记录
            EndTuningRound("no-start");
        }
    }

    /// <summary>曲线行驶调参参数应用（常规纯跟踪参数；路径几何在 BuildQxxsPath 里读取 appliedQxxsTune）</summary>
    private void ApplyQxxsTune(QxxsTune t)
    {
        if (t == null) return;
        _arriveDist = t.arriveDist;
        _lookForward = t.lookForward;
        _steerGain = t.steerGain;
        _steerSpeedFwd = t.steerSpeedFwd;
        _throttleMax = t.throttleMax;
    }

    /// <summary>把候选参数应用到路径跟踪字段（BuildZjzwPath 在 StartDemo 里读取 appliedTune）</summary>
    private void ApplyTune(ZjzwTune t)
    {
        if (t == null) return;
        _arriveDist = t.arriveDist;
        _lookForward = t.lookForward;
        _steerGain = t.steerGain;
        _steerSpeedFwd = t.steerSpeedFwd;
        _throttleMax = t.throttleMax;
        _zjzwLockArc = t.lockArc;
        _zjzwCornerX = t.cornerX;
        _zjzwLockLead = t.lockLead;
        _zjzwLockSpeed = t.lockSpeed;
        _zjzwLockSteer = t.lockSteer;
        _zjzwReleaseYaw = t.releaseYaw;
        _zjzwDynamic = t.dynamicRoute;
        _routeCfg.entryLaneOffset = t.routeEntryOffset;
        _routeCfg.turnBias = t.routeTurnBias;
        if (t.routeRadiusMax > 1f) _routeCfg.radiusMax = t.routeRadiusMax;
    }

    /// <summary>结束一轮调参并写出结果（outcome: pass / deduct / settlement / timeout / stuck / no-start）</summary>
    private void EndTuningRound(string outcome)
    {
        _state = DemoState.Idle;
        if (_kernel != null) _kernel.Hardware_ReleaseControl();
        ResumePlayerInput();
        SetButtonText("▶ 演示");

        Vector3 pos = transform.position;
        float endDist = _wps != null && _wps.Length > 0 ? FlatDist(pos, _wps[_wps.Length - 1]) : -1f;
        lastResult = new ZjzwRoundResult
        {
            outcome = outcome,
            seconds = Time.time - _roundStartTime,
            finalScore = Car_Information.GetScore(),
            endDist = endDist,
            maxDev = _maxDev,
            endX = pos.x,
            endZ = pos.z,
            endYaw = transform.eulerAngles.y,
            trace = _trace.ToArray()
        };
        roundFinished = true;
        // 失败轮里 MyGameManager 可能已把 timeScale 置 0（结算暂停），重载场景前必须恢复
        if (Time.timeScale == 0f) Time.timeScale = 1f;
    }

    /// <summary>车到参考路径折线的最短距离（调参轮统计轨迹偏差用；只搜当前点附近几段）</summary>
    private float DistanceToPath(Vector3 pos)
    {
        if (_wps == null || _wps.Length < 2) return 0f;
        int firstSeg = Mathf.Max(0, _wpIndex - 1);
        int lastSeg = Mathf.Min(_wps.Length - 2, _wpIndex + 2);
        float best = float.MaxValue;
        for (int i = firstSeg; i <= lastSeg; i++)
        {
            Vector3 a = _wps[i];
            Vector3 b = _wps[i + 1];
            float abx = b.x - a.x, abz = b.z - a.z;
            float len2 = abx * abx + abz * abz;
            float t = 0f;
            if (len2 > 0.0001f)
            {
                t = Mathf.Clamp01(((pos.x - a.x) * abx + (pos.z - a.z) * abz) / len2);
            }
            float dx = pos.x - (a.x + abx * t);
            float dz = pos.z - (a.z + abz * t);
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            if (d < best) best = d;
        }
        return best;
    }

    // ============================================================
    //  玩家输入接管（唯一暂停的东西；考试判定全部不动）
    // ============================================================
    private void PausePlayerInput()
    {
        _pausedInputs.Clear();
        CollectDisabled(FindObjectsOfType<HardwareInputController>(), _pausedInputs);
    }

    private void ResumePlayerInput()
    {
        for (int i = 0; i < _pausedInputs.Count; i++)
        {
            if (_pausedInputs[i] != null) _pausedInputs[i].enabled = true;
        }
        _pausedInputs.Clear();
    }

    private void CollectDisabled(Behaviour[] found, List<Behaviour> bucket)
    {
        for (int i = 0; i < found.Length; i++)
        {
            if (found[i] != null && found[i].enabled)
            {
                found[i].enabled = false;
                bucket.Add(found[i]);
            }
        }
    }

    // ============================================================
    //  运行时 UI（字幕 + 演示按钮，不改任何场景文件）
    // ============================================================
    private void EnsureUI()
    {
        if (_canvas != null) return;

        GameObject cgo = new GameObject("DemoDriverCanvas");
        _canvas = cgo.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 500;
        CanvasScaler scaler = cgo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280f, 720f);
        scaler.matchWidthOrHeight = 0.5f;
        cgo.AddComponent<GraphicRaycaster>();

        Font font = FindChineseFont();

        // ---- 字幕条（顶部居中） ----
        GameObject bgGo = new GameObject("CaptionBg");
        bgGo.transform.SetParent(cgo.transform, false);
        _captionBg = bgGo.AddComponent<Image>();
        _captionBg.color = new Color(0f, 0f, 0f, 0.55f);
        // 字幕条不接收点击，防止挡住下方的设置/视角按钮
        _captionBg.raycastTarget = false;
        RectTransform bgRt = bgGo.GetComponent<RectTransform>();
        // 右侧留到 0.90：游戏设置按钮（齿轮）在最右 ~7% 区域，不能盖住
        bgRt.anchorMin = new Vector2(0.05f, 0.84f);
        bgRt.anchorMax = new Vector2(0.90f, 0.94f);
        bgRt.offsetMin = Vector2.zero;
        bgRt.offsetMax = Vector2.zero;

        GameObject txtGo = new GameObject("CaptionText");
        txtGo.transform.SetParent(bgGo.transform, false);
        _caption = txtGo.AddComponent<Text>();
        _caption.font = font;
        _caption.color = Color.white;
        _caption.alignment = TextAnchor.MiddleCenter;
        _caption.horizontalOverflow = HorizontalWrapMode.Wrap;
        _caption.verticalOverflow = VerticalWrapMode.Overflow;
        _caption.resizeTextForBestFit = true;
        _caption.resizeTextMinSize = 16;
        _caption.resizeTextMaxSize = 34;
        _caption.raycastTarget = false;
        Outline outline = txtGo.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
        RectTransform txtRt = txtGo.GetComponent<RectTransform>();
        txtRt.anchorMin = Vector2.zero;
        txtRt.anchorMax = Vector2.one;
        txtRt.offsetMin = new Vector2(12f, -4f);
        txtRt.offsetMax = new Vector2(-12f, 4f);

        // ---- 演示按钮（右侧、齿轮设置按钮下方，移动端也能点） ----
        // 游戏的 Button_Setting 齿轮占屏幕最右上 76x77（距顶 40px），
        // 本按钮放在它正下方，避免遮挡"选项"入口
        GameObject btnGo = new GameObject("DemoButton");
        btnGo.transform.SetParent(cgo.transform, false);
        Image btnImg = btnGo.AddComponent<Image>();
        btnImg.color = new Color(0.16f, 0.5f, 0.95f, 0.92f);
        _demoButton = btnGo.AddComponent<Button>();
        _demoButton.targetGraphic = btnImg;
        RectTransform btnRt = btnGo.GetComponent<RectTransform>();
        btnRt.anchorMin = new Vector2(0.85f, 0.74f);
        btnRt.anchorMax = new Vector2(0.99f, 0.82f);
        btnRt.offsetMin = Vector2.zero;
        btnRt.offsetMax = Vector2.zero;
        _demoButton.onClick.AddListener(ToggleDemo);

        GameObject btnTxtGo = new GameObject("Text");
        btnTxtGo.transform.SetParent(btnGo.transform, false);
        _buttonText = btnTxtGo.AddComponent<Text>();
        _buttonText.font = font;
        _buttonText.color = Color.white;
        _buttonText.alignment = TextAnchor.MiddleCenter;
        _buttonText.resizeTextForBestFit = true;
        _buttonText.resizeTextMinSize = 14;
        _buttonText.resizeTextMaxSize = 30;
        RectTransform btnTxtRt = btnTxtGo.GetComponent<RectTransform>();
        btnTxtRt.anchorMin = Vector2.zero;
        btnTxtRt.anchorMax = Vector2.one;
        btnTxtRt.offsetMin = Vector2.zero;
        btnTxtRt.offsetMax = Vector2.zero;

        SetButtonText("▶ 演示");
    }

    /// <summary>复用场景里现成 UI 的字体（保证中文字形与游戏一致），找不到再用内置 Arial</summary>
    private Font FindChineseFont()
    {
        Text[] texts = FindObjectsOfType<Text>();
        for (int i = 0; i < texts.Length; i++)
        {
            if (texts[i] != null && texts[i].font != null) return texts[i].font;
        }
        return Resources.GetBuiltinResource<Font>("Arial.ttf");
    }

    private void ShowCaption(string text, float duration)
    {
        EnsureUI();
        _caption.text = text;
        _captionBg.gameObject.SetActive(true);
        _captionHideAt = duration > 0f ? Time.time + duration : -1f;
    }

    private void HideCaption()
    {
        if (_captionBg != null) _captionBg.gameObject.SetActive(false);
    }

    private void SetButtonText(string text)
    {
        if (_buttonText != null) _buttonText.text = text;
    }

    /// <summary>外部（ZjzwTuner）显示字幕的入口；批次结束时显示汇总提示用</summary>
    public void ShowExternalCaption(string text, float duration)
    {
        ShowCaption(text, duration);
    }

    // ============================================================
    //  工具
    // ============================================================
    /// <summary>欧拉角归一化到 [-180,180]（绕过 0/360 跳变；本场景东=90 北=0 左转递减）</summary>
    private static float NormYaw(float y)
    {
        y %= 360f;
        if (y > 180f) y -= 360f;
        if (y < -180f) y += 360f;
        return y;
    }

    private static float FlatDist(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}

/// <summary>
/// DemoDriver 安装看门狗：常驻场景（DontDestroyOnLoad），每 0.5 秒自检一次。
/// 覆盖场景：Play 中重编译（域重载丢失静态订阅）、按 R 重载场景、异常退出考场等。
/// </summary>
public class DemoDriverInstaller : MonoBehaviour
{
    private float _timer = 0.2f;

    private void Update()
    {
        _timer -= Time.deltaTime;
        if (_timer > 0f) return;
        _timer = 0.5f;

        Scene s = SceneManager.GetActiveScene();
        if (s.name == "Exam Mode")
        {
            // 反射调用私有静态方法不必要：直接放一个公开入口
            DemoDriver.EnsureInstalled();
        }
    }
}
