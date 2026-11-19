# MicCast · 声渡

把鸿蒙手机当作 Windows 电脑的麦克风。

> 虚拟声卡推荐 [VB-CABLE](https://vb-audio.com/Cable/)（VB-Audio 出品，donationware，本项目不附带其安装包）。
> 许可：[MIT](LICENSE)

```
鸿蒙手机（服务端）                         Windows（MicCast Receiver）
┌──────────────────────────┐             ┌─────────────────────────────────┐
│ TCP  59100  监听/握手     │ ◄────────── │ 连过来握手                      │
│ UDP  59200  广播自身      │ ──────────► │ 自动发现，列表里直接选           │
│ UDP  59100  推 PCM        │ ──────────► │ 收 StreamData → NAudio          │
│ AudioCapturer 48k/2ch     │             │   ↓ WasapiOut                   │
│ 每 2s 发 PingRequest      │             │ CABLE Input                     │
└──────────────────────────┘             └─────────────────────────────────┘
                                                         ↓ ══ VB-CABLE ══
                                                        CABLE Output
                                                         ↓
                                        微信 / 腾讯会议 / Discord / Win+H 选它当麦克风
```

- **包名**：`com.local.miccast`
- **服务端广播名**：`<机型> (MicCast)`，例如 `P60 (MicCast)`
- **线缆协议**：字段号沿用 AudioRelay 0.27.5 的 protobuf schema（从本机 `Model.dll` 反射核对）。
  之所以还沿用这套格式，是因为手机端与接收端都在本仓库内、协议本身够用，
  改动它只会带来无谓的联调成本 —— 但**官方 App 收不了我们推的流**（见下方「协议设计上踩过的三个坑」）。

## 端到端实测

| 环节 | 证据 |
|---|---|
| 手机启动服务 | `tcp listening 0.0.0.0:59100` + `discovery broadcast started (udp/59200)` |
| **自动发现** | `发现设备: <机型> (MicCast) @ 192.168.1.23` |
| 手机推流 | **200 帧/秒**（5ms/帧），`sentOk` 持续增长、`sendErr=0` |
| 接收端收流 | `fps=200 kbps=1533 buffered=75ms device=CABLE Input (VB-Audio Virtual Cable)` |
| **Windows 拿到真麦克风** | 从 `CABLE Output` 录 30s：`mean_volume=-33.8 dB` / `max_volume=-2.5 dB` |
| 对比：某些 App 自带的 Virtual Mic | −91 dB（纯静音），不可用 |

真机：HarmonyOS 手机（API 26 / OpenHarmony 7.0），Windows 11 + VB-CABLE 3.5.2.0。

## 手机端

### 界面

- **顶部渐变 Hero**：应用名 + 副标题 + 状态胶囊（`未启动` / `等待电脑连接` / `正在串流`）
- 串流时 Hero 变成唯一的实时状态区：
  - **麦克风电平条**（把采集到的 PCM 直接算 RMS 再转 dB），一眼能看出手机到底有没有在收音
  - 一行三个**速率类**指标：已串流时长 / 帧/秒 / kbps
    —— 刻意不显示"累计帧数"这种只增不减的计数器，对用户没有意义还会造成焦虑
- 主操作卡片：说明文字随状态变化 + 一个圆角主按钮
- 「电脑上怎么用」四步指引，带编号圆标，可折叠
- 运行日志默认**收起**，只有展开时才刷新内容
- **动效**：电平条跟随有缓动、开始/停止按钮颜色过渡 + 按下缩放回弹、Hero 实时区滑入淡出、
  指引与日志展开用 `animateTo` 平滑过渡
- 深浅色跟随系统：`resources/base/element/color.json`（浅色）与 `resources/dark/element/color.json`（深色）两套配色，`EntryAbility` 里 `setColorMode(COLOR_MODE_NOT_SET)`

### 行为

- 点「启动麦克风服务」→ 监听 TCP 59100 + 每 2 秒广播发现包
- 电脑端连上 → 自动申请麦克风权限、启动采集、申请 `audioRecording` 长时任务
- **电脑端断开 → 自动停掉采集与长时任务**，状态回到「等待电脑连接」
- 采集失败会回落到单声道再复制成双声道，保证对端始终按 2ch 解析

### 构建 / 装机

前置：`devecocli`（`pnpm add -g --config.node-linker=hoisted @deveco/deveco-cli`）或直接用 DevEco Studio。

```powershell
# 按自己的安装位置调整
$env:PATH = '<DevEco Studio>\tools\node;<pnpm 目录>;' + $env:PATH
$env:DEVECO_HOME = '<DevEco Studio>'

devecocli auth login --no-prompt
devecocli signature generate --type debug    # 首次或改了 bundleName 后执行，会把 signingConfigs 写进 build-profile.json5
devecocli build --modules entry
devecocli run --skip-build --module entry --device <手机IP:端口> --ability EntryAbility
```

`<手机IP:端口>` 由 `hdc tconn <手机IP>:<端口>` 建立，端口在手机的「开发者选项 → 无线调试」里。

> 仓库里的 `build-profile.json5` 是**不含签名信息**的模板；`signature generate` 会自动补上
> `signingConfigs`，证书默认落在 `~/.ohos/config/`。

## 电脑端

见 [windows/README.md](windows/README.md)。要点：

1. 界面为自绘现代风格（深浅色跟随系统，Windows 11 圆角 + 深色标题栏）
2. **图形界面单例**：重复启动不会开第二个窗口，只会把已有窗口切到前台（`--help` / `--discover` / `--list-devices` 等控制台模式不受限制）
3. **缺少 VB-CABLE 时顶部会有引导卡片，可一键下载并静默安装**
4. 运行 `MicCastReceiver.exe`，手机列表自动出现，选中 → 输出设备选 `CABLE Input` → 开始接收
5. 会议软件 / Win+H 的麦克风选 `CABLE Output`，或 `Set-DefaultMic.ps1 -Name 'CABLE Output'` 设为默认

## 目录结构

```
MicCast/
├── AppScope/app.json5                    # bundleName: com.local.miccast
├── entry/src/main/
│   ├── ets/
│   │   ├── model/
│   │   │   ├── Pb.ets                    # 手写 protobuf 编解码（无三方依赖）
│   │   │   └── Messages.ets              # 协议消息定义与构造函数
│   │   ├── service/
│   │   │   ├── MicServer.ets             # TCP 59100 服务端 + 握手 + 2s 心跳 + 断开回调
│   │   │   ├── AudioSender.ets           # AudioCapturer → 5ms/960B 帧 → UDP 59100
│   │   │   ├── BackgroundTaskGuard.ets   # audioRecording 长时任务
│   │   │   └── Logger.ets
│   │   ├── pages/Index.ets               # UI
│   │   └── entryability/EntryAbility.ets
│   └── resources/
│       ├── base/element/color.json       # 浅色配色
│       └── dark/element/color.json       # 深色配色
├── windows/
│   ├── MicCastReceiver/
│   │   ├── Discovery.cs                  # UDP 59200 发现监听
│   │   ├── Receiver.cs                   # TCP 握手 + UDP 收流
│   │   ├── Protocol.cs                   # 协议常量 / 消息构造 / 发现包解析
│   │   ├── AudioPlayer.cs                # NAudio WasapiOut
│   │   ├── VbCable.cs                    # VB-CABLE 检测 + 一键下载安装
│   │   ├── SingleInstance.cs             # 图形界面单例（命名互斥量 + 激活已有窗口）
│   │   ├── MainForm.cs                   # 主界面与布局
│   │   ├── Program.cs                    # 命令行
│   │   └── Ui/                           # 自绘控件（零第三方 UI 库）
│   │       ├── Theme.cs                  # 深浅色配色 + 圆角绘制
│   │       ├── Card.cs / ModernButton.cs / DropDown.cs
│   │       ├── TextField.cs / StatusPill.cs / LevelMeter.cs
│   │       └── NativeFx.cs               # Win11 圆角 + 深色标题栏
│   ├── Set-DefaultMic.ps1                # 切换系统默认录音设备
│   ├── vbcable/                          # VB-CABLE Pack46（本地留存）
│   └── README.md
└── tools/
    ├── player-test.js                    # 纯 Node 模拟客户端连手机服务端，算 RMS
    ├── lan-scan.js                       # 扫描局域网里的 hdc 调试端口
    └── install.ps1                       # 构建 + 装机 + 抓日志
```

## 协议要点

| 通道 | 端口 | 内容 |
|---|---|---|
| 发现 | UDP 59200 广播 | `[int32 LE 长度][ProtoServerConfig]` |
| 控制 | TCP 59100 | varint 长度前缀的 delimited protobuf |
| 音频 | UDP 59100 | 每个数据报 = 裸 `StreamData`（无信封） |

**服务端握手顺序**：TCP accept → 发 `ProtoServerConfig` → 收 `ProtoClientConfig` → 发 `ProtoFeatures` → 推流。

### 协议设计上踩过的三个坑

前两条是为对接官方桌面端时发现的，现在虽然不再用它，但约束依然成立：

1. **帧长必须 ≤ MTU**：20ms/3840 字节 → 数据报 3856 → IP 分片，任一分片丢失整帧报废。
   改 5ms/960 字节（整包 ~985 字节）后彻底避免分片。这对**任何** UDP 接收端都是收益。
2. **双方各绑本地 59100**：服务端也从 UDP 59100 发出，接收端按源端口过滤。
3. **服务端每 2s 发一次 `PingRequest`**：当初是因为官方桌面端读消息有 10s 超时。
   自研接收端没有超时逻辑，这一条现在只是保留着方便测链路存活，不是硬性要求。

## 已知限制

- **必须装虚拟声卡**：VB-CABLE 是必需前置，Windows 没有内置虚拟麦克风。
- **纯局域网**：协议无中继 / NAT 穿透，手机与电脑必须互通（同一 Wi-Fi 或有路由的子网）。
- **只做手机→PC**：PC→手机方向需要 Opus 解码，未实现。
- **PCM 不压缩**：48kHz/16bit/2ch ≈ 1.5 Mbps，Wi-Fi 无压力，走移动网络会吃流量。
- **不能开机静默常驻**：鸿蒙要求录音必须前台启动，启动后立即申请长时任务。
- **手机端必须保持前台可见**：切后台靠 `audioRecording` 长时任务维持，被系统清理则需重新打开。

## 排错

| 现象 | 处理 |
|---|---|
| 电脑端列表一直空 | 手机是否已点「启动麦克风服务」；两端是否同一 Wi-Fi；端口 59200 是否被其他程序占用 |
| 帧数上涨但没声音 | 接收端播放设备要选 `CABLE Input` |
| 会议软件/ Win+H 没声音 | 麦克风选 `CABLE Output`；只认默认设备的软件跑 `Set-DefaultMic.ps1 -Name 'CABLE Output'` |
| 手机显示「正在串流」但电脑没声 | 检查电脑端是否还活着；手机在电脑断开后会自动停采集 |
| CLI 看不到设备 | Windows 有两套 hdc：PATH 里的 `HdcExternal.exe`(5037) 与 SDK 的 `hdc.exe`(8710)，设备列表互不可见，用 SDK 那个 |

## 验证脚本

```powershell
# 模拟播放器连手机服务端，收 UDP 并算 RMS
node tools/player-test.js --ip <手机IP> --seconds 6

# 无界面扫描手机
windows\MicCastReceiver\bin\Release\net9.0-windows\win-x64\publish\MicCastReceiver.exe --discover 6

# 真·麦克风链路自检：接收端播到 CABLE Input，同时从 CABLE Output 录音测电平
ffmpeg -f dshow -i audio="CABLE Output (VB-Audio Virtual Cable)" -t 10 -y cable.wav
ffmpeg -i cable.wav -af volumedetect -f null -
# mean_volume 在 -40 dB 上下算正常；-91 dB 说明链路没通
```
