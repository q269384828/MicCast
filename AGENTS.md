# MicCast 开发约定（给 AI 助手）

这个文件记录**已经踩过的坑和硬性约定**。动手前先读完，别重复犯错。

---

## 1. 工程结构：两个平台完全独立

```
MicCast/
├── harmony/     ← HarmonyOS 工程（DevEco Studio 打开这个目录，不是仓库根）
├── windows/     ← WinForms 接收端（VS / dotnet 打开这个目录）
├── tools/       ← 跨平台脚本
└── docs/
```

- **禁止**把 HarmonyOS 的文件放回仓库根目录
- 构建命令各自在子目录执行：
  - HarmonyOS：`cd harmony && ./hvigorw assembleHap`
  - Windows：`cd windows/MicCastReceiver && dotnet build -c Release`

## 2. 构建 HarmonyOS HAP：签名必须手动带上

`harmony/build-profile.json5` **不含签名配置**（模板文件，入库）。真实的签名在：

```
harmony/build-profile.local.json5      # 被 .gitignore 忽略，本地存在
```

**直接 `hvigorw assembleHap` 出来的 HAP 是未签名的，`hdc install` 会报 `no signature file`。**

正确做法：

```powershell
cd harmony
Copy-Item 'build-profile.json5' 'build-profile.json5.bak' -Force
Copy-Item 'build-profile.local.json5' 'build-profile.json5' -Force
$env:DEVECO_SDK_HOME = 'D:\Huawei\DevEco Studio 26.0.1.290\sdk'
$env:JAVA_HOME = 'D:\Huawei\DevEco Studio 26.0.1.290\jbr'
& .\hvigorw.bat assembleHap --mode module -p product=default -p buildMode=debug --no-daemon
Move-Item 'build-profile.json5.bak' 'build-profile.json5' -Force
# 产物：entry\build\default\outputs\default\entry-default-signed.hap
```

**签名密码被 DevEco 用 `.ohos/config/material/` 里的密钥加密，命令行还原不出来。** 不要试图用 `keytool` 或 `hap-sign-tool` 猜密码，浪费时间。

## 3. 设备自动化：禁止硬编码坐标

`hdc shell uitest` **只有坐标点击，没有选择器**：

```
click/doubleClick/longClick <x> <y>
text <text>              # 只能往已聚焦位置输入
```

`@ohos.uitest` 的 `ON.text()` / `ON.id()` 存在，但需要编译独立测试 HAP 才能用，`hdc shell` 调不到。

### 必须遵守的点击流程

```
1. uitest dumpLayout -p /data/local/tmp/x.json
2. 从 JSON 里按 text/id 找到目标节点的 bounds，算中心点
3. 立刻点击（不要缓存坐标、不要复用上一次 dump 的结果）
4. 立刻再 dump 一次，验证目标状态真的变了
5. 没变 → 重试或报告失败，不要假设成功
```

### 为什么坐标会变（实测见过 y=1156 / 1207 / 1303 三个值）

| 触发因素 | 影响 |
|---|---|
| 运行日志面板展开 / 收起 | 下方内容整体位移 ±150px |
| 提示文案行数变化（1 行 ↔ 3 行） | 卡片高度变，下面跟着移 |
| 服务状态切换（未启动 / 正在串流） | Hero 区高度不同 |

**曾经因为硬编码 `(564, 1306)` 连续多次点在空白处，导致「以为切了模式其实没切」，后续一整套测试数据全部作废。**

## 4. 乱码：dump 出来的中文不能靠猜

`hdc shell` 输出的中文在 PowerShell 里是 GBK/UTF-8 错位（`ԭʼ` = 原始，`������ǿ` = 语音增强）。**不要靠肉眼猜含义就下结论。**

可靠做法：
- 用**截图**确认 UI 状态：`hdc shell snapshot_display -f /data/local/tmp/x.jpeg` → `hdc file recv` → 看图
- 或者用 Python 以 UTF-8 解析 `dumpLayout` 的 JSON，只输出**英文标记 + 坐标**

## 5. 音频链路测试：两个必踩的假阴性

**测 Win+H / ASR 识别率时，以下两种音源都无效：**

1. **被测设备自己播放音频喂自己的麦克风** —— 带 AEC 的处理链会把测试音当回声消掉
2. **PC 的 TTS 从扬声器播放** —— `TextInputHost`（Win+H 宿主）加载了 `xaudio2_9.dll`（纯播放 API），会拿系统渲染流做 AEC 参考，把电脑自己放出来的声音判成回声

**正确做法：用另一台设备（另一部手机 / 音箱）循环播放。**

排查 Win+H 是否在做 AEC：

```powershell
Get-Process TextInputHost | Select-Object -ExpandProperty Modules |
  Where-Object ModuleName -match 'xaudio2|audio'
```

## 6. HarmonyOS SourceType 的坑

**`SOURCE_TYPE_VOICE_COMMUNICATION` 是 VoIP 专用，禁止用于本项目的录音场景。**

未建立通话音频场景时它**持续返回全零数据**。表象极具迷惑性：采集器正常运行、帧率 207/s、`sendErr=0`、`readData` 持续回调，但麦克风输入一直是静音。

华为官方「录音无声排查」文档点名了这个误用。当前只用两档：

| 模式 | SourceType |
|---|---|
| 原始 | `SOURCE_TYPE_UNPROCESSED`（API 14+，不支持时回退 `MIC`） |
| 识别优先 | `SOURCE_TYPE_VOICE_RECOGNITION` |

**长时任务**：`BackgroundTaskGuard` 申请 `AUDIO_RECORDING`，必须在**前台**启动录音后再申请。实测息屏 25 秒帧率稳定 196→202，丢包 0.00%，无断流。

## 7. 往手机传文件：App 读不到 `/data/local/tmp`

`hdc file send` 到 `/data/local/tmp` 只有 shell 可读，媒体 App 拿不到内容（表现为能显示文件名，但 `totaltime` 恒为 `00:00`、点播放报「未找到文件」）。

要让播放器真正加载：

```
# 1. 推到 file_manager 组可写的公共目录
hdc file send <local> /storage/media/100/local/files/Docs/Download/<name>

# 2. 用 docs URI 打开（不是 file:///storage/...）
hdc shell aa start -b com.huawei.hmsapp.music -a MainAbility \
  -U file://docs/storage/Users/currentUser/Download/<name> -t audio/mpeg
```

**hdc 推入的文件不会进 HarmonyOS 媒体库索引**（`mediatool query` 返回 0 结果），但上面的 docs URI 能让播放器直接读。

另外 `hdc shell` 用户在 `file_manager` 组（`uid=2000(shell) groups=...1006(file_manager)...`），但 `chmod` 改不动文件权限（非 owner）。

## 8. 上下文隔离：不要用模拟按键测 Win+H

`keybd_event` 模拟 Win+H 面板能弹出，但**焦点窗口不可控**，识别结果插到哪儿无法保证。需要在文本框里接收结果时，用一个独立的 WinForms 窗口 + `TextChanged` 实时写文件，别指望记事本（Win11 记事本多标签，`SetForegroundWindow` 到旧句柄会切错标签）。

另外这个环境的 `Stop-Process` 有包装限制，对动态 PID 会报 `must specify at least one process using -Id`。**改用 `$proc.Kill()`。**

## 8b. Windows 端 UI 自动化（自绘控件）

这个项目的按钮是 `ModernButton`（继承 `Control` 自绘），**不是标准 `Button`**，UIA 表现如下：

| 控件 | UIA ControlType | 支持的模式 |
|---|---|---|
| `ModernButton` | `Pane` | **只有点击坐标**，没有 InvokePattern |
| `TrackBar` | `Slider` | **不支持 RangeValuePattern** |
| `Label` | `Text` | 只读 |

**正确做法：**

```powershell
# 1. 按名字找元素，拿最新 BoundingRectangle（不要缓存！）
$btn = $all | Where-Object { $_.Current.Name -match '停止接收' } | Select-Object -First 1
$r = $btn.Current.BoundingRectangle
# 2. 算中心，SetForegroundWindow 后 mouse_event 点击
```

**两个坑：**

1. **按钮文字一变，宽度就变** —— 「开始接收」→「停止接收」会让 `rect.X` 跳动。
   每次操作前必须重新查 `BoundingRectangle`，不能复用上一次的坐标。
2. **自绘 `Ui/Slider` 没有 `RangeValuePattern`**（它是个裸 `Control`，UIA 只看到 Pane）。
   要设增益只能按坐标点轨道位置——和 `TrackBar` 不同，滑块是**双向的**，
   中点 = 0 dB，左右端分别是 −12 / +12，点哪个位置就是哪个值（1:1 线性映射，无偏）。
   需要精确值时按窗口宽度算 `sliderX + width * (value + 12) / 24`。

**Windows 端窗口坐标是稳定的**（不像手机 UI 会因面板折叠而位移），
所以 UIA 拿到的 `BoundingRectangle` 可以直接算中心点。

## 8c. 命名管道客户端会阻塞

给宿主发命令时，`NamedPipeClientStream` 用 `PipeDirection::Out` 单工，
**不要读**（`StreamReader.Peek()` 在无数据时会阻塞，导致整个命令挂死）。
只需要写命令时：

```powershell
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'MicCastReceiver.Backend',
    [System.IO.Pipes.PipeDirection]::Out)
$pipe.Connect(3000)
$writer = New-Object System.IO.StreamWriter($pipe); $writer.AutoFlush = $true
$writer.WriteLine('{"cmd":"gain","gainDb":6}')
```

验证结果用 `MicCastReceiver.exe --status`，别在管道里读。

## 8d. JitterBuffer 的预填充必须和初始 target 对齐

冷启动时"播放器已经在读、但手机端还没出第一帧"会一路欠载，
实测能积 1.1~1.4 秒，还把自适应水位顶到上限，听起来像"每几秒咔一下"。两条独立原因：

1. **预填充水位 ≠ 初始 target** —— `PrimeSilence(78)` 之后 `target` 还是 `minTarget(28)`，
   播放器一开就发现缓冲远超目标，按最大速率（+6%）猛吃，1 秒内吃光再欠载。
   修法：`PrimeSilence(ms)` 之后调 `SetTarget(ms)` 对齐起算点。
2. **起播时机太早** —— `ProtoClientConfig` 到达就 `Play()`，但手机端 `createAudioCapturer`
   还要几百毫秒才出第一帧。
   修法：`Start()` 只 `Init()` + prime，**推迟到 `Write()` 收到首个真实音频包才 `Play()`**。

另外 target 回落速度要慢（每 5 个包降 1ms ≈ 40ms/s），降太快会一头扎回欠载区。

## 8e. 水位下限必须按 WasapiOut 的拉取粒度算，不能拍脑袋

**这是 3.7ms/s 地板的真凶**，也是本项目最容易误判成"网络抖动"的问题。

`--status` 里的诊断字段（`readFrames` / `availAtUnderrun`）一出来就清楚了：

```
readFrames=418          # WasapiOut 单次 Read 要 418 帧 = 26ms
availAtUnderrun=0       # 欠载时缓冲里真的是 0，不是"差一点点"
buf=56ms target=62ms    # 看着有 56ms，还在欠载
```

**`WasapiOut` 起播时要连着读几次把 render buffer 填满**，
所以水位必须覆盖**两次完整拉取**（2×26 = 52ms）。而 `balanced` 的 target 在 42~65 之间晃，
正好卡在边界上，隔一秒就撞一次 —— 表现为稳定的 3.7~4.2 ms/s。

修法：第一次 `Read` 时按实测 `outFrames` 校准水位下限

```csharp
int needed = (int)Math.Ceiling(outFrames * 2 / (double)_framesPerMs) + 10;
if (needed > _minTargetMs && needed <= _maxTargetMs) { _minTargetMs = needed; ... }
```

本机算出 63ms，欠载从 4.24 降到 **0.10 ms/s**。

**三条印证这个解释的证据：**
- `low` 档（buf 36ms < 52ms）差到 **37 ms/s**
- `balanced`（51ms）和 `stable`（93ms）都卡在 **3.7 左右** —— 前者刚好在边界，后者
  的 `wasapiLatency=40` 让 `readFrames` 更大，2× 后同样在边界
- `availAtUnderrun=0` 说明不是"水位差几毫秒"，是真空了

**教训：欠载排查必须先看"单次要多少"再看"手上剩多少"，光看 buf vs target 会得出错误结论。**

**判断欠载健康度看速率不看总量**：`underrunMs` 是累计值只增不减，
刚启动那几十秒的欠载会永远留在计数里。换算成 `ms/s`（5 秒滑动窗口）才有意义，
稳态应该 < 2 —— 界面上显示的也是这个。

## 9. 构建缓存不要入库

- 根目录和 `harmony/` 下的 `.hvigor/` 都是构建缓存，必须 gitignore
- `git rm --cached` 前**先确认 .gitignore 已包含该路径**，否则 `git add -A` 会把它加回来
- 提交前用 `git ls-files | Select-String hvigor` 检查

## 10. `record-cable.ps1` 会忽略 -OutputPath

脚本实际输出到 `$env:TEMP\miccast-<HHmmss>.wav`，`-OutputPath` 参数当前不生效。要拿录音文件时，去 TEMP 找最新的 `miccast-*.wav`。

---

## 验证优先于猜测

这个项目已经因为「不验证就下结论」浪费过多轮时间：

- 用 TTS 测 Win+H → 错误结论「引擎缺失」（实际引擎完好，`zh-CN-N` 模型在 `C:\Windows\Speech_OneCore\Engines\SR\`）
- 硬编码坐标点空 → 错误结论「语音增强能识别」
- 猜乱码含义 → 错误结论「切到了语音增强」
- 看到启动期 `underrun=1445ms` → 差点误判「改成 target 42 反而更差」，
  实际是累计值里含预热期，稳态采样（60 秒增量）才是 0

**任何关于设备状态、UI 状态、音频电平的判断，都要有可复现的证据（截图 / 日志 / 电平测量），不要靠推理链。**
