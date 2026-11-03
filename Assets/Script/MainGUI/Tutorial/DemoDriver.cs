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

    private const float ARRIVE_DIST = 2.2f;   // 到达路径点的判定距离（米）
    private const float LOOK_FORWARD = 3.6f;  // 前进纯跟踪前瞻距离（米）
    private const float LOOK_REVERSE = 2.6f;  // 倒车纯跟踪前瞻距离（倒车精度要求高）

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
    private float _logTimer = 0f;   // 调试遥测计时（调完删）
    private bool _gearReverse;      // 当前档位是否 R（倒车）
    private bool _leftStart;        // 是否已离开起点区域（防止起步当帧跳点）
    private bool _pendingShift;     // 刹停后需要换档
    private bool _pendingFinish;    // 刹停后结束演示
    private float _holdRemaining;   // 当前停车保持剩余秒数
    private float _shiftTimer;      // 换档等待计时
    private float _stuckTimer;      // 防卡死计时
    private bool _demoUsed;         // 用过演示后 R 键才生效

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
        TryInstall(SceneManager.GetActiveScene());
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        TryInstall(scene);
    }

    private static void TryInstall(Scene scene)
    {
        if (scene.name != "Exam Mode") return;

        SelectPass sp = Object.FindObjectOfType<SelectPass>();
        Debug.Log("[DemoDriver] 进入 Exam Mode，sites=" + (sp != null ? sp.sites.ToString() : "<无SelectPass>"));
        if (sp == null) return;
        if (sp.sites == SelectPass.Sites.Default) return; // Default 是全图考试，不装

        DrivingKernel kernel = Object.FindObjectOfType<DrivingKernel>();
        if (kernel == null) return;
        if (kernel.GetComponent<DemoDriver>() != null) return;

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
        _stuckTimer = 0f;
        _wpIndex = 0;
        _stepIndex = 0;
        _gearReverse = false;
        _leftStart = false;
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

    /// <summary>正常行驶：路径推进 + 纯跟踪转向 + 分段限速 + 卡死看门狗</summary>
    private void UpdateRun()
    {
        Vector3 pos = transform.position;

        // 0) 段方向检查：下一段需要 R/D 而当前档位不符 → 刹停换档
        if (_wpIndex < _wps.Length - 1 && _rev[_wpIndex] != _gearReverse)
        {
            BeginStop(true, 0f, false);
            return;
        }

        // 1) 离开起点区域后才允许推进（防止起步当帧从 wp0 跳到 wp1）
        if (!_leftStart && FlatDist(pos, _wps[0]) > ARRIVE_DIST)
        {
            _leftStart = true;
        }

        // 2) 路径点推进：一次只推进一个点，下一帧再处理（不会跳过停车点）
        if (_wpIndex < _wps.Length - 1
            && (_wpIndex > 0 || _leftStart)
            && FlatDist(pos, _wps[_wpIndex]) < ARRIVE_DIST)
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

        // 3) 纯跟踪转向（倒车用镜像转向律：目标在车后方，取 atan2(lx, |lz|)）
        float look = _gearReverse ? LOOK_REVERSE : LOOK_FORWARD;
        Vector3 target = LookaheadPoint(pos, look);
        Vector3 local = transform.InverseTransformPoint(target);
        float ang;
        if (_gearReverse) ang = Mathf.Atan2(local.x, Mathf.Max(-local.z, 0.15f));
        else ang = Mathf.Atan2(local.x, Mathf.Max(local.z, 0.15f));
        float steerTarget = Mathf.Clamp(ang * 2.4f, -1f, 1f);
        float steerSpeed = _gearReverse ? 3.2f : 2.4f; // 倒车打死要更快（入库时机短）
        _steer = Mathf.MoveTowards(_steer, steerTarget, steerSpeed * Time.deltaTime);
        _kernel.Hardware_SetSteering(_steer);

        // 4) 速度控制（分段目标车速，急弯自动降速）
        float vTarget = _speeds[Mathf.Clamp(_wpIndex, 0, _speeds.Length - 1)];
        if (Mathf.Abs(_steer) > 0.5f) vTarget = Mathf.Min(vTarget, 6f);
        float v = _kernel.speed;
        float throttle = 0f;
        float brake = 0f;
        if (v < vTarget - 1f)
        {
            throttle = Mathf.Clamp((vTarget - v) * 0.35f, 0.2f, 0.8f);
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
                ShowCaption("演示中断：车辆受阻（可能碰到了边线）。按 R 重新开始", 8f);
                CancelDemo();
                return;
            }
        }
        else
        {
            _stuckTimer = 0f;
        }

        // 6) 调试遥测（每 0.5s 一条，调完删）
        _logTimer -= Time.deltaTime;
        if (_logTimer <= 0f)
        {
            _logTimer = 0.5f;
            Debug.Log(string.Format("[DEMO] pos=({0:F1},{1:F1}) yaw={2:F0} wp={3}/{4} steer={5:F2} v={6:F1} gearRev={7} distWp={8:F1} throttle={9:F2}",
                pos.x, pos.z, transform.eulerAngles.y, _wpIndex, _wps.Length - 1,
                _steer, v, _gearReverse, FlatDist(pos, _wps[_wpIndex]), throttle));
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

    /// <summary>从车辆当前位置沿路径向前找 look 米处的目标点</summary>
    private Vector3 LookaheadPoint(Vector3 pos, float look)
    {
        float remain = look;
        Vector3 from = pos;
        for (int i = _wpIndex; i < _wps.Length; i++)
        {
            float d = FlatDist(from, _wps[i]);
            if (d >= remain && d > 0.001f)
            {
                float t = remain / d;
                float x = Mathf.Lerp(from.x, _wps[i].x, t);
                float z = Mathf.Lerp(from.z, _wps[i].z, t);
                return new Vector3(x, pos.y, z);
            }
            remain -= d;
            from = _wps[i];
        }
        return new Vector3(_wps[_wps.Length - 1].x, pos.y, _wps[_wps.Length - 1].z);
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

    /// <summary>直角转弯：起点(13.2,-33.7)朝东 → 左转90° → 北向驶出（End 在 36.3,-18.8）</summary>
    private void BuildZjzwPath()
    {
        _wps = new Vector3[]
        {
            new Vector3(13.2f, 0f, -33.7f),
            new Vector3(20.0f, 0f, -33.6f),
            new Vector3(26.5f, 0f, -33.5f),
            new Vector3(29.8f, 0f, -33.3f),
            new Vector3(32.2f, 0f, -32.2f),
            new Vector3(34.6f, 0f, -29.9f),
            new Vector3(35.9f, 0f, -27.0f),
            new Vector3(36.3f, 0f, -23.5f),
            new Vector3(36.4f, 0f, -19.2f)
        };
        _speeds = new float[] { 9, 9, 9, 7, 6, 6, 6, 8, 9 };
        _rev = new bool[] { false, false, false, false, false, false, false, false, false };
        _hold = new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        _handbrake = new bool[_wps.Length];
        _steps = new DemoStep[]
        {
            new DemoStep { atWp = 0, text = "① 起步：挂 D 档缓慢前进，控制车速，沿车道中间偏右行驶" },
            new DemoStep { atWp = 2, text = "② 提前打开左转向灯，转过弯后再关闭", turnSignalOn = true },
            new DemoStep { atWp = 3, text = "③ 当车头盖即将盖过转角横线时，向左打死方向盘" },
            new DemoStep { atWp = 5, text = "④ 慢速转过弯角，注意左后轮不要压到内侧直角" },
            new DemoStep { atWp = 7, text = "⑤ 车身即将转正时，迅速回正方向盘，驶出项目", turnSignalOff = true }
        };
    }

    /// <summary>曲线行驶：起点(41.7,111.0)朝北 → S弯（先左后右）→ 北向驶出（End 在 15.8,147.3）</summary>
    private void BuildQxxsPath()
    {
        _wps = new Vector3[]
        {
            new Vector3(41.7f, 0f, 111.0f),
            new Vector3(41.5f, 0f, 116.5f),
            new Vector3(39.5f, 0f, 122.5f),
            new Vector3(36.0f, 0f, 127.5f),
            new Vector3(31.5f, 0f, 132.0f),
            new Vector3(26.8f, 0f, 135.8f),
            new Vector3(22.8f, 0f, 139.0f),
            new Vector3(19.8f, 0f, 141.8f),
            new Vector3(17.8f, 0f, 144.0f),
            new Vector3(16.4f, 0f, 145.6f),
            new Vector3(15.8f, 0f, 146.8f)
        };
        _speeds = new float[] { 9, 9, 8, 7, 7, 7, 7, 7, 7, 7, 8 };
        _rev = new bool[] { false, false, false, false, false, false, false, false, false, false, false };
        _hold = new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        _handbrake = new bool[_wps.Length];
        _steps = new DemoStep[]
        {
            new DemoStep { atWp = 0, text = "① 车头对准弯道入口、略靠右侧边线，挂 D 档缓慢进入" },
            new DemoStep { atWp = 1, text = "② 当车头左角即将碰到右侧边线时，向左打一圈又四分之一方向" },
            new DemoStep { atWp = 3, text = "③ 保持车头左角始终沿右侧边线行驶，有偏差时小角度微调" },
            new DemoStep { atWp = 5, text = "④ 弯道中段：车头右角即将碰到左侧边线时，先回正方向，再向右打一圈又四分之一" },
            new DemoStep { atWp = 7, text = "⑤ 保持车头右角沿左侧边线行驶，随时微调方向" },
            new DemoStep { atWp = 9, text = "⑥ 车头出弯后，从后视镜观察左后轮与边线平行，回正方向驶出" }
        };
    }

    private void FireStepsAt(int wpIndex)
    {
        while (_stepIndex < _steps.Length && _steps[_stepIndex].atWp <= wpIndex)
        {
            DemoStep step = _steps[_stepIndex];
            ShowCaption(step.text, 0f);

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

    // ============================================================
    //  工具
    // ============================================================
    private static float FlatDist(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
