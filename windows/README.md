# MicCast Receiver (Windows)

手机麦克风 → 电脑的接收端。把手机端推来的 PCM 音频解码后写入你指定的播放设备。

## 界面

自绘的现代风格界面（不依赖任何 UI 库）：

- 顶部渐变应用标识 + 状态胶囊（`未启动` / `已发现手机` / `接收中` / `连接失败`）
- **单例运行**：再次双击只会把已有窗口切到前台，不会出现两个窗口抢 UDP 端口
- **手机下拉列表自动列出局域网里的设备**，点一下即可选中；发现不到时下面还能手动填 IP
- 输出设备下拉框，自动优先选中 `CABLE Input`
- 接收中显示三个大号指标（帧/秒、kbps、毫秒缓冲）+ 平滑跟随的渐变电平条
- 深浅色跟随系统（也可 `--theme dark|light` 强制），Windows 11 下自动圆角 + 深色标题栏
- 缺少 VB-CABLE 时顶部出现引导卡片，可**一键下载并静默安装**（下载地址见
  [vb-audio.com/Cable](https://vb-audio.com/Cable/)），也提供「打开下载页面」和「我已装好，刷新」

## 为什么自己写接收端

现成的第三方桌面端虽然能和我们的手机端握手、也能收到音频包，
但它的内部实现从不解码播放（日志里持续 `Queue's offer() failed`），属于闭源实现的局限。
既然手机端的协议、推流、参数都已经完全可控，自己写一个接收端反而最简单，也能完全掌控
缓冲策略、设备选择与错误恢复。

## 音频参数：16kHz 单声道

麦克风通话不需要 48kHz 立体声 —— 人声频带只有 50~7000 Hz：

| | 原实现 | 现在 |
|---|---|---|
| 采样率 | 48 kHz | **16 kHz** |
| 声道 | 2（立体声） | **1（单声道）** |
| 每帧 | 5ms / 960 字节 | 5ms / **160 字节** |
| 协议载荷带宽 | 1536 kbps | **257 kbps**（降 5.9 倍） |
| 含 IP/UDP 头的实际流量 | ~1568 kbps | **~301 kbps** |

零延迟代价、零 CPU 代价，只是把音质从"音乐级"降到"宽带语音级"，正好匹配麦克风用途。

**参数由服务端下发**：手机端在 `ServerAudioConfigResponse` 里声明实际的 `sampleRate/bits/channels`，
PC 端收到后再按该参数启动 `WasapiOut`。所以以后改采样率只需改手机端，PC 端自动跟随，
不会出现"按 48kHz 播 16kHz 数据"那种变调。

> PC 端有兜底：握手 1.5 秒还没收到配置、或先收到音频包，就按默认参数起播，避免一直静音。

## 延迟策略

接收端用的是**自适应抖动缓冲 + 变速追平**（`JitterBuffer.cs`）：

- 按帧到达间隔估算网络抖动（RFC 3550 指数平滑），目标水位 = 下限 + 3×抖动
- 抖动变大时快速抬高目标，抖动静下来后缓慢回落
- **追平靠微调播放速率，不丢弃音频**：缓冲偏多就播快一点（最多 +6%），偏少就播慢一点
  - 6% 速率偏移 ≈ 8 音分的音调变化，人耳完全听不出
  - 一秒钟能吃掉/补回约 60ms 的水位偏差
- 欠载时抬高目标水位自我修正，并缓慢衰减
- 启动前预填充静音，避免播放起步时连续欠载产生爆音

三档可选（`--latency`）：

| 档位 | 实测缓冲 | 抖动 | 硬丢弃 |
|---|---|---|---|
| `low` | 30.8 ms | 5.0 ms | 0 |
| `balanced`（默认） | **29~35 ms** | 5.0 ms | 0 |
| `stable` | 64.8 ms | 5.0 ms | 0 |

原实现（固定 `BufferedWaveProvider` + 50ms WasapiOut）实测稳定在 **60~95 ms**，
且缓冲一涨就会一路积压到溢出。现在默认档 **29~35 ms，降幅约 50%，且不再丢音频**。

> 验收方式：从 `CABLE Output` 录 20 秒 PCM，逐帧扫描全零采样。
> 最长连续全零 **0.79 ms**，没有任何 ≥2ms 的可察觉断流。
> 频域检查 8kHz 以上能量仅 -66.2 dB（整体 -38.1 dB），确认 16kHz 带宽生效。

## 已验证

本机实测（HarmonyOS 手机 → Windows 11，VB-CABLE 3.5.2.0）：

**链路：** 手机麦克风 → Wi-Fi → 本程序 → `CABLE Input` → ══虚拟线══ → `CABLE Output`

```
STATS frames=4776 fps=200 kbps=1533 buffered=75ms level=0.0147 device=CABLE Input (VB-Audio Virtual Cable)
```

再同时用 ffmpeg 从 `CABLE Output` 录 30 秒，做电平分析：

```
mean_volume: -33.8 dB
max_volume:  -2.5 dB
分段 RMS: -67.9 → -46.4 → -39.2 → -31.3 → -33.4 → -29.5 dB
```

- 稳定 **200 帧/秒**（5ms/帧，48kHz/16bit/2ch），**1533 kbps**
- 缓冲区 **60~95 ms**
- **从 `CABLE Output` 录到的不是静音**，是真实可用的麦克风输入

## 使用前提：一个虚拟声卡

Windows 没有内置虚拟麦克风，需要一个"播放端 → 录音端"成对的虚拟声卡：

- 推荐 **[VB-CABLE](https://vb-audio.com/Cable/)**（免费，donationware）：装完会多出
  - 播放设备 `CABLE Input`（另有 `CABLE In 16ch`）← 本程序往这里写
  - 录音设备 `CABLE Output` ← 会议软件 / Win+H 从这里采

> 注意：有些 App 自带的 `Virtual Speakers` / `Virtual Mic` 之类设备彼此**并不互通**
> （录 `Virtual Mic` 会得到 -91 dB 纯静音），不能拿来替代专用虚拟声卡。
> `立体声混音` 在很多机器上只是驱动残留，实际不可用。

### 安装 VB-CABLE

**推荐**：直接打开 `MicCastReceiver.exe`，界面顶部会自动出现「还需要一个虚拟声卡」卡片，点
**「自动下载并安装」** 即可 —— 程序会下载
[`VBCABLE_Driver_Pack46.zip`](https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack46.zip)
到临时目录、解压，并以管理员身份静默运行安装器（会弹一次 UAC，点「是」）。装完不需要重启。

手动安装：下载同一份 zip（新版见 [vb-audio.com/Cable](https://vb-audio.com/Cable/)），解压后**以管理员身份**运行 `VBCABLE_Setup_x64.exe`。

静默安装（-i 安装 / -h 隐藏界面）在本机实测 5 秒完成、**无需重启**：

```powershell
Start-Process .\VBCABLE_Setup_x64.exe -ArgumentList '-i','-h' -Verb RunAs
```

装完确认：

```powershell
sc.exe query VBAudioVACMME          # → STATE : 4  RUNNING
pnputil /enum-drivers | Select-String vbaudio
```

## 用法

1. 手机端打开 MicCast → 点「启动麦克风服务」（状态变成「等待电脑连接」）
2. 运行 `MicCastReceiver.exe`
3. **手机列表会自动出现**（监听 UDP 59200 的发现广播），选中即可；发现不到就手动填 IP
4. 播放设备选 **`CABLE Input`**（程序会自动优先选中）
5. 点「开始接收」，看到帧数上涨即成功
6. 会议软件 / Win+H 的麦克风选 **`CABLE Output`**

### 设备自动发现

手机端每 2 秒往 `255.255.255.255:59200` 和本网段广播地址各发一个发现包
（`int32 LE 长度 + ProtoServerConfig`）。接收端从同一个端口收，靠**数据报的来源 IP**
拿到手机地址，所以手机端不需要任何配置，也不用在界面上找自己的 IP。

- 超过 **8 秒**没再收到某个设备的广播，就把它从列表里移除
- 端口被别的程序占用时（例如某个仍在运行的桌面端 App 占着 59200），界面会提示"无法监听 UDP 59200"，此时手动填 IP 照样能用
- 无界面自检：

```powershell
MicCastReceiver.exe --discover 6
# 监听 UDP 59200，等待手机广播…
# 发现设备: <机型> (MicCast) @ 192.168.1.23
# DEVICE <机型> (MicCast) 192.168.1.23 v0.27.5 os=5 HarmonyOS 7.0
# SUMMARY count=1 seconds=6
```

`--autostart` 且不带 `--ip` 时，程序会等最多 20 秒自动发现第一台设备再连接。

### 让 Win+H 之类"只认默认麦克风"的程序也能用

`Win+H` 语音输入、以及不少会议软件的默认选项只读系统默认录音设备。用附带脚本把默认麦克风切过去：

```powershell
.\Set-DefaultMic.ps1 -List                     # 列出录音设备，<== 标记当前默认
.\Set-DefaultMic.ps1 -Name 'CABLE Output'      # 设为默认（会记住原设备）
.\Set-DefaultMic.ps1 -Restore                  # 用完恢复
```

脚本走 `IPolicyConfig::SetDefaultEndpoint`，把 Console / Multimedia / Communications 三种角色一起切，不需要重启或注销。

### 命令行参数

```powershell
MicCastReceiver.exe --help                  # 完整用法
MicCastReceiver.exe --list-devices          # 列出播放设备及序号
MicCastReceiver.exe --discover 6            # 只扫描手机，不开界面
MicCastReceiver.exe --autostart --device 4  # 自动发现 + 立即开始接收
MicCastReceiver.exe --theme dark            # 强制深色（默认跟随系统）
MicCastReceiver.exe --ip <手机IP> --device 4 --autostart
MicCastReceiver.exe --ip <ip> --loopback-verify 3000 --log D:\arx.log   # 自检
```

## 构建

```powershell
cd windows\MicCastReceiver
dotnet publish -c Release -r win-x64 --self-contained false
# 产物：bin\Release\net9.0-windows\win-x64\publish\MicCastReceiver.exe（约 0.73 MB，需 .NET 9 运行时）
```

## 协议实现要点

| 环节 | 说明 |
|---|---|
| TCP 59100 | delimited protobuf：收 `ProtoServerConfig` → 发 `ProtoClientConfig` → 收 `ProtoFeatures`；收到 `PingRequest` 回 `PingResponse` |
| UDP 59100 | 每个数据报 = 裸 `StreamData`（tick / timestampMs / payload / payloadSize） |
| 音频 | PCM S16LE 48kHz 2ch，写入 `BufferedWaveProvider`（600ms 容量、溢出丢弃） |
| 设备 | WASAPI 共享模式，50ms 延迟 |

**帧长必须是 5ms（960 字节）**：整包（含 protobuf 头 ~985 字节）必须小于 MTU 1472。
用 20ms（3856 字节）会被 IP 分片，官方端直接判 `malformed`。
