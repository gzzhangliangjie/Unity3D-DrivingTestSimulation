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
- **键盘**：A/D 或方向键渐进打方向（`keyboardSteerSpeed` 控制打轮速度），W/↑ 油门，S/↓/空格 刹车，**H 手刹**，**1/2/3/4 直选 P/R/N/D 档**，**Q/E 左/右转向灯**，**F 双闪**，**V 切换视角**
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

1. **打 Tag**：Edit → Project Settings → Tags and Layers，添加 `LeftFrontWheel`、`RightFrontWheel`、`LeftRearWheel`、`RightRearWheel`、`VehicleBody`、`Obstacle`，分别挂到车体对应 Collider 上（EasyDriving 检测器需要；主项目 Wall 压线方案不依赖这些 tag）
2. `WheelStateDetector` 可用于倒车入库"车身出库"判定：订阅 `OnAllWheelEnterAndStop` 等

### 压线检测现状（2026-11-03 排查并补齐）

| 科目 | 压线实现 | 状态 |
|---|---|---|
| 侧方位停车 / 倒车入库 / 直角转弯 / 半坡定点 | `collider_*.obj`（2 米高墙状 MeshCollider）+ `Wall.cs`，已在 Exam Mode 场景摆放 | ✅ 正常 |
| 曲线行驶 | 场景内 `Qxxs_WinWall`：按考场俯视截图提取的两条边线折线生成的 2 米高连续竖直墙（`QxxsSidelineMesh.asset`，133 段）+ `Wall.cs` | ✅ 已生成并保存进场景 |

**Wall.cs 已修的问题**：
- 旧写法 `collision.gameObject.tag == "Player"` 只有车身根节点碰撞体（tag=Player）能触发；车轮 WheelCollider 在未打 tag 的子物体上，车轮先压线不判。已改为按 `collision.rigidbody`（整车）判定，车轮/车身碰到都算
- 压线时补播了 `Play_YaXian_100()` 语音（原来代码里从来没人调用），延迟 0.6s 避开碰撞音
- 加了一次性防重复扣分守卫 + 1.5s 全局冷却（曲线行驶墙是连续网格，一次压线会连续接触，防止重复扣分）；选中时画 BoxCollider 黄色线框

**曲线行驶墙的来历**：`collider_QuXianXingShi.obj` 从未摆进场景且形状与实际画线不符（弃用）。实际 S 弯白线是画在路面贴图里的，位置通过对 Exam Mode 拍俯视截图 → 局部对比度分割 + 骨架化提取两条边线折线（世界坐标）得到，与画线重合已人工核验。折线数据内嵌在 `Assets/Editor/QxxsSidelineBuilder.cs`；菜单「驾考工具 → 1-生成曲线行驶压线墙」可重新生成（幂等），「2-渲染压线墙验证截图」可临时以红色渲染墙体拍俯视图核对（竖直墙俯视投影为零，需斜视角才能看到）。墙体 MeshRenderer 默认关闭（游戏内不可见），编辑器中勾选可查看。

## 五、其他可借鉴未搬运的部分

EasyDriving 的模块化车辆架构（`Engine/Transmission/Chassis/Steering` 独立类）需要重构整个车体预制体才能落地，收益/成本比低，暂未搬运；`GetWorldPose` 轮网同步同理（当前 rpm 旋转方案在现有模型层级下视觉正常）。

## 六、实操演示模式（DemoDriver，2026-11-03 新增）

新增 `Assets/Script/MainGUI/Tutorial/DemoDriver.cs`：电脑自动驾驶**玩家的车**，按真实考试流程完整跑一遍标准路线，配分步字幕讲解（文案按驾校教练实际教法编写）。**五个项目全部支持**：倒车入库、侧方停车、半坡起步、直角转弯、曲线行驶。

### 设计原则：与考试流程完全对齐，不搞特殊
- 演示期间考试判定**全部正常工作**：五个检测器（Det_*）、压线墙（Wall）、出口空气墙、结束触发器（End/Victory）、进度播报（JinDu）、计分结算，一个都不禁用
- 演示车走的是合格路线 → 不压线、不撞墙、走完正常触发结算弹出「成绩合格」
- 若路线有偏差被扣分，结算照常触发——这正是路径需要调校的真实反馈
- 唯一接管：演示中暂停 `HardwareInputController`（玩家键盘/外设输入），相当于教练代开时学员不碰方向盘；演示停止立刻恢复
- 演示路径终点设在 End 触发器处/之前刹停，不会去撞出口空气墙

### 使用方法
1. 主页 → 考试模式（单项练习）→ 选择任一项目
2. 进入场地后，点击右上角**【演示】**按钮或按 **G** 键开始演示
3. 演示中：**V** 切换视角；再按 **G** 中途退出；**R** 重新开始本项练习
4. 演示会自动把车传回项目起点、挂 D 档、松手刹，转向灯按教学步骤自动开关

### 实现要点
- 零场景改动：脚本通过 `RuntimeInitializeOnLoadMethod` 在进入 Exam Mode 单项练习时自动挂到玩家车辆上；字幕条与演示按钮全部运行时创建（复用场景现有字体保证中文渲染）
- 控制链路复用外接方向盘接口：`DrivingKernel.Hardware_SetSteering / Hardware_SetPedals`，屏幕方向盘与档杆动画全程联动
- 路径跟踪：纯跟踪（Pure Pursuit）+ 分段限速（直道 8~9 km/h、弯中 6~7 km/h、倒车 4 km/h）
- 倒车项目（倒库/侧方）：每段路径标注 `_rev[i]`（该段是否 R 档），到达换档点自动刹停 → `GearsDangWei_Control.R()/D()` 换档（档杆动画联动）→ 等待 0.8s → 继续；倒车用镜像转向律 `atan2(local.x, |local.z|)`（倒车时方向盘往哪打车尾往哪去）
- 点位停车：`_hold[i]` 到达后刹停保持秒数（倒库看点、侧方停稳、半坡定点）
- 半坡起步：定点停车点 `_handbrake[i]` 自动拉手刹，保持 2.5 秒后自动松开、半联动起步
- 演示结束**不做任何恢复动作**（考试组件从未被禁用），成绩结算按考试流程正常进行

### 调校
- 直角转弯/曲线行驶：路径按场景实测，一般无需改动
- 倒车入库/侧方停车/半坡起步：路径为按库位坐标（Point_garage / Point_left / Point_right）推算的**估算值**，实测压线/撞角时直接改 `DemoDriver.cs` 里 `BuildDcrkPath()` / `BuildCftcPath()` / `BuildBpqbPath()` 的坐标（X/Z 世界坐标，每个点都有注释说明用途）
- 半坡爬坡无力时，把 `UpdateRun()` 里油门上限 `0.8f` 再调大
- 演示中被扣 100 分会像真实考试一样弹「不合格」并暂停，按 R 重开即可
