using UnityEngine;
using System.Collections;

/// <summary>
/// 【汽车核心类】汽车动能类
/// 融合 EasyDriving 项目的改进（原项目: https://github.com/HWliiu/EasyDriving）：
/// 1. 阿克曼转向角修正（内外前轮转角按几何差速计算，替代原来左右轮同角度）
/// 2. 模拟量油门/刹车输入（0~1 连续值，为外接方向盘踏板与半坡半联动控制铺路）
/// 3. 统一的每帧制动力合成（UI 刹车 / 模拟量刹车 / 手刹互不冲突）
/// 兼容性：所有原有 public 字段与方法签名均保留，预制体与场景引用不受影响。
/// </summary>
public class DrivingKernel : MonoBehaviour
{
    [Header("汽车核心驱动类(PC/Phone)")]
    public WheelCollider flWheelCollider;
    public WheelCollider frWheelCollider;
    public WheelCollider rlWheelCollider;
    public WheelCollider rrWheelCollider;

    public Transform flWheelModel;//内部控制轮子转动（子物体）
    public Transform frWheelModel;//内部控制轮子转动（子物体）
    public Transform rlWheelModel;
    public Transform rrWheelModel;

    public Transform flDiscBrake, frDiscBrake, centerOfMass;//控制转向外部（父物体），centerOfMass是重心

    public float motorTorque = 1600;//最大马达值
    public float steerAngle = 38;//车轮转向角度
    public float brakeTorque = 2000;//刹车力量

    public Transform SteeringWheel;//方向盘-变换组件
    public float CurrentWheelAngle = 0;//当前车轮旋转角度

    //方向盘总角度：550=360+180+10
    //转换比（520/38=13.684210526315789473684210526316）--方向盘总度数520：转向向角38
    public const float ratio = 13.684210526315789473684210526316f;
    [Header("就是汽车实际运行速度(KM/H)")]
    public float speed = 0;
    public float speedOrigin = 0;

    [Header("方向盘UI预制件")]
    [Tooltip("自动查找并赋值，无需管理！")]
    public SteeringWheel2D steeringWheelUi;//UI方向盘鱼前车轮同步

    [Header("===== EasyDriving 融合参数 =====")]
    [Tooltip("启用阿克曼转向：内侧前轮转角大于外侧，转弯几何更接近真车")]
    public bool useAckermannSteering = true;
    [Tooltip("轴距（米）：前后轴中心距离，用于阿克曼计算")]
    public float wheelBase = 2.6f;
    [Tooltip("后轮距（米）：左右后轮中心距离，用于阿克曼计算")]
    public float rearTrack = 1.5f;
    [Tooltip("模拟量输入是否生效（由 HardwareInputController 自动置位）")]
    public bool analogInputActive = false;

    [Header("UI档位类")]
    [Tooltip("自动查找并赋值，无需管理！")]
    [SerializeField]
    private GearsDangWei_Control Gears;
    [SerializeField]
    private Car_HandleBrake Handle;
    private bool Handle_Flag = true;

    // ---------- 内部输入状态 ----------
    private float _steerInput = 0f;      // 归一化转向输入 -1(左)..1(右)
    private float _analogThrottle = 0f;  // 模拟量油门 0..1
    private float _analogBrake = 0f;     // 模拟量刹车 0..1
    private bool _uiBrakeActive = false; // UI 布尔刹车状态
    private float _flSteerDeg = 0f;      // 左前轮实际转角（阿克曼修正后）
    private float _frSteerDeg = 0f;      // 右前轮实际转角

    private void Start()
    {
        //利用centerOfMass空物体（这个物体在车地盘下）改变车的重心，防止翻车。
        Rigidbody rigidboy = this.GetComponentInParent<Rigidbody>();
        rigidboy.centerOfMass = centerOfMass.localPosition;

        Gears = FindObjectOfType<GearsDangWei_Control>();
        steeringWheelUi = FindObjectOfType<SteeringWheel2D>();
        Handle = FindObjectOfType<Car_HandleBrake>();
    }

    private void Update()
    {
        // ---------- 转向输入源选择 ----------
        // 外接方向盘激活时使用硬件轴，否则使用 UI 方向盘
        if (analogInputActive)
        {
            _steerInput = Mathf.Clamp(_hardwareSteer, -1f, 1f);
        }
        else
        {
            //UI 方向盘引用可能失效（加载初期面板未激活、或运行中被销毁的模板实例），
            //懒补找并做空保护，避免空引用把整个 Update 在转向之前打断
            if (steeringWheelUi == null)
                steeringWheelUi = FindObjectOfType<SteeringWheel2D>();
            _steerInput = steeringWheelUi != null
                ? steeringWheelUi.wheelAngle / steeringWheelUi.maximumSteeringAngle
                : 0f;
        }

        // ---------- 阿克曼转向角计算 ----------
        ApplyAckermannSteering(_steerInput);

        // ---------- 模拟量油门（外接踏板） ----------
        if (analogInputActive)
        {
            ApplyAnalogThrottle();
            ApplyBrakeForces();
        }

        //转向系统
        SteerWheel();
        //转动车轮
        RotateWheel();
        //手刹 + 布尔刹车（仅模拟量未激活时在此统一处理，模拟量路径已在上面合成）
        if (!analogInputActive)
        {
            ApplyBrakeForces();
        }
    }

    /// <summary>
    /// 阿克曼转向：由归一化转向输入计算左右前轮各自的转角并应用到 WheelCollider。
    /// 公式：R = L / tan(δ)（转弯半径），内侧轮 atan(L / (R - T/2))，外侧轮 atan(L / (R + T/2))。
    /// L=wheelBase 轴距，T=rearTrack 轮距。
    /// </summary>
    private void ApplyAckermannSteering(float steerInput)
    {
        float steerDeg = steerInput * steerAngle;

        if (!useAckermannSteering || Mathf.Abs(steerDeg) < 0.01f)
        {
            _flSteerDeg = steerDeg;
            _frSteerDeg = steerDeg;
        }
        else
        {
            float radius = wheelBase / Mathf.Tan(Mathf.Abs(steerDeg) * Mathf.Deg2Rad);//转弯半径
            float innerAngle = Mathf.Atan(wheelBase / Mathf.Max(radius - rearTrack * 0.5f, 0.01f)) * Mathf.Rad2Deg;
            float outerAngle = Mathf.Atan(wheelBase / (radius + rearTrack * 0.5f)) * Mathf.Rad2Deg;

            //上面按 |steerDeg| 算出的都是正角度，必须回乘转向符号，否则左右转都变成同一个方向
            float sign = Mathf.Sign(steerDeg);

            //steerDeg > 0（右转）：右前轮为内侧；steerDeg < 0（左转）：左前轮为内侧
            if (steerDeg > 0f)
            {
                _frSteerDeg = sign * innerAngle;
                _flSteerDeg = sign * outerAngle;
            }
            else
            {
                _flSteerDeg = sign * innerAngle;
                _frSteerDeg = sign * outerAngle;
            }
        }

        flWheelCollider.steerAngle = _flSteerDeg;
        frWheelCollider.steerAngle = _frSteerDeg;
    }

    /// <summary>
    /// 按当前档位应用模拟量油门（0~1），D 档前进 / R 档倒车 / P、N 档无动力。
    /// </summary>
    private void ApplyAnalogThrottle()
    {
        //档位组件可能加载初期找不到（UI 未激活）或运行中被销毁，懒补找 + 空保护
        if (Gears == null)
            Gears = FindObjectOfType<GearsDangWei_Control>();

        float torque = 0f;
        if (Gears != null && _analogThrottle > 0.001f)
        {
            switch (Gears.gears)
            {
                case GearsDangWei_Control.currentGears.D:
                    torque = _analogThrottle * motorTorque;
                    break;
                case GearsDangWei_Control.currentGears.R:
                    torque = -_analogThrottle * motorTorque;
                    break;
            }
        }
        rlWheelCollider.motorTorque = torque;
        rrWheelCollider.motorTorque = torque;
    }

    /// <summary>
    /// 统一制动力合成：取 UI 布尔刹车、模拟量刹车中的较大值作用于前轮，
    /// 后轮再叠加手刹。每帧统一写入，避免多条路径互相覆盖。
    /// </summary>
    private void ApplyBrakeForces()
    {
        //手刹组件同样懒补找 + 空保护（为 null 时按未拉手刹处理，不能让异常打断转向）
        if (Handle == null)
            Handle = FindObjectOfType<Car_HandleBrake>();

        float brakeNorm = Mathf.Max(_uiBrakeActive ? 1f : 0f, _analogBrake);
        float brakeFront = brakeNorm * brakeTorque;
        float brakeRear = Mathf.Max(brakeFront, (Handle != null && Handle.Activate) ? brakeTorque : 0f);

        flWheelCollider.brakeTorque = brakeFront;
        frWheelCollider.brakeTorque = brakeFront;
        rlWheelCollider.brakeTorque = brakeRear;
        rrWheelCollider.brakeTorque = brakeRear;

        Handle_Flag = Handle != null && Handle.Activate;
    }

    /*
     * 车轮转动起来，转起来（注意：不是转弯！）
     * 前轮应该是子物体
     */
    private void RotateWheel()
    {
        //前轮（子物体）
        flDiscBrake.Rotate(flWheelCollider.rpm * 6 * Time.deltaTime * Vector3.right);
        frDiscBrake.Rotate(frWheelCollider.rpm * 6 * Time.deltaTime * Vector3.right);
        //后轮
        rrWheelModel.Rotate(rrWheelCollider.rpm * 6 * Time.deltaTime * Vector3.right);
        rlWheelModel.Rotate(rlWheelCollider.rpm * 6 * Time.deltaTime * Vector3.right);

        //汽车速度
        speed = flWheelCollider.rpm * (flWheelCollider.radius * 2 * Mathf.PI) * 60 / 1000;
        //车速-去掉小数点
        speed = Mathf.Round(speed);
        speedOrigin = speed;
        //绝对值
        speed = Mathf.Abs(speed);
    }

    /*
     * 转向（模型视觉同步：前轮模型按各自阿克曼转角偏转）
     */
    private void SteerWheel()
    {
        //左前轮模型
        Vector3 flLocalEuler = flWheelModel.localEulerAngles;
        flLocalEuler.y = _flSteerDeg;
        flWheelModel.localEulerAngles = flLocalEuler;
        //右前轮模型（阿克曼修正后与左轮转角不同）
        Vector3 frLocalEuler = frWheelModel.localEulerAngles;
        frLocalEuler.y = _frSteerDeg;
        frWheelModel.localEulerAngles = frLocalEuler;

        CurrentWheelAngle = _flSteerDeg;

        //同步方向盘（OK，是延自身旋转）
        Vector3 locaRv3 = SteeringWheel.localRotation.eulerAngles;
        locaRv3.z = -CurrentWheelAngle * ratio;
        SteeringWheel.localRotation = Quaternion.Euler(locaRv3);
    }

    // ================= 外接方向盘/踏板接口（由 HardwareInputController 调用） =================

    /// <summary>外接方向盘的归一化转向轴值（-1 左 .. 1 右）</summary>
    private float _hardwareSteer = 0f;

    /// <summary>
    /// 设置外接方向盘转向输入。value: -1(最左)..1(最右)
    /// </summary>
    public void Hardware_SetSteering(float value)
    {
        _hardwareSteer = Mathf.Clamp(value, -1f, 1f);
        analogInputActive = true;
        //反向同步到 UI 方向盘，保持画面视觉一致；UI 停止触摸/回正逻辑
        if (steeringWheelUi != null)
        {
            steeringWheelUi.hardwareControlled = true;
            steeringWheelUi.wheelAngle = _hardwareSteer * steeringWheelUi.maximumSteeringAngle;
        }
    }

    /// <summary>
    /// 释放硬件/键盘接管，把输入控制权还给 UI 方向盘（未接入设备且无按键时由 HardwareInputController 调用）
    /// </summary>
    public void Hardware_ReleaseControl()
    {
        analogInputActive = false;
        _hardwareSteer = 0f;
        _analogThrottle = 0f;
        _analogBrake = 0f;
        if (steeringWheelUi != null)
        {
            steeringWheelUi.hardwareControlled = false;
        }
    }

    /// <summary>
    /// 设置外接踏板模拟量输入。throttle: 0..1 油门，brake: 0..1 刹车（半坡半联动可用中间值）
    /// </summary>
    public void Hardware_SetPedals(float throttle, float brake)
    {
        _analogThrottle = Mathf.Clamp01(throttle);
        _analogBrake = Mathf.Clamp01(brake);
        analogInputActive = true;
    }

    // ================= 原有公开接口（签名不变，外部脚本依赖） =================

    /// <summary>
    /// PUBLIC-油门踏板
    /// </summary>
    /// <param name="press">踏板踩下标记</param>
    public void MobileTerminal_AcceleratorPedal(bool press)
    {
        //档位组件空保护：找不到时无法判断档位，安全退出（与模拟量路径同一懒补找逻辑）
        if (Gears == null)
            Gears = FindObjectOfType<GearsDangWei_Control>();
        if (Gears == null)
            return;

        if(press)
        {
            switch (Gears.gears)
            {
                case GearsDangWei_Control.currentGears.P:
                    break;
                case GearsDangWei_Control.currentGears.R:
                    EnginePower(false);
                    break;
                case GearsDangWei_Control.currentGears.N:
                    CancelEnginePower();
                    break;
                case GearsDangWei_Control.currentGears.D:
                    EnginePower(true);
                    break;
            }
        }
        else
        {
            CancelEnginePower();
            CancelResistance();
        }
    }

    /// <summary>
    /// PUBLIC-刹车踏板
    /// </summary>
    /// <param name="press">踏板踩下标记</param>
    public void MobileTerminal_BrakePedal(bool press)
    {
        switch (press)
        {
            case true:
                Resistance();
                break;
            case false:
                CancelResistance();
                break;
        }
    }

    /// <summary>
    /// 【动力提供】
    /// </summary>
    /// <param name="flag">“真”前进，“假”后退。</param>
    private void EnginePower(bool flag)
    {
        switch (flag)
        {
            case true:
                //前进
                rlWheelCollider.motorTorque = motorTorque;
                rrWheelCollider.motorTorque = motorTorque;
                break;
            case false:
                //后退
                rlWheelCollider.motorTorque = -motorTorque;
                rrWheelCollider.motorTorque = -motorTorque;
                break;
        }
    }

    /// <summary>
    /// 【阻断发动机动力】没有动力
    /// </summary>
    private void CancelEnginePower()
    {
        rlWheelCollider.motorTorque = 0;
        rrWheelCollider.motorTorque = 0;
    }

    /// <summary>
    /// 【刹车】四轮子同时刹车
    /// </summary>
    public void Resistance()
    {
        _uiBrakeActive = true;//交由 ApplyBrakeForces 每帧统一写入
        //四个轮子同时刹车（立即生效，保持与原实现一致的即时响应）
        flWheelCollider.brakeTorque = brakeTorque;
        frWheelCollider.brakeTorque = brakeTorque;
        rlWheelCollider.brakeTorque = brakeTorque;
        rrWheelCollider.brakeTorque = brakeTorque;
    }

    /// <summary>
    /// 【取消所有轮子阻力】
    /// </summary>
    public void CancelResistance()
    {
        _uiBrakeActive = false;
        flWheelCollider.brakeTorque = 0;
        frWheelCollider.brakeTorque = 0;
        rlWheelCollider.brakeTorque = 0;
        rrWheelCollider.brakeTorque = 0;
    }
}
