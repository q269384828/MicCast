# MicCast 开发约定（给 AI 助手）

这个文件记录**已经踩过的坑和硬性约定**。动手前先读完，别重复犯错。

---

## 1. 工程结构：三部分

```
MicCast/
├── harmony/                    ← HarmonyOS 工程（DevEco Studio 打开这个目录）
├── windows/
│   ├── MicCastReceiver.Core/   ← 纯逻辑（NAudio + Rx），WinForms 与 WinUI 共用
│   ├── MicCastReceiver/        ← WinForms 版（保留，可回退）
│   └── MicCastReceiver.WinUI/  ← WinUI 3 版（主力）
├── tools/                      ← 跨平台脚本
└── docs/
```

- **禁止**把 HarmonyOS 的文件放回仓库根目录
- **Core 里不许出现 WinForms / WinUI API**（原来 `Startup.cs` 用了 `Application.ExecutablePath`，
  已换成 `Environment.ProcessPath`）。判断方法：`grep -r "System.Windows.Forms\|Microsoft.UI" windows/MicCastReceiver.Core`
- 构建命令：
  - HarmonyOS：`cd harmony && ./hvigorw assembleHap`
  - WinForms：`cd windows/MicCastReceiver && dotnet build -c Release`
  - WinUI：`cd windows/MicCastReceiver.WinUI && dotnet build -c Release -p:Platform=x64`
  - 安装包：`.\tools\build-installer.ps1`（见第 2c 节）
- **要启动/调试就用 `.\run.cmd`**（= `tools/run.ps1`：增量构建 + 启动），别手动 cd 进
  `bin\x64\Release\net9.0-windows10.0.19041.0\win-x64\` 找 exe。带参数会透传给程序。
  产物路径是固定的；`run.ps1` 里 `$outDir` 是唯一事实来源，改 TFM 时同步改它
- ps1 里写中文必须存成 **UTF-8 带 BOM**，否则 Windows PowerShell 5.1 按 ANSI 读会乱码，
  报 `Missing closing '}'` 之类看起来无关的语法错

## 1b. WinUI 3 的几个硬坑

| 坑 | 现象 | 修法 |
|---|---|---|
| `dotnet build` 报 `MSB4062 未能加载 Microsoft.Build.Packaging.Pri.Tasks.dll` | 少了 PRI 任务，且**产物没有 `.pri`**，程序启动即以 `0xc000027b` 崩 | 确保引用了 `Microsoft.Windows.SDK.BuildTools.MSIX` 这个 NuGet 包（已在 csproj 里）。它提供 PRI 任务，不必装 VS。删掉它就只能回退到 VS 的 MSBuild |
| `AppWindow.Resize` | 窗口变大了但内容仍按旧尺寸排版，右侧一片空白 | 用 Win32 `SetWindowPos`（走正常 WM_SIZE 路径） |
| `GetDpiForWindow` | 窗口渲染前返回 96，150% 缩放屏上 900 被当成物理像素 | 尺寸要在 `RootGrid.Loaded` 里算 |
| `MaxWidth` + `HorizontalAlignment="Center"` | 按「内容自然宽度」测量，卡片反而被缩窄 | 外面套一层撑满的 `Grid`，内层再 MaxWidth + Center |
| 资源查找 | `RootGrid.Resources[key]` 抛 KeyNotFoundException，崩在 XAML 层（`0xc000027b`） | 样式在 `Application.Resources`，先查根再退回 `Application.Current.Resources` |
| csproj XML 注释 | 写 `--status` 这种词会报 `MSB4025 注释中不能包含 --` | 注释里把参数名写成 `status` |
| `Windows.Win32` PInvoke | 需要 CsWin32 源生成器，多一层依赖 | 用纯 `[DllImport]` |
| `Border.CornerRadius="999"` | 画成**纺锤形/杏仁形**（四段圆弧重叠），不是圆角胶囊 | WinUI 不钳制半径。固定高度 + 半径取半高（26 高 → 13）。WinForms 的 `AddArc` 会自动收敛，从 WinForms 照搬 999 必错 |
| Rx 日志/状态订阅 | 后台线程直接改 XAML 控件抛跨线程异常，表现为**日志区一直空着** | `.ObserveOn(new DispatcherQueueSynchronizationContext(DispatcherQueue))` |
| 容器内文字偏上 | 只设外层 `Padding` 不够，子元素撑满高度后顶部对齐 | 垂直居中要在**模板**里用 `ContentPresenter VerticalAlignment=Center` 保证 |
| 设置项每行手写布局 | 复制粘贴导致图标/文字/间距每次差一点，相关项看不出是一组 | 用 `Controls/SettingRow` + `SettingGroup`，布局与分组在模板里定义；分组判据见 `docs/design-system.md` 第 9 节 |
| `AllocConsole` | 输出被重定向时把管道句柄弄乱，命令挂住不返回 | 用 `AttachConsole(ATTACH_PARENT_PROCESS)` |
| 限制窗口最大/最小尺寸 | `OverlappedPresenter.PreferredMaximumWidth` 只挡用户拖拽边框，**程序化 `SetWindowPos` 会绕过去**（实测拉 1800 就是 1800） | 子类化 WndProc 处理 `WM_GETMINMAXINFO`，改 `ptMinTrackSize` / `ptMaxTrackSize`。拖拽、最大化、`SetWindowPos` 全部走这里，一处生效 |

**托盘必须照 PowerToys 的 `TrayIconService` 做**，不要自建消息窗口：

1. 建**专用且永不激活**的 WinUI `Window` 只为拿 HWND（主窗口关掉重开不影响托盘）
2. `SetWindowLongPtr(hwnd, GWLP_WNDPROC, proc)` 子类化它的 WndProc
3. **委托必须存字段**（PowerToys 原文：否则 `WndProc will explode`）
4. `RegisterWindowMessage("TaskbarCreated")` 处理 explorer 重启
5. NIM_ADD 开机自启时可能因 explorer 未就绪失败 ——
   用 `WM_WINDOWPOSCHANGING` 做重试钩子（那时收不到 TaskbarCreated）
6. WndProc 末尾必须 `CallWindowProc(原proc, ...)` 链回去

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

## 2b. 打包 Windows 安装程序

```powershell
# 前提：Inno Setup 6（winget install JRSoftware.InnoSetup）
.\tools\build-installer.ps1                  # 版本默认 1.0.0
.\tools\build-installer.ps1 -Version 1.0.1
# 产物：dist\MicCast-Setup-<版本>-win-x64.exe
```

脚本做三件事：`dotnet publish` 自包含产物 → 校验 `.pri` 存在 → 调 `windows/installer/MicCast.iss` 打包。

安装器约定（改 `MicCast.iss` 时别破坏）：

- `PrivilegesRequired=lowest`，装到 `%LOCALAPPDATA%\Programs\MicCast`。程序把 `last-ip.txt` /
  `winui-crash.log` 写在自身目录，装 Program Files 会因无权限静默失败
- 必须把 `windows/Set-DefaultMic.ps1` 一起装进 `{app}` —— 托盘菜单从程序目录往上找这个脚本
- VB-CABLE 不入包（第三方 donationware），首次运行由程序引导下载

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

## 6b. `MicServer` 只容许一个客户端，但 `onClientConnected` 会被调多次

PC 端重启/重连时，**新连接先建立、旧连接稍后才真正断开**，于是短时间内有两个活连接。
原来每个回调都无条件改共享状态，造成一串连锁问题：

| 症状 | 原因 |
|---|---|
| 「界面上写着拿不到电脑端地址，但其实可以正常使用」 | 旧连接的 `getRemoteAddress` 重试失败，`catch` 把 `lastError` 和 `state=Listening` 写进了**共享**字段，而新连接明明成功 |
| 重连后好一阵没声音 | 旧连接的 `close` 无条件 `this.conn = undefined` 并调 `disconnectHandler()`，把新连接也一起清掉、停采集 |
| 偶发解析出垃圾帧 | 两个连接的 `on('message')` 都往同一个 `this.rx` 里塞数据，两路 TCP 字节流交错 |

**修法：所有回调先认领「自己还是不是当前连接」。**

```typescript
conn.on('message', (info) => {
  if (this.conn !== conn) return;          // 旧连接的数据不进 rx
  this.onData(new Uint8Array(info.message));
});
conn.on('close', () => {
  if (this.conn !== conn) return;          // 旧连接不许清新连接
  ...
});
// getRemoteAddress 的 catch 里同样判断
```

**另外 `attachClient` 里必须 `this.lastError = ''`** —— 报错是「上一次连接」的事，
不清掉就会变成「明明能用却一直挂着错误提示」。

日志里的判别特征（同一毫秒既有失败又有成功）：

```
00:53:29.129  getRemoteAddress failed, retry after 200ms
00:53:29.129  client ip = 192.168.3.95      <- 另一条连接的 then
00:53:29.339  getRemoteAddress retry failed
```

## 6c. ArkUI 里 `width('100%')` 加 `margin` 不会收缩，会溢出被裁
```typescript
Column() { /* 渐变 Hero */ }
  .width('100%')                                        // 宽度 = 父容器
  .margin({ left: 20, right: 20 })                       // 再往外推 20
  .borderRadius(22)
```

**结果不是"左右各留 20"，而是宽度撑满父容器、再被 margin 推出去，
两边直接被屏幕边缘裁掉。** 大圆角和渐变让它看起来像"故意出血"，很容易漏掉 ——
这次就是"手机顶部 Hero 左右贴边"的实际原因，而不是边距给小了。

三种正确写法，按场合选：

| 写法 | 说明 |
|---|---|
| `.width(\`calc(100% - ${Tokens.pagePadding * 2}vp)\`)` + margin | 改动最小，宽度自己算 |
| 外面包一层 `Column().width('100%').padding({left,right})` | 结构最清晰，但要多一层 |
| 父容器用 `padding` 而不是 `margin` | Scroll 内的卡片一直是这么做的，所以没出问题 |

**自检方法**：同一屏里如果有的块用 `padding`、有的用 `margin`，一定要比对两者的左边缘是否对齐。
这次 Hero 用 margin（错）+ 卡片用 padding（对），截图里 Hero 左边缘比卡片靠外约 50px，一眼能看出来。

## 6d. 长时任务的「运行态一致性校验」—— 后台保活的核心约束

官方文档明确：**系统会校验应用是否真在执行所声明的任务类型。声明了 `AUDIO_RECORDING`
却没有真正在录音，系统会取消长时任务并冻结进程。**

这一条直接决定架构，踩过两次**相反方向**的坑：

| 做法 | 结果 |
|---|---|
| PC 断开 → 停采集 + **停**长时任务 | 切后台立刻被冻结，重连时 `sender.start()` 起不来 |
| PC 断开 → 停采集 + **保留**长时任务 | 系统查出「声明录音却没录音」→ 取消任务 + 冻结。<br>表现同样是 `connected=True` 但 `fps=0` |
| **PC 断开 → 只停推流，采集继续 + 保留长时任务** | 正确。任务始终合法，后台不被冻结，重连秒恢复 |

**所以「麦克风服务开着」必须等价于「正在采集」，不能等价于「正在推流」。**
`AudioSender` 因此拆成两层状态：

- `start()` 启动采集（不推流）—— 服务启动时调用
- `attach(ip)` 挂推流目标 / `detach()` 只停推流 —— 采集器始终不重建
- `stop()` 全停 —— 只在用户点「停止」时调用

日志里的正确行为：

```
[bg]    background long task started (audioRecording)   <- 先申请任务
[audio] attach -> 192.168.3.105:59100 (capturer kept alive)
[audio] detach (capturer kept alive)                     <- 断开，采集不停
[audio] attach -> 192.168.3.105:59100 (capturer kept alive)  <- 重连
```

**另外：服务对象必须是模块级单例，不能挂在页面 struct 里。**
`aboutToDisappear()` 是页面销毁（不是用户点停止），在里面 `stopAll()` 会把服务一起带走。
现在 `micServer` / `micSender` / `micGuard` 都提在模块作用域，页面来去不影响服务。

**关于「拆进程保活」—— 不行，别试：**

- `ServiceExtensionAbility` 是**系统应用专属**，三方应用声明不了
- `module.json5` 的 `process` 字段能让 Ability 跑在独立进程，但**冻结与否看的是有没有
  合法长时任务，不是看在哪个进程**。拆出去照样冻
- 唯一管用的保活机制就是「长时任务 + 业务与声明一致」

## 7. 往手机传文件：App 读不到 `/data/local/tmp`


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

## 8f. WinForms 自绘容器 + 常驻子控件是雷区

给「手动 IP」做一个圆角输入框，试了 **8 种方案全部失败**：

| 方案 | 结果 |
|---|---|
| `Control` + `UserPaint` + 子 TextBox | 子控件底块在、光标在、`Text` 有值，**文字不可见** |
| `Panel` + `BackColor = Transparent` | 同上 |
| `Panel` + 不透明底 + `Region` 裁圆角 | 同上 |
| `SetBounds` / `Dock.Fill` + `Padding` | 同上（诊断打印出 `inner=309x25@(18,13)` 属性全对，就是渲染不对）|
| `OnResize` / `OnLayout` 触发同步 | 同上 |
| `BringToFront()` | 同上 |
| `AutoSize = false` | 同上 |
| `UserControl` 基类 | 同上 |
| **标准 `TextBox`（方角）** | **立刻正常** |

**结论：WinForms 里凡是"自绘容器 + 常驻子控件"的组合都会踩这个渲染坑。**
纯自绘控件（没有子控件）完全没事 —— `ModernButton` / `Slider` / `LevelMeter` / `Hero` / `StatusPill` 都工作良好。
同目录的 `DropDown` 能用，也是因为它自己画文字，**没有常驻子控件**（弹出列表时才建独立窗口）。

**规则：**
- 需要**输入能力**的控件，直接用标准控件（`TextBox` / `ComboBox`），不要自绘包装
- 需要**展示**的控件，可以放心自绘
- 想同时要「圆角 + 输入」，Round 1 的解法是把标准控件放进**卡片**（圆角由卡片承载），而不是给控件本身套自绘外壳

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
