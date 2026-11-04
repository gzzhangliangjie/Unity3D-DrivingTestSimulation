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
    private float _arriveDistRev = 1.0f;  // 倒车段专用到达距离（缓慢倒车时 2.2m 会让 wp 虚推进、车进不了库）
    private float _lookForward = 3.0f;   // 前进纯跟踪前瞻距离（米）；太大会提前切弯内侧
    private float _lookReverse = 2.6f;   // 倒车纯跟踪前瞻距离（倒车精度要求高）
    private float _steerGain = 2.4f;     // 纯跟踪转向增益（角度误差 → 归一化转向）
    private float _steerSpeedFwd = 2.4f; // 前进时方向盘转动速率（归一化/秒）
    [System.NonSerialized] public float _crossGain = 1.1f; // Stanley 横向误差增益（偏移米数 → 额外打角），调参器可覆盖
    private float _throttleMax = 1.0f;   // 油门上限（半坡爬坡无力时调大；2026-11-04 提到 1.0 才能冲上坡顶）

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

    // ---- 全科连续路线专用（Default 模式）----
    private SelectPass.Sites _activeSite;   // 当前行驶段所属科目（单科时恒等于 _sites；全科时随路点切换）
    private SelectPass.Sites[] _segSite;    // 每个路点所属科目（仅 Default 使用，用于分段切换科目专属参数）
    private bool[] _segConn;                // 每个路点是否处于"科间连接段"（连接段只走纯跟踪，禁用各科专属转向/精准停点）

    // ---- 全科分段基址（Default）----
    // 科目专属动作（倒库满舵点、侧方摆正/出库左打满）在单科路点里是按**段内索引**标定的：
    // 侧方 wp14~17 摆正、wp18~26 左打满出库；倒库 wp7~16 / 26~35 满舵、wp16~22 / 35~41 直出。
    // 全科把五科首尾相接后，这些索引带上了前面科目的基址（侧方段起点≈47），
    // 直接用全局 _wpIndex 判断会让条件全部落空 —— 实测表现为侧方停车出库时左打满不生效，
    // 车头顶到库位东壁 x=-9.83 压线扣 100。故统一换算成段内相对索引。
    private static readonly SelectPass.Sites[] _defaultOrder =
    {
        SelectPass.Sites.Dcrk, SelectPass.Sites.Cftc, SelectPass.Sites.Zjzw,
        SelectPass.Sites.Bpqb, SelectPass.Sites.Qxxs
    };
    private int[] _segStart;                // 每科段首路点在 _wps 中的全局索引（与 _defaultOrder 同序）
    private int _curSegStart;               // 当前所在科目段的段首全局索引
    /// <summary>当前路点在本科目段内的相对索引（单科模式恒等于 _wpIndex）。</summary>
    private int RelWp { get { return _wpIndex - _curSegStart; } }

    // 全科连接段中间路点（可编辑，仅供校准）。索引（真实考试顺序）：
    //   [0] 倒车入库→侧方停车  [1] 侧方停车→直角转弯  [2] 直角转弯→半坡起步  [3] 半坡起步→曲线行驶。
    // 每个元素是该连接段"严格中间"路点（不含两端科目各自的终点/起点）。留 null = 用两端点直线插值（每 ~15m 一个点）。
    // 已按 ObstacleProbe 场景碰撞体扫描校准：
    //   [0][1][2] 直连走廊无障碍（锥桶都在走廊北侧边界，最近净空 ≥2m；全科模式下非曲线压线墙自动禁用）；
    //   [3] 坡顶→曲线入口：按地面归属扫描（Tuning/ground_probe.txt）走"路面"，别压草坪——
    //       坡顶向北的南北车道只有 x∈[28,36] 是路面（x>37 全是 Grass 瓦片），
    //       绕开 RoadBlock_D(2)(3)（x 37.40~38.08，z 95.90~101.58）后再向东并入曲线入口 (41.7,111.4)。
    private static readonly Vector3[][] _defaultConnectors = new Vector3[][]
    {
        null,                              // 0 倒库→侧方：直连
        null,                              // 1 侧方→直角：直连
        null,                              // 2 直角→半坡：直连（出口路向北即坡道入口）
        new Vector3[]                      // 3 半坡→曲线：贴车道中线北上，过全部路障后再东并
        {
            new Vector3(34.0f, 0f, 52.0f),   // 坡顶下来先回车道中线（路面 x[28,36]）
            new Vector3(34.0f, 0f, 68.0f),
            new Vector3(34.0f, 0f, 84.0f),
            new Vector3(34.3f, 0f, 96.0f),   // 过 RoadBlock_D(3)（x 37.40~38.08，z 95.90~96.72）
            new Vector3(34.8f, 0f, 103.0f),  // 过 RoadBlock_D(2)（z 100.75~101.58）后再动方向：
                                             // 实测若提前在 z94 就开始东并，直达 (37,104) 的连线会让
                                             // 车身右缘在 z101 处离墩子只剩 0.1m（等于擦着过）
            new Vector3(37.5f, 0f, 107.0f),
            new Vector3(39.8f, 0f, 109.5f),  // 并入曲线入口 (41.7,111.4)
        },
    };

    // ---------------- 运行状态 ----------------
    private DemoState _state = DemoState.Idle;
    private DrivePhase _phase = DrivePhase.Run;
    private float _steer;           // 当前归一化转向输入 -1..1
    private float _steerTargetSmooth; // 转向目标低通滤波值（抑制前瞻点跨段跳变导致的方向盘抖动）
    private float _driveTime;         // 本轮演示已行驶时长（起步斜坡用：起步 1s 内限制转向幅度）
    private float _lastFF;            // 最近一帧的曲率前馈舵量（遥测诊断用）
    private bool _ffSpanFix;          // 前馈曲率按「首末弦中点实际间距」修正（离散路点会低估一半，仅 Dcrk 启用）
    private float _logTimer = 0f;   // 调试遥测计时（调完删）
    private bool _gearReverse;      // 当前档位是否 R（倒车）
    private bool _leftStart;        // 是否已离开起点区域（防止起步当帧跳点）
    private bool _pendingShift;     // 刹停后需要换档
    private bool _shiftTargetRev;   // 刹停后要挂的档位（真车：停稳→挂档→起步，不等起步再刹一次）
    private bool _pendingFinish;    // 刹停后结束演示
    private float _holdRemaining;   // 当前停车保持剩余秒数
    private float _shiftTimer;      // 换档等待计时
    private float _stuckTimer;      // 防卡死计时
    private bool _demoUsed;         // 用过演示后 R 键才生效

    // ---- 精准停点（2026-11-04 新增，仅侧方/半坡启用）----
    // 原逻辑：推进进 hold 路点的瞬间就 BeginStop，而推进发生在距"上一个"路点
    // _arriveDist(2.2m) 处 → 实际停点比 hold 路点提前 2~7m。侧方停车因此停不进
    // point_left 2m 窗（倒车目标落在车后方→纯跟踪全反→满舵甩头压线），
    // 半坡定点停不进 Det_BPQB 的 1.2m 判定窗。启用后：行驶到贴近 hold 点
    // StopReach 内才刹停，停点误差 <0.5m。Dcrk 的既有停点已按旧提前量标定，
    // 保持旧行为不动（_preciseStop 只在 Cftc/Bpqb 构建路线时置 true）。
    private bool _preciseStop;      // 是否启用精准停点
    private int _stopAtIndex = -1;  // 精准停点目标路点（-1=无）
    private float _stopAtLastD = 999f; // 上一帧距停点距离（不减反增=已越过→强制刹停兜底）
    private float _stopReach = 0.45f;  // 贴近停点的判定距离（米）；Cftc 入库段收窄到 0.25 防倒过头撞后墙

    // ---- 侧方入库「位姿收车」（v29）----
    // 不再用「距路点多少米」收车：那个量随前瞻/路点疏密漂移 ±0.3m，而 Det_CFTC 的入库窗
    // Ab = 车心z + 32.806 ≤ 1.65 → 车心z ≤ -31.156，库底又在 ≈-31.7，可用带宽只有 0.5m。
    // 直接按车心 z 收车，停点与转向参数彻底解耦：以后改前瞻/改路点稀疏都不会把停点带偏。
    private bool _cftcStopByPose;        // 侧方入库按位姿收车（仅侧方段 + 倒车档生效）
    private float _cftcStopZ = -31.15f;  // 深度校验：车心 z ≤ 此值才算车身进了库深范围（判定 Ab≤1.65）
    // 半坡定点：Det_BPQB 的定点窗只有 30cm 宽（车心 z ∈ [40.88,41.18]），
    // 靠距离触发硬刹的滑行量会漂出窗（实测一轮 40.95 合格、一轮 40.55 判中途停车），故按位姿收车。
    private bool _bpqbStopByPose;
    private float _bpqbStopZ = 40.95f;   // 车心 z ≥ 此值即刹停；坡上 3.5km/h 蠕行滑行 ≈0.15m → 停稳 ≈41.1（窗 [40.88,41.18] 内）
    // 收车点不写死坐标：就是「倒车段最后一个路点」的 x（路线自己写着停在哪），
    // 沿行驶方向 +0.12m 作为刹车余量。见 CftcStopX()。
    private float CftcStopX()
    {
        int i = Mathf.Clamp(_wpIndex, 0, _wps.Length - 1);
        while (i + 1 < _rev.Length && _rev[i + 1]) i++;
        return _wps[i].x + 0.12f;
    }
    // 倒车专项（仅 _preciseStop 路线生效）：侧方停车倒车段纯跟踪会持续偏转，
    // 10m 内车头从 90° 掉到 45°（车尾下扫 1.5m 撞后墙）。拉长前瞻稳住直线跟踪 +
    // 限制倒车舵量上限防"驶向新路点瞬间"的方向尖峰饱和。
    private float _lookReverseOverride = -1f; // >0 时覆盖 _lookReverse
    private float _revSteerCap = 1f;          // 倒车舵量上限（1=不限制）

    // ---- 倒车入库「直接打满」点位（v27）----
    // 挂 R 后先沿通道直退，车心到打满点立刻满舵，保持到车身与库位平行再回正。
    // 点位按下式取：打满点 x = 库中心 x ∓ 满舵实际半径（库中心 = -71.60，实测满舵半径 ≈4.6m）。
    // 想微调入库左右位置只改这两个数（第一次右倒库在行驶线东侧打满，第二次西侧镜像）。
    private float _dcrkLockX = -67.4f;   // 第一次（右倒库）打满点：车心 x 小于此值即满舵
    private float _dcrkLockX2 = -76.3f;  // 第二次（镜像倒库）打满点：车心 x 大于此值即满舵

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
        // Default（全图考试）也装：提供与其他五科一致的"开启指引"按钮，演示车按真实连续路线把五科串起来开一遍

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
        // 先清上次残留；BuildDcrkWps / BuildCftcPath / SyncSegParams 会按科目设置倒车前瞻。
        // 这一行必须放在 BuildPath() 之前——放在后面会把倒库专用的短前瞻重置掉，
        // 车重新按 2.6m 前瞻切内弯（只往西走、不南下，车尾扫库口横墙西端）。
        _lookReverseOverride = -1f;
        _revSteerCap = 1f;   // 残留清理（BuildDcrkWps 会按科目覆盖，v23 教训：建路后的重置会冲掉 0.88）
        _ffSpanFix = false;
        _cftcStopByPose = false;
        _bpqbStopByPose = false;
        if (!BuildPath()) return;
        _activeSite = _sites; // 单科恒等于 _sites；全科由 SyncSegParams 按路点动态切换

        // 1) 把车传回项目起点（无论玩家刚才开到哪，演示永远从标准起点开始）
        //    全科连续路线从第一科（倒车入库）起点开始，故 Default 用 "Dcrk" 起点对象
        string spawnName = (_sites == SelectPass.Sites.Default) ? "Dcrk" : _sites.ToString();
        GameObject spawnObj = GameObject.Find(spawnName);
        if (spawnObj == null)
        {
            ShowCaption("演示启动失败：找不到项目起点对象 " + spawnName, 5f);
            return;
        }
        Rigidbody rb = _kernel.GetComponentInParent<Rigidbody>();
        if (rb != null)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            // 全图考试（Default）不挪车：MyGameManager 在 Default 下保持玩家车原位，
            // 连续路线的起点就是全图考试自己的入口。单科演示才回各自的标准起点。
            if (_sites != SelectPass.Sites.Default)
            {
                rb.position = spawnObj.transform.position;
                rb.rotation = spawnObj.transform.rotation;
            }
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
        ExamJudge.DrivingDone = false;   // 新一轮演示：恢复中途停车判定
        _holdRemaining = 0f;
        _shiftTimer = 0f;
        _stopAtIndex = -1;
        _stopAtLastD = 999f;
        // _lookReverseOverride 不在这里重置：BuildDcrkWps / BuildCftcPath 在 BuildPath()
        // 里按科目设好了值，重置会让倒车段退回 2.6m 前瞻而切内弯。
        // _revSteerCap / _ffSpanFix 同理不在这里重置（v23 实测：这行把 Dcrk 的 0.88 钳制
        // 冲掉，弧中舵量又打到 1.0，车尾甩过头扫库口东翼）。残留清理已移到 StartDemo() 开头。
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
        // 终点收车：路线已走完，之后这次停稳不是「中途停车」。
        // 检测器只看 Speed==0，会在这 1 秒后扣 100（实测直角转弯单科演示跑完被扣 100）。
        if (finish) ExamJudge.DrivingDone = true;
        _pendingShift = needShift;
        _pendingFinish = finish;
        _holdRemaining = holdSeconds;
        if (needShift)
        {
            // 停稳就挂好档（真车操作顺序）：停点是 hold 路点时，目标档位取「下一段」的档，
            // 而不是当前段 —— 否则起步那一帧还挂在 R 上，车会先往后溜半米再刹车换 D，
            // 视觉上就是「出库先回一下」。
            int i = Mathf.Clamp(_wpIndex, 0, _wps.Length - 1);
            if (_rev != null && _rev.Length == _wps.Length && _rev[i] == _gearReverse && i + 1 < _wps.Length)
                i++;
            if (_rev != null && _rev.Length == _wps.Length) _shiftTargetRev = _rev[i];
        }
        _phase = DrivePhase.BrakeToStop;
    }

    /// <summary>全科连续路线：根据当前路点所属科目，动态切换转向/精准停点等"科目专属"参数。
    /// 单科模式不走这里（参数由各自的 Build*Path 固定设置，_activeSite 恒等于 _sites）。</summary>
    private void SyncSegParams(int wpIndex)
    {
        if (_sites != SelectPass.Sites.Default || _segSite == null) return;
        int idx = Mathf.Clamp(wpIndex, 0, _segSite.Length - 1);
        SelectPass.Sites seg = _segSite[idx];
        _activeSite = seg;

        // 段内相对索引基址：科目专属动作按「本科目第几个路点」判断（见 _defaultOrder 注释）
        int si = System.Array.IndexOf(_defaultOrder, seg);
        _curSegStart = (_segStart != null && si >= 0 && si < _segStart.Length) ? _segStart[si] : 0;

        // 连接段：只走纯跟踪，禁用一切科目专属转向与精准停点
        if (_segConn != null && idx < _segConn.Length && _segConn[idx])
        {
            _preciseStop = false;
            _stopReach = 0.45f;
            _lookReverseOverride = -1f;
            _revSteerCap = 1f;
            _zjzwDynamic = false;
            _zjzwLockArc = false;
            _curSegStart = -1000000;   // 让 RelWp 落到所有区间之外，彻底杜绝专属动作误触发
            return;
        }

        // 复位默认值，再按科目覆盖
        _preciseStop = false;
        _stopReach = 0.45f;
        _lookReverseOverride = -1f;
        _revSteerCap = 1f;
        _ffSpanFix = false;
        _zjzwDynamic = false;
        _zjzwLockArc = true;
        _cftcStopByPose = false;
        _bpqbStopByPose = false;
        if (seg == SelectPass.Sites.Dcrk)
        {
            // 全图第一科要按 Det_DCRK 正常判定链停稳四次：point_right(2m 窗) → 库内 Ab<=3.2
            // → point_left(2m 窗) → 库内 Ab<=3.2，ParkingCount 到 2 才销毁出口空气墙。
            // 普通到达距离 2.2m 会让车停在判定窗外，故收窄触发距离；库内停点路点取 z=-41.0，
            // 即使纯跟踪一路倒到路点，车尾 z=-43.25 仍在库底 -43.305 之内、Ab 也仍在 3.2 内。
            _preciseStop = true;
            _stopReach = 0.7f;
            // 与单科 BuildDcrkWps 一致：密集圆弧下 2.6m 前瞻会切内弯，车只往西走不南下。
            _lookReverseOverride = 3.5f;
        }
        else if (seg == SelectPass.Sites.Cftc)
        {
            _preciseStop = true;
            _stopReach = 0.45f;   // 与单科一致：兜底距离，真正收车靠位姿阈值
            // 与单科一致：侧方倒车用默认 2.6m 前瞻（3.5 会让弧提前起弯、停不到位）
            _lookReverseOverride = -1f;
            _revSteerCap = 1.0f;
            _cftcStopByPose = true;
        }
        else if (seg == SelectPass.Sites.Bpqb)
        {
            _preciseStop = true;
            _stopReach = 0.45f;
            _bpqbStopByPose = true;   // 定点停车按位姿 z 收车（判定窗只有 30cm）
        }
        else if (seg == SelectPass.Sites.Zjzw)
        {
            _zjzwDynamic = true;
            ConfigureZjzwRoute();
        }
    }

    /// <summary>正常行驶：路径推进 + 纯跟踪转向（速度自适应前瞻）+ 分段限速/前方转角限速 + 卡死看门狗</summary>
    private void UpdateRun()
    {
        Vector3 pos = transform.position;
        if (_sites == SelectPass.Sites.Default) SyncSegParams(_wpIndex);

        // -1a) 侧方入库收车：按「行驶方向」判定，不看路点距离。
        //   入位方向是 -x：车心 x 退到 _cftcStopX 即车尾已进库；z 只用来校验车身在库深范围内。
        //   不能只看 z：纯跟踪实跑线比路点偏南约 0.5m，z 早在库口外 1.2m 就到位了（历史事故）。
        //   不依赖 _stopAtIndex 是否已登记 —— 登记发生在离库 1.3m 处，那时再减速已经晚了。
        if (_cftcStopByPose && _preciseStop && _gearReverse && _rev[_wpIndex])
        {
            float stopX = CftcStopX();                        // 路线自己的倒车终点
            bool deepIn = pos.x <= stopX && pos.z <= _cftcStopZ;
            bool overrun = pos.x <= stopX - 0.35f;            // 兜底：z 万一不达标也不许再往后怼
            if (deepIn || overrun)
            {
                // 库内停稳 = 倒车段走完：进度直接落到「出库第一个前进路点」，
                // 出库直接挂 D 走人（停在 lastRev 会因 _rev[17]=R 再刹一次、再挂 R 往后溜
                // —— 就是「出库先回一下」）。
                int lastRev = _wpIndex;
                while (lastRev + 1 < _rev.Length && _rev[lastRev + 1]) lastRev++;
                int nextWp = Mathf.Min(lastRev + 1, _wps.Length - 1);
                _wpIndex = nextWp;
                _stopAtIndex = -1;
                Debug.Log(string.Format("[DEMO] 侧方入库到位 pos=({0:F2},{1:F2}) yaw={2:F0} Ab={3:F2} 车尾x={4:F2} lastRevWp={5}",
                    pos.x, pos.z, NormYaw(transform.eulerAngles.y), pos.z + 32.806f, pos.x - 2.25f, lastRev));
                bool nextRev = (lastRev + 1 < _rev.Length) ? _rev[lastRev + 1] : false;
                BeginStop(nextRev != _gearReverse, _hold[Mathf.Clamp(lastRev, 0, _hold.Length - 1)], false);
                return;
            }
        }

        // -1b) 精准停点（仅 _preciseStop 路线）：向 hold 路点继续行驶，贴近 StopReach 才刹停。
        //     距离不减反增说明已被顶过停点 → 原地强制刹停兜底。
        if (_preciseStop && _stopAtIndex >= 0)
        {
            float dStop = FlatDist(pos, _wps[_stopAtIndex]);
            // 半坡定点：按位姿 z 收车。Det_BPQB 的定点窗只认车心 z ∈ [40.88,41.18]（30cm），
            // 靠「距路点 0.45m」触发再以 9km/h 硬刹，滑行量有 ±0.4m 抖动 —— 实测同一套代码
            // 一轮落在 40.95（合格）、一轮落在 40.55（出窗判中途停车扣 100）。改成位姿触发。
            bool bpqbPoseHit = _bpqbStopByPose && !_gearReverse && pos.z >= _bpqbStopZ;
            // 位姿收车时禁用「距路点 StopReach」这条：路点 z=41.0，0.45m 兜底会在 z=40.55 就刹，
            // 那时还没进判定窗 [40.88,41.18] → 判中途停车扣 100（v26 实测就是这个）。
            bool reachHit = !_bpqbStopByPose && dStop < _stopReach;
            if (bpqbPoseHit || reachHit || dStop > _stopAtLastD + 0.1f)
            {
                int idx = _stopAtIndex;
                _stopAtIndex = -1;
                if (bpqbPoseHit)
                    Debug.Log(string.Format("[DEMO] 半坡定点到位 pos=({0:F2},{1:F2}) yaw={2:F0} distWp={3:F2}",
                        pos.x, pos.z, NormYaw(transform.eulerAngles.y), dStop));
                // 停稳后要不要换档，看「接下来要走的那一段」，不看当前段：
                // 侧方库内停稳时当前段还是 R，但下一段（出库）是 D —— 就该在停稳这 3 秒里挂好 D，
                // 出库直接走，不再多刹一次、也不会先往后溜。
                bool nextRev = (idx + 1 < _rev.Length) ? _rev[idx + 1] : _gearReverse;
                BeginStop(nextRev != _gearReverse, _hold[idx], false);
                return;
            }
            _stopAtLastD = dStop;
        }

        // 0) 段方向检查：下一段需要 R/D 而当前档位不符 → 刹停换档
        if (_stopAtIndex < 0 && _wpIndex < _wps.Length - 1 && _rev[_wpIndex] != _gearReverse)
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
        if (_stopAtIndex < 0 && _wpIndex < _wps.Length - 1 && (_wpIndex > 0 || _leftStart))
        {
            float dCur = FlatDist(pos, _wps[_wpIndex]);
            float dNext = FlatDist(pos, _wps[_wpIndex + 1]);
            bool mustStopHere = _hold[_wpIndex] > 0f || _rev[_wpIndex] != _gearReverse;
            float arrive = _rev[_wpIndex] ? _arriveDistRev : _arriveDist;
            if (dCur < arrive || (!mustStopHere && dNext < dCur))
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
                if (_preciseStop && _hold[_wpIndex] > 0f)
                {
                    // 精准停点（仅 _preciseStop 路线）：不立即刹停，登记停点，
                    // 行驶到贴近该路点（StopReach）再刹停（见 UpdateRun 开头）
                    _stopAtIndex = _wpIndex;
                    _stopAtLastD = 999f;
                    return;
                }
                if (needShift || _hold[_wpIndex] > 0f)
                {
                    // 旧行为：纯换档点 / 未启用精准停点的路线，在推进位置直接刹停
                    BeginStop(needShift, _hold[_wpIndex], false);
                    return;
                }
            }
        }

        // 3) 转向：直角转弯转角段用「打死→保持→回正」；其余路段纯跟踪
        //    （倒车用镜像转向律：目标在车后方，取 atan2(lx, |lz|)）
        bool steerByLock = false;
        _routeSpeed = -1f;
        if (_activeSite == SelectPass.Sites.Zjzw && _zjzwDynamic && !_gearReverse)
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
        else if (_activeSite == SelectPass.Sites.Zjzw && _zjzwLockArc && !_gearReverse)
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
        // 侧方停车入库摆正：把车身拉到与库边线平行（yaw=90）并锁住。
        // 旧写法「满舵保持到 yaw≥88 再放手」会过冲：放手那一帧 yaw 已 88~90，
        // 纯跟踪仍带着 -1.00 的舵，车继续往南摆到 95°，车尾怼库底墙（v21 实测扣 100）。
        // 改成航向闭环：偏差大时自然饱和到满舵，接近 90 时自动回舵，过冲会自动反打。
        else if (_activeSite == SelectPass.Sites.Cftc && _gearReverse &&
                 RelWp >= 14 && RelWp <= 17)
        {
            // Mathf.DeltaAngle(cur,target) = target-cur；而倒车时「舵越负 yaw 越大」，
            // 故 cmd = -k·(90-yaw)：yaw 偏南→负舵摆正，过冲到 95°→正舵回摆。
            float yawErr = Mathf.DeltaAngle(transform.eulerAngles.y, 90f); // = 90 - yaw
            float cmd = Mathf.Clamp(-yawErr * 0.12f, -1f, 1f);
            _steer = Mathf.MoveTowards(_steer, cmd, 4f * Time.deltaTime);
            _kernel.Hardware_SetSteering(_steer);
            steerByLock = true;
        }
        // 侧方停车出库：方向盘左打满并保持，直到车中心北抬出库口。
        // v14 实跑：出库第一帧 steer=-1.00 但只持续一帧就回正到 +0.08
        //（wpIndex 从 17 跳到 22/24、前瞻点方向突变），车头没来得及出缺口，
        // 在 x≈-9.83 蹭到库位东壁：contact=(-9.83,-31.09) carPos=(-12.19,-30.81)。
        // 按考试指南「出库左打满」，用位姿条件保持满舵，车头北抬后交回路线跟随。
        else if (_activeSite == SelectPass.Sites.Cftc && !_gearReverse &&
                 RelWp >= 18 && RelWp <= 26 && pos.z < -29.9f)
        {
            _steer = Mathf.MoveTowards(_steer, -1f, 4f * Time.deltaTime);
            _kernel.Hardware_SetSteering(_steer);
            steerByLock = true;
        }
        // 倒车入库（v27）「挂 R 直接打满」：点位一到立刻满舵，车身摆正（yaw 到位）立刻回正。
        // 为什么不继续用纯跟踪追弧点：实测打舵爬升有滞后，满舵吃满要 0.9m，
        // 整条弧跟着后移，车尾西角扫到库口西翼（(-72.99,-37.93) vs 墙 x=-72.91）。
        // 位姿条件是确定性的，与前瞻距离/路点疏密无关 —— 这才是「找准点位」该有的样子。
        else if (_activeSite == SelectPass.Sites.Dcrk && _gearReverse &&
                 RelWp >= 7 && RelWp <= 16 &&
                 pos.x <= _dcrkLockX && NormYaw(transform.eulerAngles.y) > 2f)
        {
            // 第一次（右倒库）：车头朝东 90° → 目标朝北 0°，满舵正舵
            _steer = Mathf.MoveTowards(_steer, 1f, 10f * Time.deltaTime);
            _kernel.Hardware_SetSteering(_steer);
            steerByLock = true;
        }
        else if (_activeSite == SelectPass.Sites.Dcrk && _gearReverse &&
                 RelWp >= 26 && RelWp <= 35 &&
                 pos.x >= _dcrkLockX2 && NormYaw(transform.eulerAngles.y) < -5f)
        {
            // 第二次（镜像倒库）：车头朝西 -90° → 目标朝北 0°，满舵反舵
            _steer = Mathf.MoveTowards(_steer, -1f, 10f * Time.deltaTime);
            _kernel.Hardware_SetSteering(_steer);
            steerByLock = true;
        }
        // 倒库入位最后一段（车身已进库、离停点约 2m）：航向闭环把车摆正到 0°。
        // 纯跟踪在库里这段 2m 直线纠不干净 —— 实测第一次停稳 yaw≈-1°（可接受），
        // 第二次 yaw≈+3~4°，停车后肉眼就是「车有点歪」（2026-11-05）。
        // 纠偏量限幅 ±0.4、增益 0.15：库里左右各只剩 0.65m，不能为大角度摆正把车尾蹭到库壁。
        // 放在两条「挂 R 直接打满」分支之后，|yaw|>2 时仍由满舵接管。
        else if (_activeSite == SelectPass.Sites.Dcrk && _gearReverse &&
                 ((RelWp >= 13 && RelWp <= 16) || (RelWp >= 32 && RelWp <= 36)) &&
                 pos.z < -39.0f && Mathf.Abs(NormYaw(transform.eulerAngles.y)) <= 10f)
        {
            float yawErr = Mathf.DeltaAngle(transform.eulerAngles.y, 0f); // = 0 - yaw
            float cmd = Mathf.Clamp(-yawErr * 0.15f, -0.4f, 0.4f);
            _steer = Mathf.MoveTowards(_steer, cmd, 4f * Time.deltaTime);
            _kernel.Hardware_SetSteering(_steer);
            steerByLock = true;
        }
        // 倒车入库（v27）出库「先正对库口直出」：库口缺口只有 2.65m（X[-72.91,-70.26]），
        // 车身宽 1.664m，两侧合计余量不到 1m。第二次出库实测车在库口截面已斜 11°，
        // 右后角蹭到东翼角点 (-70.26,-38.08) 扣 100 分。
        // 车尾完全出库口需要车心 z > -35.83（库口线 -38.08 + 半车长 2.25）。
        // v35：按驾考实操定点位——「驾驶座出库口再打方向」，即车心越过库口线 z=-38.08
        // 才解除直出保护。v34 把阈值收到 -35.2（车开出库口 3m 才准转）导致车头冲过头，
        // 满舵后弧顶怼上北墙 contact(-72.42,-31.06) 扣 100 分。
        // 打舵有 ~0.1s 爬升（6.5km/h 下约 0.3m 行程），故阈值再提前 0.3m 取 -38.38，
        // 使驾驶座压线瞬间方向盘正好开始转，与真车手感一致。
        // v36：两段分开取点。第一次出库往西左转，车尾甩向西北；第二次往东右转，车尾甩向东南。
        // 实测第二次车尾东角离库口东翼只剩 0.34m（第一次对西翼有 0.71m），故第二次再推后 0.5m，
        // 让车尾多出库口一点再打舵。第一次保持「驾驶座压库口线」不变。
        else if (_activeSite == SelectPass.Sites.Dcrk && !_gearReverse &&
                 ((RelWp >= 16 && RelWp <= 22) || (RelWp >= 35 && RelWp <= 41)) &&
                 pos.z < (RelWp >= 35 ? -37.00f : -38.38f))
        {
            _steer = Mathf.MoveTowards(_steer, 0f, 6f * Time.deltaTime);
            _kernel.Hardware_SetSteering(_steer);
            steerByLock = true;
        }
        if (!steerByLock)
        {
        _driveTime += Time.deltaTime;
        // 精准停点待停标志：停点附近前进/倒车段常共线重叠，前瞻/投影全部不可信
        bool pendingStop = _preciseStop && _stopAtIndex >= 0;
        // 横向误差（Stanley 项）：车偏出路径时按偏移量直接反打方向。
        // 0.15m 内线性淡入——贴线时完全交给纯跟踪，避免两套修正互搏形成振荡回路（方向盘大幅甩动）；
        // 偏出 0.15m 后纠偏全权介入，偏差一出现（不用等碰到边线）就开始快速回正
        Vector3 projPt; int projSeg;
        float crossErr = ProjectOnPath(pos, out projPt, out projSeg);
        float errFactor = Mathf.Clamp01(Mathf.Abs(crossErr) / 0.5f); // 偏 0.5m 时响应拉满
        // 曲率前馈先算：既给转向用，也充当速度自适应前瞻的直/弯判据。
        // v20：倒车也计算（原先 !_gearReverse 直接置 0）——倒车弧段恰恰最需要前馈。
        float ff = (_wps != null && _wps.Length > 1)
            ? SteerFeedforward(projPt, projSeg, _ffSpanFix, _ffSpanFix ? 1.5f : 0f)
            : 0f;
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
        // 精准停点待停期间：直接瞄准停点本身，不走全路径前瞻——
        // 2026-11-04 实测：前进段与倒车段在停点共线重叠，前瞻点落到倒车段上，
        // 车沿倒车路径"前进"拐进东翼横墙（contact -5.94,-30.21）
        Vector3 target = pendingStop
            ? _wps[_stopAtIndex]
            : LookaheadPoint(pos, _gearReverse
                ? (_lookReverseOverride > 0f ? _lookReverseOverride : _lookReverse)
                : lookFwd);
        Vector3 local = transform.InverseTransformPoint(target);
        float ang;
        if (_gearReverse) ang = Mathf.Atan2(local.x, Mathf.Max(-local.z, 0.15f));
        else ang = Mathf.Atan2(local.x, Mathf.Max(local.z, 0.15f));
        float steerTarget = Mathf.Clamp(ang * _steerGain, -1f, 1f);
        // 倒车曲率前馈（v20，仅 Dcrk 启用 _ffSpanFix）：与前进同式直接叠加，符号取反
        // （倒车跟踪同一弧线所需舵量与前进相反，v18/v19 实跑标定）。
        // v19 教训：弯中「以前馈为主」的混合会互相压制——纯跟踪中段本给 0.9，
        // 被 0.49 的前馈混到 0.55，弧更平、撞得更早。叠加后入口即满舵（纯跟踪欠的
        // 正是入口那一脚），中段钳 1.0 ≈ 实测满舵半径 4.0m ≈ 计划弧 4.1m。
        // 倒车航向误差（v26 先算，前馈阻尼和 Stanley 共用）：车尾指向 vs 路径行进切线。
        Vector3 tanV = Vector3.zero;
        float errRad = 0f;
        if (_gearReverse && _ffSpanFix && projSeg >= 0 && projSeg < _wps.Length - 1)
        {
            tanV = _wps[projSeg + 1] - _wps[projSeg]; tanV.y = 0f;
            if (tanV.sqrMagnitude > 1e-6f)
                errRad = Vector3.SignedAngle(-transform.forward, tanV.normalized, Vector3.up) * Mathf.Deg2Rad;
        }
        if (_gearReverse && _ffSpanFix && !pendingStop)
        {
            float ffRev = -ff; // 倒车符号反转（v18/v19 实跑标定）
            // 过旋转阻尼（v26）：车尾已转过计划切线时，弧还没走完的前馈会继续催转，
            // 把 Stanley 的反向修正顶到钳制值——车尾甩过头扫库口翼墙（v22-v25 根源）。
            // err 与 ffRev 同号 = 已转过 → 按角度线性掐前馈（11.5° 全掐）；滞后态不碰。
            if (Mathf.Abs(errRad) > 1e-4f && Mathf.Sign(errRad) == Mathf.Sign(ffRev) && ffRev != 0f)
            {
                ffRev *= 1f - Mathf.Clamp01(Mathf.Abs(errRad) / 0.20f);
            }
            steerTarget = Mathf.Clamp(steerTarget + ffRev, -1f, 1f);
        }
        // 倒车航向修正（v25）：纯跟踪只纠位置不纠航向——车压在线上但朝向歪着时舵量归零，
        // 残留偏航原样带进库内（v24 实测：出弧 -15° 一路倒进库，尾东角在库内刮东翼）。
        // 符号（实测标定）：正舵=车尾向左；err>0(切线在尾右侧) 需要负舵向右回。
        if (_gearReverse && _ffSpanFix && Mathf.Abs(errRad) > 1e-4f)
        {
            steerTarget = Mathf.Clamp(steerTarget - 2.5f * errRad, -1f, 1f);
        }
        // 倒车舵量上限（仅精准停点路线）：限制"驶向新路点瞬间"的方向尖峰，
        // 防止车头在倒车中持续偏转导致车尾下扫（2026-11-04 实测 10m 内 90°→45°）。
        // v23：钳制放在前馈叠加之后——前馈必须受钳制约束。Dcrk 用 0.88：
        // 满舵 1.0 实际半径 ≈3.5m 比计划弧 4.1m 紧，90° 旋转提前 1.7m 完成、
        // 前馈来不及收舵，车尾甩过头 -7° 扫到库口东翼（v22 实测 contact -70.26,-38.56）；
        // 0.88 → 实际半径 ≈3.95m ≈ 计划 4.1m，旋转按计划完成、前馈按时归零。
        if (_gearReverse && _revSteerCap < 1f) steerTarget = Mathf.Clamp(steerTarget, -_revSteerCap, _revSteerCap);
        if (!_gearReverse && _wps.Length > 1 && !pendingStop)
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
        // 临近精准停点：按剩余距离做减速规划（v = √(2·a·d)），到点自然停住。
        // 旧做法是 6.5km/h 冲到跟前再一脚急刹，车会继续滑 0.4~0.5m —— 这就是
        // 「入库没到位」「出库前先回一下」的根源（看着像倒车，其实是刹车距离）。
        bool stopApproach = false;
        if (_cftcStopByPose && _preciseStop && _gearReverse && _rev[_wpIndex])
        {
            // 侧方入库的停点是「车心 x 退到 _cftcStopX」，不是路点（路点还在 0.5m 外、
            // 且实跑线偏南对不上）—— 减速规划必须按到这条线的距离算，
            // 否则会以 5~6km/h 冲到线前再一脚刹，多滑 0.5m（= 出库前"先回一下"）。
            float dAppr = Mathf.Max(pos.x - CftcStopX(), 0.02f);
            // a=0.7m/s² 的平缓减速，末速不低于 2km/h 蠕行入位 —— 停下时几乎不滑。
            float vAllow = Mathf.Sqrt(2f * 0.7f * Mathf.Max(dAppr - 0.05f, 0.01f)) * 3.6f;
            vTarget = Mathf.Min(vTarget, Mathf.Max(vAllow, 2.0f));
            stopApproach = true;
        }
        if (_bpqbStopByPose && _preciseStop && _stopAtIndex >= 0 && !_gearReverse)
        {
            // 半坡定点：按到「定点位姿线」的距离做减速规划，最后 2m 降到 3~4km/h 蠕行。
            // 旧做法 9km/h 冲到 0.45m 再一脚刹，滑行 0.45~0.9m，停点抖出 30cm 判定窗。
            float dAppr = Mathf.Max(_bpqbStopZ - pos.z, 0.02f);
            // 末速下限 3.5km/h：坡上低于 3 油门顶不住（死区 0.2 会让轮速归零判中途停车），
            // 3.5km/h 全刹滑行约 0.15m，正好补到位姿线之后。
            float vAllow = Mathf.Sqrt(2f * 0.9f * Mathf.Max(dAppr - 0.05f, 0.01f)) * 3.6f;
            vTarget = Mathf.Min(vTarget, Mathf.Max(vAllow, 3.5f));
            stopApproach = true;
        }
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
        // 接近停点：闸门不能用 +2km/h 死区（那样到跟前才刹，必然滑过 0.4m）。
        // 只要超过规划速度就按比例上刹车，让车按 v=√(2ad) 的曲线平滑停住。
        if (stopApproach && v > vTarget)
        {
            throttle = 0f;
            brake = Mathf.Max(brake, Mathf.Clamp((v - vTarget) * 0.45f, 0.25f, 1f));
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
                if (_shiftTargetRev) _gears.R();
                else _gears.D();
            }
            _gearReverse = _shiftTargetRev;
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
    private float SteerFeedforward(Vector3 projPt, int projSeg, bool spanFix, float backOffset = 0f)
    {
        int n = _wps.Length;
        if (n < 4) return 0f;
        // backOffset（v21）：测量窗口起点沿路径后移。窗口 2m 前瞻会让前馈提前 2m 起坡，
        // 倒库弧提前 ~1.3m 起弯、落线整体东移 0.9m（v20 实测压东翼 -70.26）。
        // 后移 0.8m → 起弯推迟、弧中段量测不变、出口收舵稍晚。
        if (backOffset > 0.001f)
        {
            float back = backOffset;
            while (back > 0.0001f && projSeg >= 0)
            {
                Vector3 a = _wps[projSeg];
                float d = FlatDist(projPt, a);
                if (d >= back) { projPt += (a - projPt) * (back / Mathf.Max(d, 1e-5f)); back = 0f; }
                else { back -= d; projPt = a; projSeg--; }
            }
            if (projSeg < 0) return 0f;
        }
        // 航向角增量法：取投影点前方 ~2m 窗口，窗口两端方向向量的夹角 = 该段累计转角。
        // 弯道交接处（左弯→右弯）窗口内左右转角互相抵消，前馈平滑过零——
        // 旧版三点外接圆（1.8/3.0/4.2m）在交接处会横跨符号翻转，算出虚假大曲率，
        // 导致「上一个弯还没转完就猛打下一个弯的方向」。
        if (spanFix)
        {
            // v22（仅 Dcrk）：窗口两端各取 0.6m 固定基线的弦方向 = 该 0.6m 子弧中点的切线。
            // 原弦采样在窗口端点会取到不足整段的「部分弦」——弦越短，路点 0.01 取整噪声
            // 把方向甩得越歪（v21 实测弧上 ff 虚高 1.09，真值 0.85，曲率虚高 38%）。
            // 基线中点间距 = window - sample，圆弧上 kappa 严格 = 1/R；基线还能把噪声平均掉。
            const float windowF = 2.0f, sample = 0.6f;
            Vector3 pA = PathPointAlong(projPt, projSeg, sample, out int segA);
            Vector3 pB = PathPointAlong(projPt, projSeg, windowF - sample, out int segB);
            Vector3 pC = PathPointAlong(projPt, projSeg, windowF, out int segC);
            if (segC < 0) return 0f; // 窗口越过路径末端，不做前馈
            Vector3 d0v = pA - projPt; d0v.y = 0f;
            Vector3 d1v = pC - pB; d1v.y = 0f;
            if (d0v.magnitude < 1e-3f || d1v.magnitude < 1e-3f) return 0f;
            d0v.Normalize(); d1v.Normalize();
            float crossF = d0v.x * d1v.z - d0v.z * d1v.x;
            float dHeadingF = Mathf.Asin(Mathf.Clamp(crossF, -1f, 1f));
            float kappaF = Mathf.Abs(dHeadingF) / (windowF - sample);
            if (kappaF < 0.005f) return 0f;
            float degF = Mathf.Atan(_kernel.wheelBase * kappaF) * Mathf.Rad2Deg;
            return -Mathf.Sign(dHeadingF) * degF / Mathf.Max(_kernel.steerAngle, 1f);
        }
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

    /// <summary>从 (p, seg) 沿路径前进 s 米的点；越过路径末端时 outSeg=-1 并钳到终点。</summary>
    private Vector3 PathPointAlong(Vector3 p, int seg, float s, out int outSeg)
    {
        Vector3 cur = p;
        float left = s;
        int i = seg;
        while (left > 0.0001f)
        {
            if (i < 0 || i >= _wps.Length - 1) { outSeg = -1; return _wps[_wps.Length - 1]; }
            Vector3 nxt = _wps[i + 1];
            float d = FlatDist(cur, nxt);
            if (d >= left) { cur = cur + (nxt - cur) * (left / Mathf.Max(d, 1e-5f)); left = 0f; }
            else { left -= d; cur = nxt; i++; }
        }
        outSeg = i;
        return cur;
    }

    /// <summary>把车投影到当前路径段附近，返回最近投影点和段索引（out），并返回带符号横向偏移（正 = 车在路径前进方向右侧）</summary>
    /// <summary>当前档位连续段的终点 wps 索引：前瞻/投影都不得越过它。
    /// 同一条线可能走两遍（Cftc 出库 = 入库倒车段逆序回放），路径自交叠；
    /// 若不按档位阶段截断，倒车入库时前瞻会越过停点看到折返回东的出库段，
    /// 目标点跳到车后方 → 舵量反转、车身不再摆正（v12 实测）。</summary>
    private int DriveSegEnd()
    {
        int last = _wps.Length - 1;
        if (_rev == null || _rev.Length != _wps.Length) return last;
        bool rev = _gearReverse;
        int end = Mathf.Clamp(_wpIndex, 0, last);
        while (end < last && _rev[end] != rev) end++;   // 跳过还没推进到的换档点
        while (end < last && _rev[end + 1] == rev) end++;
        return end;
    }

    private float ProjectOnPath(Vector3 pos, out Vector3 bestPt, out int bestSeg)
    {
        // 路线只作参考：在当前档位阶段的**整段**路径上找最近投影。
        // 旧版把搜索窗锁在 _wpIndex±2（4 段折线），车一旦被顶偏/起步位姿有偏差，
        // 投影点就钉死在那几段上，前瞻目标跟着跳到远处 —— 侧方出库起步"猛蹿"就是这么来的。
        // 现在允许在全阶段范围内投影，车能自己顺着路线走回来，路点不再喧宾夺主。
        int lastSegAll = _wps.Length - 2;
        if (lastSegAll < 0) { bestPt = pos; bestSeg = 0; return 0f; }

        int phaseEnd, phaseStart;
        if (_rev != null && _rev.Length == _wps.Length)
        {
            bool rev = _gearReverse;
            int p = Mathf.Clamp(_wpIndex, 0, _wps.Length - 1);
            while (p < _wps.Length - 1 && _rev[p] != rev) p++;   // 跳过还没推进到的换档点
            phaseEnd = p;
            phaseStart = p;
            while (phaseEnd < _wps.Length - 1 && _rev[phaseEnd + 1] == rev) phaseEnd++;
            while (phaseStart > 0 && _rev[phaseStart - 1] == rev) phaseStart--;
        }
        else { phaseStart = 0; phaseEnd = _wps.Length - 1; }

        int firstSeg = Mathf.Clamp(phaseStart, 0, lastSegAll);
        int lastSeg = Mathf.Clamp(phaseEnd - 1, firstSeg, lastSegAll);
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

        // 2) 从投影点沿路径（弧长）向前找 look 米处的目标点；
        //    搜索截断在当前档位阶段终点，防止跨到折返的出库段（见 DriveSegEnd）
        int endWp = DriveSegEnd();
        float remain = look;
        Vector3 from = bestPt;
        for (int i = bestSeg; i < endWp; i++)
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
        return new Vector3(_wps[endWp].x, pos.y, _wps[endWp].z);
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
            case SelectPass.Sites.Default: BuildDefaultPath(); return true;
        }
        ShowCaption("该项目暂不支持演示", 5f);
        return false;
    }

    /// <summary>
    /// 全科连续路线（Default）：把五个单科已验证路点按真实考试顺序首尾相接，
    /// 科间用"连接段"路点把车从上一科终点实地开到下一科起点（不瞬移）。
    /// 转向/精准停点等科目专属参数由 SyncSegParams 按当前路点所属科目动态切换。
    /// ⚠ 连接段默认是两端点的直线桥接，可能穿过考场中的墙/库位——请在 Unity 编辑器中
    ///   对照实际道路校准 _defaultConnectors（移动中间点绕开障碍）。
    /// </summary>
    private void BuildDefaultPath()
    {
        // 真实考试顺序（与场景地理一致：自西向东再一路向北的连续路线，
        // 直角弯出口向北 16m 即半坡入口，坡顶向北直达曲线行驶）：
        // 倒车入库 → 侧方停车 → 直角转弯 → 半坡起步 → 曲线行驶
        var order = _defaultOrder;

        // 1) 依次构建各科路径并快照（Build*Path 会改写 _wps/_speeds/... 等共享字段）
        var snaps = new System.Collections.Generic.List<PathSnap>();
        foreach (var s in order)
        {
            if (s == SelectPass.Sites.Dcrk)
            {
                // 全图考试不挪车：连续路线起点就是全图自己的入口 (-118.1,-33.5) 一带。
                // 与单科共用同一份两次倒库几何，只换首点，避免两套几何漂移。
                Vector3 here = transform.position;
                BuildDcrkWps(new Vector3(here.x, 0f, here.z));
            }
            else if (s == SelectPass.Sites.Cftc) BuildCftcPath();
            else if (s == SelectPass.Sites.Bpqb) BuildBpqbPath();
            else if (s == SelectPass.Sites.Zjzw) BuildZjzwPath();
            else if (s == SelectPass.Sites.Qxxs) BuildQxxsPath();
            snaps.Add(new PathSnap
            {
                site = s, wps = _wps, speeds = _speeds, rev = _rev,
                hold = _hold, hand = _handbrake, steps = _steps
            });
        }

        var allWps = new System.Collections.Generic.List<Vector3>();
        var allSpeeds = new System.Collections.Generic.List<float>();
        var allRev = new System.Collections.Generic.List<bool>();
        var allHold = new System.Collections.Generic.List<float>();
        var allHand = new System.Collections.Generic.List<bool>();
        var allSeg = new System.Collections.Generic.List<SelectPass.Sites>();
        var allConn = new System.Collections.Generic.List<bool>();
        var allSteps = new System.Collections.Generic.List<DemoStep>();
        var allSegStart = new System.Collections.Generic.List<int>();

        for (int k = 0; k < snaps.Count; k++)
        {
            var sn = snaps[k];
            int baseWp = allWps.Count;
            allSegStart.Add(baseWp);   // 本科段首点的全局索引（RelWp 换算用）
            allWps.AddRange(sn.wps);
            allSpeeds.AddRange(sn.speeds);
            allRev.AddRange(sn.rev);
            allHold.AddRange(sn.hold);
            allHand.AddRange(sn.hand);
            for (int i = 0; i < sn.wps.Length; i++) { allSeg.Add(sn.site); allConn.Add(false); }
            foreach (var st in sn.steps)
                allSteps.Add(new DemoStep { atWp = baseWp + st.atWp, text = st.text,
                    turnSignalOn = st.turnSignalOn, turnSignalOff = st.turnSignalOff });

            // 科间连接段：上一科终点 → 下一科起点，车实地开过去（非瞬移）
            if (k < snaps.Count - 1)
            {
                Vector3 a = sn.wps[sn.wps.Length - 1];
                Vector3 b = snaps[k + 1].wps[0];
                Vector3[] mid = (_defaultConnectors != null && _defaultConnectors[k] != null)
                    ? _defaultConnectors[k]
                    : StraightBridge(a, b);
                foreach (var p in mid)
                {
                    allWps.Add(p);
                    allSpeeds.Add(6.5f);
                    allRev.Add(false);
                    allHold.Add(0f);
                    allHand.Add(false);
                    allSeg.Add(sn.site);   // 连接段归属上一科（仅用于日志），实际转向由 _segConn 强制为纯跟踪
                    allConn.Add(true);
                }
            }
        }

        _wps = allWps.ToArray();
        _speeds = allSpeeds.ToArray();
        _rev = allRev.ToArray();
        _hold = allHold.ToArray();
        _handbrake = allHand.ToArray();
        _segSite = allSeg.ToArray();
        _segConn = allConn.ToArray();
        _segStart = allSegStart.ToArray();
        _steps = allSteps.ToArray();
    }

    private struct PathSnap
    {
        public SelectPass.Sites site;
        public Vector3[] wps;
        public float[] speeds;
        public bool[] rev;
        public float[] hold;
        public bool[] hand;
        public DemoStep[] steps;
    }

    /// <summary>两端点之间的直线桥接中间点（不含两端），每 ~15m 一个，便于在编辑器中逐个挪动绕障。</summary>
    private static Vector3[] StraightBridge(Vector3 a, Vector3 b)
    {
        float d = Vector3.Distance(a, b);
        int n = Mathf.Max(1, Mathf.RoundToInt(d / 15f));
        var arr = new Vector3[n];
        for (int i = 1; i <= n; i++)
            arr[i - 1] = Vector3.Lerp(a, b, (float)i / (n + 1));
        return arr;
    }

    /// <summary>配置直角转弯几何闭环路线参数（与 BuildZjzwPath 中一致；全科模式需在进入 Zjzw 段时按需重配）。</summary>
    private void ConfigureZjzwRoute()
    {
        ZjzwTune t = appliedTune;
        float entryZ = t != null ? t.entryZ : -33.5f;
        float cornerX = t != null ? t.cornerX : 33.0f;
        float radius = t != null ? t.radius : 3.33f;
        float arcStep = t != null ? t.arcStep : 30f;
        float exitEndZ = t != null ? t.exitEndZ : -19.2f;
        float vApproach = t != null ? t.vApproach : 6.5f;
        float vCorner = t != null ? t.vCorner : 6.5f;
        float vExit = t != null ? t.vExit : 6.5f;
        _routeCfg.entryZ = entryZ;
        _routeCfg.exitX = 36.42f;
        _routeCfg.exitEndZ = exitEndZ;
        // 定版 401 的实测参数：入弯贴右 0.35m，给内侧直角 (34.41,-31.62) 留余量。
        // 缺这一句时入口段走车道中线 z=-33.5，左后轮内切 0.35m 直接挂到内角上
        // （2026-11-05 实测 contact(34.41,-31.62)，扣 100）。调参轮由 ApplyTune 注入，此处不覆盖。
        if (t == null)
        {
            _routeCfg.entryLaneOffset = 0.35f;
            _routeCfg.turnBias = 0f;
            _routeCfg.radiusMax = 3.2f;
        }
        _routeCfg.vEntry = vApproach;
        _routeCfg.vCorner = vCorner;
        _routeCfg.vExit = vExit;
        if (_kernel != null)
        {
            if (_kernel.wheelBase > 0.1f) _routeCfg.wheelBase = _kernel.wheelBase;
            if (_kernel.steerAngle > 1f) _routeCfg.maxSteerDeg = _kernel.steerAngle;
        }
    }

    /// <summary>
    /// 倒车入库（右库）：起点(-109.7,-25.8)朝东 → 沿通道前进驶过库口 → 停车挂R
    /// → 右打倒车入库 → 库内停稳挂D → 前进出库驶向 End(-54.6,-34.8)。
    /// 库口在 z=-34.4 一线（Point_left/-79.5 与 Point_right/-63.7 为左右库口中心），
    /// 底部到 z=-43.1（Point_garage）。本路径按右库（车库中心 point_carbarn≈-72.9,-43.1）演示；
    /// 倒车段已按场景真值西移校准，使库内停稳点对齐车库中心（容差 difference_carbarn=3.2m）。
    /// </summary>
    /// <summary>单科倒车入库：从标准起点 (-109.7,-25.8) 出发，按正常判定链完成两次倒库。</summary>
    private void BuildDcrkPath()
    {
        BuildDcrkWps(new Vector3(-109.7f, 0f, -25.8f));
    }

    /// <summary>
    /// 倒车入库路线（单科 / 全图共用，只有首点不同，避免两份几何漂移）。
    /// A(D) 进场 → ST1 point_right 停稳 → B(R) 右倒库 → ST2 库内停稳
    /// → C(D) 出库西行 → ST3 point_left 停稳 → D(R) 左倒库 → ST4 库内停稳
    /// → E(D) 出库东行出倒库区。两次进库都是挂 R 的真实倒库动作。
    /// Det_DCRK 判定链 Right→Carbarn→Left→Carbarn，ParkingCount==2 才 Destroy 出口空气墙
    /// (x=-55.20, z[-38.42,-30.70])；全程不豁免、不传送。
    /// 几何由 Tuning/plan_default_dcrk.py 生成，按玩家车真实碰撞体 1.664x4.496m 做连续扫掠校验。
    /// 场景真值：库口缺口 X[-72.91,-70.26]、库口线 z=-38.08、库底 z=-43.38。
    /// v18：按参考图两次都是「挂R直行一小段 → 打满舵 90° 圆弧 → 摆正直倒入位」；
    /// 通道行驶线 z=-33.20（北移 1.22m，进出场口同线、不再斜切回原位），
    /// 圆弧 R=4.10m ≈ 实跑标定的车辆满舵半径 4.00m，前瞻 3.5f 自然给满舵；
    /// 北墙按内表面 z=-31.06 校验，库内中心 x=-71.60。
    /// 车身包络用玩家车真实 BoxCollider 1.664x4.496m，连续扫掠最小余量 +0.064m。
    /// </summary>
    private void BuildDcrkWps(Vector3 start)
    {
        // v20：路点与 v18/v19 相同（两端对称 z=-33.20），修的是控制链——
        //  1) SteerFeedforward 跨度修正（_ffSpanFix）：离散路点上 2m 窗只跨 1 个折点，
        //     弧上曲率被低估一半（ff 恒 0.49，满舵需求 0.85）；
        //  2) 倒车叠加前馈（符号取反）：v18/v19 实测弧段入口纯跟踪只给 0.59 舵，
        //     车沿车道西溜 2m、整条弧变平，车尾扫到库口西翼 contact(-72.91,-38.08)；
        //  3) 整条路线画到地面（黄=前进、红=倒车），A/C 停点对称可直接目视核对。
        // 真实碰撞体 1.664x4.496m，连续扫掠最小余量 +0.064m；四个停点全部进判定窗。
        _wps = new Vector3[]
        {
            start,  // 0 A 起点（单科=标准起点；全图=车辆真实位置）
            new Vector3(-105f, 0f, -33.2f),  // 1 A
            new Vector3(-95f, 0f, -33.2f),  // 2 A
            new Vector3(-85f, 0f, -33.2f),  // 3 A
            new Vector3(-79.5f, 0f, -33.2f),  // 4 A
            new Vector3(-72f, 0f, -33.2f),  // 5 A
            new Vector3(-62.98f, 0f, -33.2f),  // 6 A ST point_right 停稳→挂R
            new Vector3(-65.5f, 0f, -33.2f),  // 7 B 挂R先直行一小段（参考图 A→B 虚线直线段）
            new Vector3(-67.5f, 0f, -33.2f),  // 8 B 车头过起始线→右打满
            new Vector3(-68.56f, 0f, -33.34f),  // 9 B
            new Vector3(-69.55f, 0f, -33.75f),  // 10 B
            new Vector3(-70.4f, 0f, -34.4f),  // 11 B
            new Vector3(-71.05f, 0f, -35.25f),  // 12 B
            new Vector3(-71.46f, 0f, -36.24f),  // 13 B
            new Vector3(-71.6f, 0f, -37.3f),  // 14 B
            new Vector3(-71.6f, 0f, -39.6f),  // 15 B
            new Vector3(-71.6f, 0f, -41f),  // 16 B ST 库内停稳→挂D
            new Vector3(-71.6f, 0f, -39.6f),  // 17 C
            new Vector3(-71.6f, 0f, -37.3f),  // 18 C
            new Vector3(-71.74f, 0f, -36.24f),  // 19 C
            new Vector3(-72.15f, 0f, -35.25f),  // 20 C
            new Vector3(-72.8f, 0f, -34.4f),  // 21 C
            new Vector3(-73.65f, 0f, -33.75f),  // 22 C
            new Vector3(-74.64f, 0f, -33.19f),  // 23 C
            new Vector3(-75.7f, 0f, -33.05f),  // 24 C
            new Vector3(-80.25f, 0f, -33.2f),  // 25 C ST point_left 回车区停稳→挂R
            new Vector3(-78f, 0f, -33.2f),  // 26 D 挂R先直行一小段（参考图 C→B 虚线直线段）
            new Vector3(-75.7f, 0f, -33.2f),  // 27 D
            new Vector3(-74.64f, 0f, -33.34f),  // 28 D
            new Vector3(-73.65f, 0f, -33.75f),  // 29 D
            new Vector3(-72.8f, 0f, -34.4f),  // 30 D
            new Vector3(-72.15f, 0f, -35.25f),  // 31 D
            new Vector3(-71.74f, 0f, -36.24f),  // 32 D
            new Vector3(-71.6f, 0f, -37.3f),  // 33 D
            new Vector3(-71.6f, 0f, -39.6f),  // 34 D
            new Vector3(-71.6f, 0f, -41f),  // 35 D ST 库内停稳 ParkingCount==2→空气墙销毁
            new Vector3(-71.6f, 0f, -39.6f),  // 36 E
            new Vector3(-71.6f, 0f, -37.3f),  // 37 E
            new Vector3(-71.46f, 0f, -36.24f),  // 38 E
            new Vector3(-71.05f, 0f, -35.25f),  // 39 E
            new Vector3(-70.4f, 0f, -34.4f),  // 40 E
            new Vector3(-69.55f, 0f, -33.75f),  // 41 E
            new Vector3(-68.56f, 0f, -33.19f),  // 42 E
            new Vector3(-67.5f, 0f, -33.2f),  // 43 E
            new Vector3(-52f, 0f, -34.6f),  // 44 E 出口对正 End(-54.8,-34.6)：到这点前 2.2m 收车，车正好压在 End 上
            new Vector3(-44f, 0f, -34.6f)  // 45 E 越过 End 后再收车（否则停车被 Det_DCRK 判中途停车扣 5）
        };
        // v33：整条倒库行驶线北移 0.35m（z=-33.20 → -32.85），让车身左侧贴近左边线。
        // 这是驾考的习惯做法：车身离左边线近，倒库起点到库口的横向距离更大，
        // 打满后的弧有更多容错，左右两边都不容易压线。
        // 只平移 0..43（出口段 44/45 要对正 End 的 z=-34.6，不参与）。
        for (int i = 0; i < 44 && i < _wps.Length; i++)
            _wps[i] = new Vector3(_wps[i].x, _wps[i].y, _wps[i].z - 0.35f);

        // v27：全程恒速 6.5（与直角弯/侧方/曲线统一）。倒库段原先降到 4，实测车速掉到 3
        // 且 throttle=0（贴在油门死区边界滑行）——低速下打舵响应滞后，满舵爬升吃掉 0.9m
        // 点位余量，整条弧后移，车尾西角扫到库口西翼（(-72.99,-37.93) vs 墙 x=-72.91）。
        _speeds = new float[]
        {
            6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f,
            6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f,
            6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f,
            6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f,
            6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f, 6.5f
        };
        _rev = new bool[]
        {
            false, false, false, false, false, false, true,
            true, true, true, true, true, true, true, true, true, false,
            false, false, false, false, false, false, false, false, true,
            true, true, true, true, true, true, true, true, true, false,
            false, false, false, false, false, false, false, false, false, false
        };
        _hold = new float[]
        {
            0f, 0f, 0f, 0f, 0f, 0f, 2f,
            0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 2f,
            0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 2f,
            0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 2f,
            0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f
        };
        _handbrake = new bool[_wps.Length];
        _steps = new DemoStep[]
        {
            new DemoStep { atWp = 0,  text = "① 沿考试车道驶入倒车入库区域" },
            new DemoStep { atWp = 6,  text = "② 右库口停稳，挂 R 档开始第一次倒库" },
            new DemoStep { atWp = 16, text = "③ 库内停稳，挂 D 档出库" },
            new DemoStep { atWp = 25, text = "④ 左库口停稳，挂 R 档开始第二次倒库" },
            new DemoStep { atWp = 35, text = "⑤ 库内停稳，两次倒库完成，出口空气墙解除" },
            new DemoStep { atWp = 36, text = "⑥ 出库东行，驶向出口" }
        };
        // 四个判定停点必须落进 point_right/left 的 2m 窗与库内 Ab<=3.2 窗；
        // 普通到达距离 2.2m 会停在窗外。倒车前瞻收短，避免纯跟踪切内弯导致车不南下、车尾扫库口横墙。
        _preciseStop = true;
        _stopReach = 0.7f;
        _lookReverseOverride = 3.5f;
        // v27「直接打满」：倒库不再靠纯跟踪慢慢磨弧，改由位姿条件满舵（见 UpdateRun），
        // 因此关掉曲率前馈跨度修正与倒车航向修正（那套是给纯跟踪补欠舵的，位姿满舵下用不上）。
        _ffSpanFix = false;
        _revSteerCap = 1f;     // 打满：不再钳 0.88
        // v19：整条行驶路线画到地面上（黄=前进段，红=倒车段），对应参考图的虚线轨迹；
        // A/C 两个停点同在 z=-33.20 这一条行驶线上，画出来可直接目视核对两边对称。
        // 【按要求去掉前进段黄色辅助线】地面的黄色线横跨整条考试车道，视觉干扰大，
        // 只保留红色倒车段轨迹。DrawRouteLine 保留（revSeg=true 仍在用）。
        // DrawRouteLine("DcrkRouteFwd", false, new Color(1f, 0.9f, 0.15f, 0.95f));
        DrawRouteLine("DcrkRouteRev", true, new Color(1f, 0.2f, 0.1f, 0.95f));
    }

    /// <summary>把当前 _wps 路线画成地面标线（LineRenderer，y=0.06）。
    /// revSeg=true 只画倒车段。长段按 0.4m 细分让圆弧平滑；换档分界处断线，
    /// 停点位置一目了然。运行时重建，退出 Play 自动随场景销毁。</summary>
    private void DrawRouteLine(string lineName, bool revSeg, Color col)
    {
        if (_wps == null || _rev == null || _wps.Length != _rev.Length) return;
        var old = GameObject.Find(lineName);
        if (old != null) Destroy(old);
        var go = new GameObject(lineName);
        var lr = go.AddComponent<LineRenderer>();
        lr.material = new Material(Shader.Find("Sprites/Default"));
        lr.startColor = col;
        lr.endColor = col;
        lr.startWidth = 0.07f;
        lr.endWidth = 0.07f;
        lr.numCornerVertices = 4;
        lr.useWorldSpace = true;
        var pts = new System.Collections.Generic.List<Vector3>();
        for (int i = 0; i < _wps.Length - 1; i++)
        {
            if (_rev[i] != revSeg) { pts.Clear(); continue; } // 换档分界断线
            Vector3 a = _wps[i], b = _wps[i + 1];
            a.y = 0.06f; b.y = 0.06f;
            int n = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(a, b) / 0.4f));
            for (int k = 0; k < n; k++) pts.Add(Vector3.Lerp(a, b, (float)k / n));
        }
        if (pts.Count < 2) { Destroy(go); return; }
        lr.positionCount = pts.Count;
        lr.SetPositions(pts.ToArray());
    }

    /// <summary>
    /// 侧方停车：起点(-39.9,-28.5)朝东 → 先沿车道北侧（Z≈-25，清空库墙西立面北沿-26.59）
    /// 东行进入库区 X 范围 → 向南切入 point_left(-4.15,-28.37) 触发倒车 →
    /// 挂R向右后倒入库（车尾向西南，对齐 Point_garage -17.64,-32.81）→ 停稳挂D →
    /// 打左灯出库（北返车道再东出）→ 驶向 End(4.56,-28.44)。
    /// 关键：前进段必须自"墙北侧面"入位（仿 Dcrk），严禁在 Z≈-29 处正撞库墙西立面(X=-24.22)。
    /// 库内停稳点按 point_carbarn(-17.64,-32.81) 校准（容差 difference_carbarn=1.65m）。
    /// </summary>
    /// <summary>
    /// 侧方停车（v4，按 Z 镜像标定后的真实几何）：车道位于北边线(z=-26.6)与两翼横墙(z=-30.25)
    /// 之间的走廊（z≈-28.5，出生点即在其上）。沿车道东行 → 停在 point_left(-4.15,-28.37) 处
    /// （<2m 触发倒车阶段）→ 挂 R 向右后倒（车头朝东→倒车向西），车尾甩入库位口袋
    /// x(-17.7,-9.8)（开口朝北，后墙 z=-32.9）→ 库内停稳 (-17.2,-31.0)：x 与 carbarn(-17.64,-32.81) 纵向对齐，
    /// Ab=cos·dist≈0.4≤1.65 判停车到位（Det_CFTC 仅校验纵向投影，故停点取 x≈-17.2）→ 挂 D 前进出库回车道 → 东行出 End(4.56,-28.44)。
    /// 全程恒速 6.5 km/h（与直角弯/曲线行驶统一，见 Tuning/驾驶参数规范.md）。
    /// 关键标定（Tuning/analyze_bay.py 修正 Z 镜像后）：北边线 z=-26.6 全宽 x[-24.2,-2.8]；
    /// 西翼横墙 z=-30.25 x[-24.2,-17.7]；东翼横墙 z=-30.25 x[-9.8,-2.8]；
    /// wall_hit 三处接触点均只与镜像布局吻合——旧 v2 走北车道(z=-25.3)必然压北边线。
    /// </summary>
    private void BuildCftcPath()
    {
        _preciseStop = true; // 停点必须精准：point_left 2m 窗 + 库内 Ab 1.65 窗
        // 【v14】0.25→1.05，按 v13 实跑逐帧标定：纯跟踪让实跑比路点偏南 0.5~0.8m，
        // 精准停点若按「距路点 0.25/0.60m」触发永远够不着，车会一路倒到库底。
        // v13 帧 (-14.3,-31.4) yaw=82 distWp=1.0 是安全停稳点：
        //   Ab=-31.4+32.806=1.406≤1.65、车尾 z≈-32.53 离库底 -32.87 余 0.34m；
        // 下一帧再到 (-15.0,-31.7) 就会压库底（contact -13.09,-32.85）。
        // 故触发距离取 1.05m，正好在这帧刹停。point_left 判定窗 2m，1.05m 提前量同样在窗内。
        // 【v30】改成 0.45：现在入库停点由位姿 z 阈值 + 临近减速规划负责，
        // 距离触发退回纯兜底；再留 1.05m 会在车还没到位时抢先刹停。
        _stopReach = 0.45f;
        // 【v28 更正】这里原来写 3.5，但 5025eb2（实测 100 分那一版）StartDemo 尾部有一行
        // `_lookReverseOverride = -1f` 在 BuildPath() 之后执行，把这行覆盖掉了——
        // 也就是说「100 分」那次实际跑的是默认 2.6m 前瞻，3.5 从未真正生效。
        // v27 为修倒库把重置移到 BuildPath() 之前后，3.5 突然生效：倒车弧整体提前起弯，
        // 实跑停车点从 (-14.3,-31.4) 漂到 (-13.9,-30.9)，Ab=1.90 > 1.65 判不成入库到位。
        // 故侧方显式锁回 -1（= 用 _lookReverse 2.6m），与实测 100 分的状态一致。
        _lookReverseOverride = -1f;
        _revSteerCap = 1.0f;
        _cftcStopByPose = true;   // 库内收车：停点取倒车段终点（CftcStopX），z 只做深度校验
        // 场地数据（Tuning/field_data.py ← Unity 权威 dump）：
        //   车道 Z[-30.25,-26.62]；库位 X[-17.75,-9.74] Z[-32.87,-30.25]（深仅 2.62m）
        //   判定 Ab = cz+32.806 ≤ 1.65 → 停稳点 cz ≤ -31.156
        //   车身斜 θ 时 Z 半跨 = 2.25·sinθ + 0.9·cosθ；θ=43° 时 2.23m，
        //   故斜置峰值车中心要在 [-30.64, -28.87] 之间才同时不碰北墙与库底。
        // 考试指南示意图：①车道停稳 → ②直线后倒 → ③右打满约 45° → ④回正直退
        //                → ⑤左打满摆正、车身平行库边停稳。
        // 历史教训：v8 整段北移 0.45 → 右打满时车头甩上北边线 -26.64（进库段必须靠南）；
        //   v6 刚挂 R 就南下 → 车尾在缺口外怼东翼横墙（wp6~8 直线后倒不能省）。
        // 用户定版原则：「出库本质就是反向的入库，走的是同一条线，操作也是类似的」——
        //   故出库不另编路点，直接把入库倒车段逆序回放；入库 右打满→回正→左打满，
        //   沿同一条线反向走，曲率符号自动取反 → 出库 左打满→回正→右打满。
        //   方向盘由路线几何决定，不靠纯跟踪反馈去猜。
        _wps = new Vector3[]
        {
            new Vector3(-39.9f, 0f, -28.5f),  // 0 出生点，沿车道东行
            new Vector3(-30.0f, 0f, -28.5f),  // 1 车道走廊
            new Vector3(-20.0f, 0f, -28.5f),  // 2
            new Vector3(-12.0f, 0f, -28.5f),  // 3
            new Vector3(-7.0f, 0f, -28.6f),   // 4 接近停车观察点，渐向库口一侧靠
            new Vector3(-4.15f, 0f, -28.70f), // 5 ① 前进停稳：距 point_left(-4.15,-28.37) 0.33m
            new Vector3(-6.0f, 0f, -28.70f),  // 6 ② 挂 R 直线后倒，方向盘正（θ=0）
            new Vector3(-7.8f, 0f, -28.70f),  // 7 ② 继续直退
            new Vector3(-9.2f, 0f, -28.75f),  // 8 ② 直退末端：车尾 x≈-11.45 已进库口缺口
            new Vector3(-10.0f, 0f, -28.95f), // 9 ③ 右打满 θ≈14°（v7 南位，车头不扫北墙）
            new Vector3(-10.8f, 0f, -29.45f), // 10 ③ θ≈32°，车尾甩入库口
            new Vector3(-11.6f, 0f, -30.20f), // 11 ③ θ≈43° 斜置峰值；半跨2.23m→北端-27.97/南端-32.43，双向有余
            new Vector3(-12.0f, 0f, -30.58f), // 12 ④ 回正方向，保持约 43° 直退（v9 原值，南下斜率是摆正指令）
            new Vector3(-12.8f, 0f, -30.70f), // 13 ⑤ 左打满摆正 θ≈22°
            new Vector3(-13.5f, 0f, -30.80f), // 14 ⑤ θ≈16°
            new Vector3(-14.2f, 0f, -30.87f), // 15 ⑤ θ≈12°
            new Vector3(-14.8f, 0f, -30.90f), // 16 ⑤ θ≈9°，车身接近平行库边
            new Vector3(-15.1f, 0f, -30.90f)  // 17 ⑤ 水平段 θ=0 停稳
        };

        // 上面这 18 点即「入库主线」，也是 Tuning/field_data.py 静态解析的路线来源。
        // 出库 = 同一条倒车轨迹逆序回放（前进档）：enter[16] → enter[6]，
        // 再接回车道东行收尾。这样整条演示只有一条 S 线，方向盘序列由它的曲率给出。
        Vector3[] enter = _wps;
        var path = new System.Collections.Generic.List<Vector3>(enter);
        for (int i = enter.Length - 2; i >= 6; i--) path.Add(enter[i]); // 16,15,...,6
        path.Add(new Vector3(-4.15f, 0f, -28.70f));  // 回到 point_left 一带
        // 终点要真的开到 End(4.56,-28.44) / WinWall 触发考试结算。
        // 推进逻辑在「距倒数第二点 2.2m」就推进到末点并 FinishDemo，
        // 故倒数第二点须放在 6.7 左右，车中心才会落在 x≈4.5 的 End 触发区。
        // 旧值 0/2.5 让车停在 x≈-2.2，演示自行结束后 Det_CFTC 把静止 1s
        // 判成「中途停车」扣 5 分（v15 实测 95 分）。
        path.Add(new Vector3(6.7f, 0f, -28.44f));
        path.Add(new Vector3(8.5f, 0f, -28.44f));
        _wps = path.ToArray();

        int n = _wps.Length;
        _speeds = new float[n];
        _rev = new bool[n];
        _hold = new float[n];
        _handbrake = new bool[n];
        for (int i = 0; i < n; i++)
        {
            _speeds[i] = 6.5f;              // 全程恒速 6.5 km/h
            _rev[i] = (i >= 6 && i <= 17);  // 6..17 为 R 档入库；推进到 18 时自动换 D 出库
        }
        _hold[5] = 2.5f;  // ① point_left 停稳看点
        _hold[17] = 3f;   // ⑤ 库内停稳，Det_CFTC 判停车到位
        _steps = new DemoStep[]
        {
            new DemoStep { atWp = 0, text = "① 起步：沿车道走廊东行，车身靠右" },
            new DemoStep { atWp = 5, text = "② 行至停车观察点停稳（右后视镜对齐库位前线）" },
            new DemoStep { atWp = 6, text = "③ 挂倒挡直线后倒，车身保持平行" },
            new DemoStep { atWp = 9, text = "④ 车尾到库口后向右打满，车身斜向库位" },
            new DemoStep { atWp = 12, text = "⑤ 车身约 45° 时回正方向，继续直退" },
            new DemoStep { atWp = 13, text = "⑥ 向左打满摆正车头，车身与库位边线平行" },
            new DemoStep { atWp = 17, text = "⑦ 库内停稳：车身与库位边线平行，距线约 30 公分" },
            new DemoStep { atWp = 18, text = "⑧ 挂前进挡，左打满沿原线反向出库" },
            new DemoStep { atWp = 22, text = "⑨ 车身斜出后回正直行，车头驶出库口" },
            new DemoStep { atWp = 25, text = "⑩ 右打满摆正，车身与车道平行" },
            new DemoStep { atWp = 29, text = "⑪ 回到车道东行，完成侧方停车项目" }
        };
    }

    /// <summary>
    /// 半坡起步：起点(33.68,-2.81)朝北 → 上坡贴右边线（x=35.15，距东墙 x=36.37 约 30 公分）
    /// → 精准定点停车 (35.15,41.0)（Det_BPQB 窗口：Bc=|x-32.69|∈[2.4,2.7] ✓2.46，
    ///   Ab=|z-43.58|∈[1.7,2.9] → 合法停车带 z∈[40.68,41.88]，wp6=41.0 居中留足余量）→ 拉手刹
    ///   → 松手刹起步过坡顶 → 下坡 → End(33.78,50.47)。
    /// 旧路线 x≈33 停在 (32.9,43.6)：Bc=0.21 远小于 2.4 → 停车瞬间被判"中途停车"扣 100（3 次全败）。
    /// 注意：爬坡段（驶向 wp5/wp6）目标速度必须保持 10，中途掉到 5 会触发"v>目标+2→刹车"，
    /// 坡上刹车丢速即 stall（2026-11-04 第七轮回归的根因）。
    /// </summary>
    private void BuildBpqbPath()
    {
        _preciseStop = true; // 定点必须停进 Det_BPQB 的 1.2m 判定窗
        _stopReach = 0.45f;  // 半坡定点用默认触发距离（Cftc 改小了 _stopReach，此处复位）
        _bpqbStopByPose = true;   // 真正收车按位姿 z（见 _bpqbStopZ）；_stopReach 只作兜底
        _wps = new Vector3[]
        {
            new Vector3(33.68f, 0f, -2.81f),  // 0 出生点，先向东北并入坡道
            new Vector3(34.8f, 0f, 4.0f),     // 1
            // 2~7 必须贴着右线走：Det_BPQB 用的是 Bc=cos·距离（左右分量），窗口 [2.4,2.7]
            // 相对停车点 x=32.692 → 车心 x 只能落在 35.09~35.39。半坡定点的考试要求就是
            // 「右侧车身距边缘线 30cm 以内」，往左挪会直接判不合格（2026-11-05 实测 34.7 → Bc=2.0 扣 100）。
            new Vector3(35.15f, 0f, 12.0f),   // 2 对正右边线（东墙 x=36.37 以西约 1.25m，车身右缘距墙约 0.34m）
            new Vector3(35.15f, 0f, 22.0f),   // 3 进入坡段
            new Vector3(35.15f, 0f, 32.0f),   // 4 爬坡
            new Vector3(35.15f, 0f, 38.5f),   // 5 接近定点，收油
            new Vector3(35.15f, 0f, 41.0f),   // 6 定点停车（位姿收车 z≥40.95，Ab=43.58-z 落 2.4~2.9 窗内）
            new Vector3(35.15f, 0f, 44.8f),   // 7 松手刹起步过坡顶
            new Vector3(34.6f, 0f, 47.8f),    // 8 下坡稍回正
            new Vector3(33.85f, 0f, 49.6f)    // 9 终点刹停（End 50.47 前，车头距端墙 z=52.2 约 0.3m）
        };
        // 速度数组语义：_speeds[i] = 正在驶向 wps[i] 时的目标车速（wpIndex 到达上一
        // 点前 2.2m 就提前推进）。死亡区 z 29.8→38.5 属于"正在驶向 wp5"段 → 必须改
        // _speeds[5]（2026-11-04 第二轮就是改错了索引，改成了 [3][4]）。
        // Det_BPQB 只要一帧 Speed==0（轮速取整，<0.5km/h 即 0）且未经窗口就扣 100；
        // 非路线模式油门死区 1.0km/h：旧目标 7 时 v=6 油门切 0，坡上滑行掉速，实测
        // 在 z≈35.5 轮速归零判中途停车。爬坡各段目标 10，油门持续 ≥0.35 顶着坡。
        // 关键：wp6（定点段）目标也保持 10——中途掉到 5 会触发"v>目标+2→刹车"，坡上
        // 刹车丢速即 stall（第七轮回归根因）；定点靠精准停点在 StopReach 内硬刹停，不受影响。
        _speeds = new float[] { 9, 9, 10, 10, 10, 10, 10, 9, 9, 8 };
        _rev = new bool[] { false, false, false, false, false, false, false, false, false, false };
        _hold = new float[] { 0, 0, 0, 0, 0, 0, 3f, 0, 0, 0 };
        _handbrake = new bool[] { false, false, false, false, false, false, true, false, false, false };
        _steps = new DemoStep[]
        {
            new DemoStep { atWp = 0, text = "① 起步：对正坡道，挂 D 档上坡，车身距右边线约 30 公分" },
            new DemoStep { atWp = 3, text = "② 接近定点时松油门，轻带刹车控制车速缓行上坡" },
            new DemoStep { atWp = 6, text = "③ 前保险杠对准定点标线——踩死刹车停稳，拉起手刹（定点完成）" },
            new DemoStep { atWp = 7, text = "④ 松手刹，轻踩油门半联动起步，防止车辆后溜" },
            new DemoStep { atWp = 8, text = "⑤ 越过坡顶后控制车速下坡，驶向出口完成项目" }
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
        // 定版 401 的实测参数：入弯贴右 0.35m（ZjzwRouteCfg 注释：正=向南侧/外线偏移）。
        // 缺这一句时入口段跟车道中线 z=-33.5，满舵内切 0.35m 会挂上内侧直角
        // (34.41,-31.62)——2026-11-05 单科实测正是挂在这里扣 100。调参轮由 ApplyTune 注入。
        if (t == null)
        {
            _routeCfg.entryLaneOffset = 0.35f;
            _routeCfg.turnBias = 0f;
            _routeCfg.radiusMax = 3.2f;
        }
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
