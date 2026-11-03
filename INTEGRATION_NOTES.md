# EasyDriving 融合改造说明

本项目基于 [SummerRainCity/Unity3D-DrivingTestSimulation](https://github.com/SummerRainCity/Unity3D-DrivingTestSimulation)（MIT 协议），
融合了 [HWliiu/EasyDriving](https://github.com/HWliiu/EasyDriving) 的优点与可复用资源（MIT 协议）。
本 fork：https://github.com/gzzhangliangjie/Unity3D-DrivingTestSimulation

## 一、车辆物理改进（修改 `Assets/Script/Car/kernelControl/DrivingKernel.cs`）

| 改进 | 说明 |
|---|---|
| 阿克曼转向 | 内/外侧前轮转角按 `R = L/tan(δ)` 几何差速计算（借鉴 EasyDriving 的 Chassis.cs），替代原来左右轮同角度。参数：`useAckermannSteering`、`wheelBase`（轴距）、`rearTrack`（轮距），均可在 Inspector 调节 |
| 模拟量油门/刹车 | 原实现油门刹车是布尔开关（按下=满扭矩）。新增 0~1 连续输入接口，半坡起步半联动控制成为可能 |
| 制动力统一合成 | UI 布尔刹车 / 模拟量刹车 / 手刹三种来源每帧取最大值统一写入，不再互相覆盖 |

**兼容性**：原有 public 字段（`steerAngle`、`motorTorque` 等）与方法签名（`MobileTerminal_AcceleratorPedal`、`Resistance` 等）全部保留，预制体与场景引用不受影响。新字段有代码默认值，无需重新配置预制体。

## 二、外接方向盘接入（新增 `HardwareInputController.cs`）

支持 Unity 旧版输入系统识别的 DirectInput 方向盘（罗技 G29/G923、图马思特等）：

1. 把 `HardwareInputController.cs` 挂到场景中任意物体上
2. `ProjectSettings/InputManager.asset` 已内置轴定义：

| 轴名 | 手柄轴 | 用途 |
|---|---|---|
| WheelSteer | Joy1 Axis X (0) | 转向 -1..1 |
| WheelThrottle | Joy1 Axis 1 | 油门 |
| WheelBrake | Joy1 Axis 2 | 刹车 |
| WheelClutch | Joy1 Axis 3 | 离合（预留） |

3. **不同品牌踏板轴序不同**：如果油门刹车映射不对，在脚本 Inspector 里改 `axisThrottle`/`axisBrake` 的轴名（可在 Unity 输入调试器 Input Debugger 里看实际轴值）；分离式踏板保持 `useTriggerStyle = false`，手柄扳机类设备勾选 true
4. 运行时检测到 Joystick 设备自动接管；手刹默认 `JoystickButton4`，键盘手刹默认 `H`，均可在 Inspector 改
5. 力反馈需要厂商 SDK，未包含

### 没有方向盘怎么办？
三种输入方式自动降级，无需配置：
- **键盘**：A/D 或方向键渐进打方向（`keyboardSteerSpeed` 控制打轮速度），W/↑ 油门，S/↓/空格 刹车，**H 手刹**，**1/2/3/4 直选 P/R/N/D 档**，**Q/E 左/右转向灯**，**V 切换视角**
- **鼠标/触摸**：拖拽屏幕 UI 方向盘（原功能不变）
- 外设拔掉后自动切回，键盘松键后约 0.4 秒把控制权还给 UI 方向盘

键位全部可在 `HardwareInputController` 的 Inspector 里改（`gearPKey` / `turnLeftKey` 等字段），改键不用动代码。
档位键走的是 UI 档杆绑定的同一批公开方法（`GearsDangWei_Control.P/R/N/D`），所以档杆动画和档位灯会同步；`GearsDangWei_Control` 内部的换档动画吞输入问题已修（详见代码注释），连按 1~4 以最后一次为准。

## 三、搬运的资源

| 资源 | 位置 | 用途 |
|---|---|---|
| DOTween 补间动画库 | `Assets/Demigiant/DOTween`（含 DLL + Modules + asmdef） | UI 动画/相机过渡可用，主项目原本没有 |
| 4 个检测器 + TagConst | `Assets/Script/DetectionRule/EasyDriving/` | `SidelineDetector`（压线）、`DistanceDetector`（距离）、`StopOffDetector`（中途停车）、`WheelStateDetector`（轮子进出库状态） |

## 四、检测器的场景接线（需在 Unity 编辑器中完成）

脚本已就位，但要生效需要接线（无法用代码改动场景文件）：

1. **打 Tag**：Edit → Project Settings → Tags and Layers，添加 `LeftFrontWheel`、`RightFrontWheel`、`LeftRearWheel`、`RightRearWheel`、`VehicleBody`、`Obstacle`，分别挂到车体对应 Collider 上
2. **压线检测（推荐先补曲线行驶，主项目此项缺失）**：在赛道边缘摆带 Trigger 的碰撞体，挂 `SidelineDetector`，订阅 `OnWheelOutlet`（轮子压线）/`OnVehicleBodyOutlet`（车身出线）事件，接 `Car_Information.DeductScore(100)`
3. `WheelStateDetector` 可用于倒车入库"车身出库"判定：订阅 `OnAllWheelEnterAndStop` 等

## 五、其他可借鉴未搬运的部分

EasyDriving 的模块化车辆架构（`Engine/Transmission/Chassis/Steering` 独立类）需要重构整个车体预制体才能落地，收益/成本比低，暂未搬运；`GetWorldPose` 轮网同步同理（当前 rpm 旋转方案在现有模型层级下视觉正常）。
