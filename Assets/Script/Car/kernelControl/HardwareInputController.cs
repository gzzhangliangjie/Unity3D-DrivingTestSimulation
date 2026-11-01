using UnityEngine;

/// <summary>
/// 【外接方向盘/踏板输入控制器】
/// 通过 Unity 旧版输入系统（DirectInput）读取物理方向盘设备的轴与按键，
/// 驱动 DrivingKernel 的阿克曼转向与模拟量油门/刹车。
///
/// 使用方法：
/// 1. 把本脚本挂到车体（或任意常驻物体）上；
/// 2. 在 ProjectSettings/InputManager.asset 中确认已添加
///    WheelSteer / WheelThrottle / WheelBrake / WheelClutch 轴（本仓库已内置）；
/// 3. 运行时检测到方向盘设备（Input.GetJoystickNames）后自动接管；
/// 4. 不同品牌设备的轴序可能不同（罗技 G29/G923、图马思特等），
///    若油门刹车映射反了，在 Inspector 里调整 AxisThrottle/AxisBrake 的轴名即可。
///
/// 注意：力反馈需要厂商 SDK（如罗技 Logitech Gaming Software SDK），本脚本不包含。
/// </summary>
public class HardwareInputController : MonoBehaviour
{
    [Header("===== 轴名映射（对应 InputManager.asset） =====")]
    [Tooltip("方向盘转向轴，输出 -1..1")]
    public string axisSteer = "WheelSteer";
    [Tooltip("油门踏板轴，输出 0..1（分离式踏板设备）")]
    public string axisThrottle = "WheelThrottle";
    [Tooltip("刹车踏板轴，输出 0..1")]
    public string axisBrake = "WheelBrake";
    [Tooltip("离合器踏板轴，输出 0..1（预留，科目二 C1 半联动可用）")]
    public string axisClutch = "WheelClutch";

    [Header("===== 按键映射 =====")]
    [Tooltip("手刹按键（罗技 G29 序列闸一般是 JoystickButton 手刹或离合全踩）")]
    public KeyCode handbrakeKey = KeyCode.JoystickButton4;
    [Tooltip("油门刹车合并式设备（如手柄扳机）时勾选：油门=throttle轴-刹车，刹车=另一轴")]
    public bool useTriggerStyle = false;

    [Header("===== 调节 =====")]
    [Tooltip("转向死区（设备回中误差）")]
    [Range(0f, 0.3f)]
    public float steerDeadZone = 0.05f;
    [Tooltip("踏板死区（很多踏板松开时不是精确的 0）")]
    [Range(0f, 0.3f)]
    public float pedalDeadZone = 0.03f;
    [Tooltip("整体启用开关")]
    public bool enableHardwareInput = true;

    [Header("===== 键盘模拟（没有方向盘时） =====")]
    [Tooltip("未检测到方向盘设备时，允许用键盘模拟：A/D(或方向键)转向，W(上)油门，S(下)/空格刹车")]
    public bool keyboardFallback = true;
    [Tooltip("键盘打方向的速度（归一化/秒），越大方向盘打得越快")]
    public float keyboardSteerSpeed = 2.5f;

    private DrivingKernel _kernel;
    public bool DeviceConnected { get; private set; }
    private float _kbSteer = 0f;          // 键盘模拟的转向量 -1..1
    private float _kbHoldTimer = 0f;      // 松键后保持接管一小段时间，避免和 UI 打架

    private void Start()
    {
        _kernel = FindObjectOfType<DrivingKernel>();
        RefreshDeviceState();
    }

    private void Update()
    {
        if (!enableHardwareInput || _kernel == null)
            return;

        RefreshDeviceState();

        if (DeviceConnected)
        {
            UpdateHardwareInput();
        }
        else if (keyboardFallback)
        {
            UpdateKeyboardInput();
        }
    }

    private void UpdateHardwareInput()
    {
        // ---------- 转向 ----------
        float steer = Input.GetAxis(axisSteer);
        steer = ApplyDeadZone(steer, steerDeadZone);
        _kernel.Hardware_SetSteering(steer);

        // ---------- 踏板 ----------
        float throttle = ApplyDeadZone(Input.GetAxis(axisThrottle), pedalDeadZone);
        float brake = ApplyDeadZone(Input.GetAxis(axisBrake), pedalDeadZone);

        if (useTriggerStyle)
        {
            //合并式扳机：轴范围 -1..1，松开是 -1，按下是 1
            throttle = Mathf.Clamp01((Input.GetAxis(axisThrottle) + 1f) * 0.5f);
            brake = Mathf.Clamp01((Input.GetAxis(axisBrake) + 1f) * 0.5f);
        }

        _kernel.Hardware_SetPedals(throttle, brake);

        // ---------- 手刹 ----------
        if (Input.GetKeyDown(handbrakeKey))
        {
            Car_HandleBrake handle = FindObjectOfType<Car_HandleBrake>();
            if (handle != null)
            {
                handle.Activate = !handle.Activate;
            }
        }
    }

    private void UpdateKeyboardInput()
    {
        bool left = Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow);
        bool right = Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow);
        bool acc = Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow);
        bool brk = Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.Space);
        bool anyKey = left || right || acc || brk;

        if (anyKey)
        {
            //渐进打方向：模拟真实方向盘的转动过程，而不是瞬间打到头
            int dir = (right ? 1 : 0) - (left ? 1 : 0);
            if (dir != 0)
            {
                _kbSteer = Mathf.Clamp(_kbSteer + dir * keyboardSteerSpeed * Time.deltaTime, -1f, 1f);
            }
            _kbHoldTimer = 0.4f;
            _kernel.Hardware_SetSteering(_kbSteer);
            _kernel.Hardware_SetPedals(acc ? 1f : 0f, brk ? 1f : 0f);
        }
        else
        {
            //松键后：方向盘自动回正，油门刹车归零；回正完并超时后把控制权还给 UI
            _kbSteer = Mathf.MoveTowards(_kbSteer, 0f, keyboardSteerSpeed * 0.8f * Time.deltaTime);
            if (_kbHoldTimer > 0f)
            {
                _kbHoldTimer -= Time.deltaTime;
                _kernel.Hardware_SetSteering(_kbSteer);
                _kernel.Hardware_SetPedals(0f, 0f);
            }
            else if (Mathf.Abs(_kbSteer) < 0.01f)
            {
                _kbSteer = 0f;
                _kernel.Hardware_ReleaseControl();
            }
        }
    }

    /// <summary>检测是否接入了方向盘类设备（有 Joystick 即视为可用）</summary>
    private void RefreshDeviceState()
    {
        string[] joysticks = Input.GetJoystickNames();
        DeviceConnected = false;
        for (int i = 0; i < joysticks.Length; i++)
        {
            if (!string.IsNullOrEmpty(joysticks[i]))
            {
                DeviceConnected = true;
                break;
            }
        }
    }

    /// <summary>死区处理，返回 -1..1 或 0..1 归一化值</summary>
    private static float ApplyDeadZone(float value, float deadZone)
    {
        if (Mathf.Abs(value) < deadZone)
            return 0f;
        return value;
    }
}
