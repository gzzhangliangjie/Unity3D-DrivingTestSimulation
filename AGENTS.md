# AGENTS.md

> 本文件供 AI Agent / 新 contributors 阅读。动手改代码前先看完，能避开本项目绝大多数坑。
> 项目定位：**Unity3D 科目二模拟驾考**（fork 自 SummerRainCity/Unity3D-DrivingTestSimulation v1.6，MIT），
> 本 fork 额外融合了 [HWliiu/EasyDriving](https://github.com/HWliiu/EasyDriving) 的车辆物理与检测器，
> 并新增外接方向盘 / 键盘输入支持。改造细节见 [INTEGRATION_NOTES.md](INTEGRATION_NOTES.md)。

---

## 1. 环境事实

| 项 | 值 |
|---|---|
| Unity 版本 | **2022.3.57f1c2**（`ProjectSettings/ProjectVersion.txt`） |
| 渲染/物理 | Built-in 管线，`WheelCollider` + `Rigidbody`（`com.unity.modules.vehicles`） |
| Package 依赖 | 仅内置 modules + `com.unity.ugui` 1.0.0，**没有任何 Package Manager 第三方依赖** |
| 第三方库（随 Assets 走） | `Assets/iTween/`（源码）、`Assets/Demigiant/DOTween/`（DLL + asmdef） |
| 输入系统 | **Unity 旧版 Input Manager**（`ProjectSettings/InputManager.asset`），未用 Input System 包 |
| 目标平台 | Android / Windows（`Assets/Plugins/Android/` 下的配置目前是 `.DISABLED`） |
| 脚本规模 | 自研 52 个 `.cs`（`Assets/Script` + `Assets/Tool`），5 个场景，281 个 prefab |

**没有 CI，没有单元测试，没有可命令行调用的编译流程。**
Unity 编译必须在编辑器里进行。Agent 改完代码后**无法自行验证编译**，请在回复中明确告知用户需要在 Unity 中打开确认 Console 无报错，不要声称"已编译通过"。

---

## 2. 目录地图

```
Assets/
├── Script/
│   ├── Car/                      车辆：转向灯、语音提示
│   │   └── kernelControl/        ★ 车辆驱动核心 + 相机 + 输入
│   ├── DetectionRule/            ★ 考试规则判定
│   │   ├── RuleScript/           五个科目的 Det_*.cs + End/Victory
│   │   └── EasyDriving/          搬运来的触发器检测器（尚未接线）
│   ├── MainGUI/                  UI、场景切换、档位、方向盘
│   │   ├── SelectSceneScript/    单项练习选择（跨场景单例）
│   │   └── Tutorial/             教程/关于/好友面板
│   └── Materials/                材质
├── Tool/Record result/           Replay 录制回放（ReplayManager / ReplayEntity）
├── Scene/                        Main Menu / Exam Mode / Free Mode / Loading / Tutorial
├── Prafab/                       （原文如此，非拼写错误，勿改名）
├── iTween/  Demigiant/           第三方，勿改
└── Resources/Mirror/             后视镜 RenderTexture
```

---

## 3. 硬性约定（违反会直接坏掉）

### 3.1 不要动 `DrivingKernel` 的公开表面
`Assets/Script/Car/kernelControl/DrivingKernel.cs` 是全局耦合点：

- **不要重命名或删除任何 `public` 字段**（`steerAngle`、`motorTorque`、`brakeTorque`、`speed`、`CurrentWheelAngle`…）——
  它们是 serialized field，改名会导致**场景/预制体里的引用静默失效**（Inspector 变 None，不报错但功能没了）。
- **不要改 `MobileTerminal_AcceleratorPedal(bool)` / `MobileTerminal_BrakePedal(bool)` / `Resistance()` / `CancelResistance()` 的签名**——
  UI 按钮的 `OnClick()` 在场景里按名字绑的，签名一改绑定就断。
- 新需求一律**加新方法**，旧方法保留转发。融合 EasyDriving 时就是这么做的（`Hardware_SetSteering` / `Hardware_SetPedals` / `Hardware_ReleaseControl`）。

### 3.2 场景和预制体只能手工编辑
`.unity` / `.prefab` 是 YAML，**不要写脚本批量改**，也不要手工改里面的字段缩进/顺序。
凡是"把脚本挂到物体上、拖引用、打 Tag"这类事（例如 `INTEGRATION_NOTES.md` 第四节），
**Agent 一律输出操作步骤让用户去 Unity 编辑器里做**，不要假装已完成。

### 3.3 别碰 `.meta`
每个资源旁的 `.meta` 持有 GUID。删除或重生成 = 全项目引用断开。**新增文件要连 `.meta` 一起提交**；`Library/` 已在 `.gitignore` 中，不会入库。

### 3.4 分数系统是全局静态的
`Car_Information.DeductScore(int)` 是 `static`，初始 100 分，`MyGameManager` 判定 **< 80 分不合格**。
任何扣分必须走 `DeductScore`，不要直接改字段（`Score` 是 `private static`）。
`Car_Information.isStop`（车辆停止 2 秒）、`Speed` / `SpeedOrigin` 也是 `static`，检测脚本普遍直接读。

### 3.5 输入降级链不能破坏
优先级：**外接方向盘 → 键盘 → UI 方向盘/触摸**。
`HardwareInputController` 每帧决定谁接管，`DrivingKernel.analogInputActive` 是仲裁位。
新增输入源时，必须保证：无设备且无按键时调用 `Hardware_ReleaseControl()` 把控制权交还 UI，
否则 UI 方向盘会彻底失灵。

### 3.6 代码风格
- 注释和 XML doc **用中文**（现有代码如此，保持一致）；新加的 public 方法补 `/// <summary>`。
- 私有字段：新增代码用 `_camelCase`（如 `_steerInput`）；**存量代码不要顺手重命名**，会造成无意义的 diff。
- 现有代码 PascalCase / camelCase 混用（原作者风格），**不要为了统一风格去批量改动**，只保证你新增的部分整洁。
- 手写 UTF-8 with BOM 不是必须的，但**新建 `.cs` 时跟随现有文件的 BOM 风格**（多数文件带 BOM，Unity 都能吃）。
- 不要引入新的 Package Manager 依赖、不要引入新的 asmdef（除非确有必要）。

---

## 4. 常见任务的标准做法

### 4.1 加一个考试项目
1. 复制 `Assets/Script/DetectionRule/RuleScript/Det_DCRK.cs` → `Det_XXX.cs`（它是最完整的状态机范例）。
2. 保持这套骨架：`enum Step` 状态机 + `MidwayParkingTime` 中途停车计时 + `timer` 倒计时 + `AirWall` 空气墙 + `OnDestroy` 清理倒计时文本。
3. 扣分统一走 `Car_Information.DeductScore()`，语音统一走 `VoicePromptAudio.Play_XXX()`（`Play_YaXian_100` 等已存在）。
4. 在 `SelectPass.Sites` 枚举里加一项（注意：`MyGameManager.Start()` 靠 `sites.ToString()` 去 `GameObject.Find()` 找出生点，
   **场景里必须有一个同名 GameObject**，否则 null）。
5. 场景接线（挂脚本、拖 `point_*`、`textTimer`、`AirWall`）**交给用户在编辑器完成**——见 3.2。

### 4.2 改键位 / 加键位
全部在 `HardwareInputController` 的 Inspector 里（`gearPKey`、`turnLeftKey`、`hazardKey`、`keyboardHandbrakeKey` …）。
**改键不用动代码**，先让用户改 Inspector；只有要加全新功能键时才改脚本。

### 4.3 加车辆物理效果
改 `DrivingKernel`。扭矩类写入要留意：**后轮 `motorTorque` 由油门路径写，四个轮 `brakeTorque` 只能由 `ApplyBrakeForces()` 每帧统一写**，
否则会出现踩油门但车不动的"互相覆盖"问题（这是本次融合专门修掉的坑）。

### 4.4 接线 EasyDriving 检测器
`Assets/Script/DetectionRule/EasyDriving/` 下 4 个检测器已就位但**尚未接入场景**（`SidelineDetector` / `DistanceDetector` /
`StopOffDetector` / `WheelStateDetector`）。需要打 Tag（`TagConst.cs` 里定义了名字）并在编辑器里订阅事件。
当前最优价值的一件事：**用 `SidelineDetector` 补上曲线行驶缺失的压线检测**（原项目此处是空白，见 README 的"未完善"）。

---

## 5. 已知坑（前人的经验，别重踩）

| 坑 | 说明 |
|---|---|
| 阿克曼转向符号 | 按 \|转角\| 算出的是正角度，`ApplyAckermannSteering` 里**必须回乘 `Mathf.Sign(steerDeg)`**，否则左右转都往同一边（已修，见 commit `2bdd89e`） |
| 手刹要走 `ChangeState()` | `Car_HandleBrake.ChangeState()` 同时翻转 `Activate` + 同步 UI 图标 + 音效；**直接改 `Activate` 会导致图标和实际状态不一致** |
| 换档动画吞输入 | `GearsDangWei_Control` 用 `Sleep` 延时 + `flag` 防重复，连按档位键时会被动画吞掉；已修为"以最后一次为准"，改这块要回归测试连按 1~4 |
| 暂停时不能加载场景 | `AsyncLoadScene` 强制 `Time.timeScale = 1`。`MyGameManager.GamePause()` 把 timeScale 设 0，**切场景前必须先 `GameContinue()`**，否则进度条卡死 |
| 回放时的错位 | `ReplayEntity` 记录子物体要用 `localPosition`，全记世界坐标回放会错位；回放时应停掉被回放物体上所有控制 Transform 的脚本 |
| 触摸"闪移" | `TPD.cs` / `FPD.cs` 里的 `camera_panel` 标签面板需限制只接收首次触点，且锚点要与面板四角重合 |
| 方向盘轴心偏移 | 部分机型方向盘转动轴心偏离——已修，改 UI 方向盘时注意锚点设在盘心 |
| 空气墙缺失 | 部分项目没有边界空气墙，玩家可绕过项目直达终点（`Wall.cs` 碰墙扣 100 分）。原作者默认"玩家守规则"，不视为必修 |
| 后视镜 RenderTexture | Unity 2022.3 升级后需要重新保存一次（commit `c2445da`）。如果后视镜黑屏，先在编辑器里 resave |

---

## 6. Git 约定

- 分支 `master`。commit message **用英文，祈使句，首字母大写，简短一行**（参考既有 log：
  `Add F key for hazard lights (double flash)`、`Fix Ackermann steering sign bug: left turn was applied as right turn`）。
- 提交前 `git status` 确认没有把 `Library/`、`Logs/`、`UserSettings/` 之类加进来（`.gitignore` 已覆盖，但新增顶层目录要复查）。
- **Agent 不要执行 `git push`**——本机直连 GitHub 不通，push 需要走 SSH + 代理配置。提交完告诉用户"已本地提交，需要推送请说一声"。
- 不要 `--amend` 已有 commit，不要 `--force`。

---

## 7. 改动后的自查清单

改完代码，在回复用户前逐条过一遍：

- [ ] 有没有动 `DrivingKernel` / `Car_Information` 的 public 字段或方法名？（3.1）
- [ ] 有没有需要用户在 Unity 编辑器里接线/打 Tag 的步骤？**明确列出来**（3.2）
- [ ] 新增的 `.meta` 是否一并提交？（3.3）
- [ ] 扣分是否走 `Car_Information.DeductScore()`？新语音是否复用 `VoicePromptAudio` 现有方法？（3.4）
- [ ] 若涉及输入，是否保持了 外接设备 → 键盘 → UI 的降级链？（3.5）
- [ ] 是否引入了新的 Package 依赖？（3.6）
- [ ] 是否诚实说明"编译需在 Unity 中确认"？（第 1 节）
