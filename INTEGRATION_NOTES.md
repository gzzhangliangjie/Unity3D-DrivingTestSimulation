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

## 七、直角转弯自动调参工具（ZjzwTuner + tune_zjzw.py，2026-11-03 新增）

调直角转弯演示参数时，"改 .cs → Unity 重编译 → 域重载 → 正在跑的演示被打断 → 重进项目再点演示"太浪费。
本工具把参数全部**外部化到项目根 `Tuning/` 目录**（在 Assets 外，Unity 不导入、不触发重编译），
Unity 里**只需开一次工程**，批量自动重试并把结果落盘，Python 侧分析选出最优参数。

### 组件
| 文件 | 作用 |
|---|---|
| `Assets/Script/MainGUI/Tutorial/ZjzwTuner.cs` | 常驻调参循环器：按 T 启停批次；每轮热加载 `Tuning/zjzw_tuning.json` → 注入 DemoDriver → 自动演示 → 结果追加写 `Tuning/zjzw_results.jsonl` → 自动重载 Exam Mode 进下一轮 |
| `DemoDriver.cs`（改造） | 路径跟踪 const 参数改为可注入字段；`BuildZjzwPath()` 由参数程序化生成；调参轮失败检测（扣分/结算暂停/超时/卡死）+ 轨迹采样（0.25s）+ 与参考路径最大偏差统计 |
| `Tuning/tune_zjzw.py` | `gen`（随机生成候选）/ `best`（按合格率>偏差>用时排名，导出 `zjzw_best.json`）/ `plot`（轨迹 SVG 对比图 `zjzw_plot.svg`）/ `reset`（清空结果重跑） |
| `Tuning/zjzw_tuning.json` | 候选参数批次配置（12 组默认候选：基线组 + 前瞻/增益/半径/打方向时机/弯速单变量 + 组合组） |

### 闭环用法（只开一次工程）
1. `python Tuning/tune_zjzw.py gen`（或手改 `zjzw_tuning.json`）
2. Unity 打开工程 → 考试模式 → 直角转弯单项练习 → **按 T**：批次自动连续跑（候选 × repeat 轮），
   期间外部改 `zjzw_tuning.json` 下一轮立即生效，**不要改 .cs**（会域重载打断批次）
3. `python Tuning/tune_zjzw.py best` → 最优参数导出到 `zjzw_best.json`（贴回 candidates 精调，或固化进 `BuildZjzwPath` 默认值）
4. `python Tuning/tune_zjzw.py plot` 看轨迹对比
5. 重跑批次前 `python Tuning/tune_zjzw.py reset`

### 设计要点
- 每轮结束**重载 Exam Mode 场景**复位（而非原地复位）：压线墙撞后被 `Wall.cs` 销毁、分数/结算面板/
  项目 30s 计时器只有场景重建才能 100% 复位；`SelectPass` 是跨场景单例，sites 保持直角转弯
- 失败判定直接用真实考试反馈：分数从本轮初始值下降（压线/中途停车/超时都扣 100）→ 记失败；
  到达路径终点 → 记成功。不依赖旁路判定，调出来的参数天然满足考试规则
- `MyGameManager` 结算触发的 `timeScale=0`/结算面板由工具在同一帧内恢复，批次不会卡死
- 调参轮中 G/R 键被忽略（防人工干预丢数据）；批次结束自动恢复
- 域重载（改代码/重编译）会打断批次，但进度从 `zjzw_results.jsonl` 行数恢复，按 T 续跑剩余轮次

## 八、直角转弯·几何闭环路线（ZjzwRoute，2026-11-03 新增）

**背景**：旧的固定提前量方案（`cornerX - lockLead` 到点打死）实测早打 0.87m，弧心西移 1m，车身左前角蹭内侧直角墙；12 组参数全在 (32.9,-32.9) 扣 100 分，轨迹完全一致（说明是几何错，不是参数没调好）。

**做法**（`Assets/Script/MainGUI/Tutorial/ZjzwRoute.cs`）：不再"到某个 x 就打死"，改为每帧按真实位姿反解：

```
左转 90° 落点几何：R = (exitX - x) / (1 - cos yaw)
方向盘转角：      δ = atan(wheelBase / R)，归一化 = δ / steerAngle
```

- `R` 大于窗口上限 → 说明还没到转弯点（或已被顶偏），沿入口道中线走，绝不提前画弧；
- `R` 落在窗口内 → 按 `R` 打方向（这个式子逐帧自洽，偏离会在下一帧被拉回来）；
- `R` 小于满舵最小半径（3.33m）→ 判"打晚了"，打满并在出口段纠偏；
- 航向已转过 30° 后几何退化，不再凑落点，改为打完这一把方向 + 出口道中线收口（前瞻 5m）；
- 每次判级变化打一条 `[ROUTE] 动态调整#n：原因 pos/yaw/Rreq/cte`，可直接看出它为什么改路线。

**几何真值**（从 Exam Mode 场景压线墙实测，别再手调）：入口道中心 z=-33.48（墙 -35.35/-31.6）、出口道中心 x=36.42（墙 34.41/38.44）、内侧直角点 (34.4,-31.6)。

**实测**（ZjzwAutoRunner 无人值守跑批，`Logs/zjzw_rep6.log`、`Logs/zjzw_exit5.log`）：9 轮 9 通过、均 100 分、单轮 13.5s；出弯横向落点 x=36.6（离中线 0.18m）。对比修复前 12/12 失败。

**三个标定量**（`ZjzwTune` 里可热注入，默认值已实测可用，一般不用动）：`routeRadiusMax=3.2`（转弯窗口半径上限，3.6 会早打内切）、`routeEntryOffset`（入口贴外线，米）、`routeTurnBias`（落点东移，米）。`dynamicRoute=false` 可切回旧的固定提前量方案做对照。

### 八·1 最终定版参数（id=301，编辑器实跑 3/3 通过，2026-11-03 19:55）

```
dynamicRoute     = true     // 几何闭环路线（核心开关）
routeRadiusMax   = 3.2      // 转弯窗口半径上限（3.6 会早打内切压线，勿越 3.5）
routeEntryOffset = 0.0      // 入口贴外线偏移（米）
routeTurnBias    = 0.0      // 落点东移补偿（米）

// 以下为常规跟踪/车速参数（配合几何闭环）
cornerX=33.0  entryZ=-33.5  exitEndZ=-19.2  radius=3.33
vApproach=9  vCorner=6  vExit=9  throttleMax=0.8
steerGain=2.4  steerSpeedFwd=2.4  lookForward=3.0  arriveDist=2.2
releaseYaw=6  maxRoundSeconds=45
```

实测成绩：3 轮全过、均 100 分、13.5s/轮、终点点 (36.6,-23.5) yaw=0（正东），三轮轨迹完全一致。
`Tuning/zjzw_tuning.json` 即当前定版配置；跑批复现：`touch Tuning/zjzw_autostart.flag` + Play（或命令行 -autoZjzw）。
编辑器内远程触发：写 `Tuning/zjzw_editorplay.flag` → ZjzwWatchRunner 自动进 Play，跑完自动停。
