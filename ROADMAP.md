# MicCast 迭代路线

当前状态：手机端推流 + Windows 端接收 → VB-CABLE 全链路已跑通，
实测 200 帧/秒、1533 kbps、缓冲 60~95 ms，`CABLE Output` 录到真实音频。

下面按「值不值得做」排序，随时可以挑一条开工。

---

## 高优先级 — 决定它能不能当日常工具

### 1. 延迟优化（当前 ~100ms，目标 30~50ms）

现状：`BufferedWaveProvider` 固定 600ms 容量、`WasapiOut` 50ms 延迟。
实测 `buffered` 稳定在 60~95 ms，其实还有压缩空间。

- 做**自适应抖动缓冲**：统计到达间隔的滑动方差，缓冲区在 30~150 ms 间自动伸缩
- 稳定网络下压到 30~40 ms；抖动大时自动放宽，避免断音
- 加一个「低延迟模式」开关给游戏/直播场景

涉及：`windows/MicCastReceiver/AudioPlayer.cs`、`Receiver.cs`

### 2. 断线自动重连

现状：电脑端断开后手机要手动点「停止」再「启动」；PC 端断开后也要手动重连。

- 手机端：TCP `close` 后保持监听，PC 重连即自动恢复推流（`MicServer` 骨架已支持，只是 UI 会停采集）
- PC 端：指数退避重连（1s → 2s → 4s → 8s，上限 30s），连接成功后重置
- 给「自动重连」加开关，默认开

涉及：`MicServer.ets`、`MainForm.cs`、`Receiver.cs`

### 3. 托盘常驻 + 开机自启

现状：`Set-DefaultMic.ps1` 要手动跑；关窗口就全停。

- 关窗口 → 最小化到系统托盘，托盘图标显示连接状态（灰/绿/红）
- 托盘右键菜单：开始/停止接收、切换默认麦克风、打开主界面、退出
- 开机自启（写入 `HKCU\...\Run` 或任务计划程序）
- 记忆上次的设备选择与自动连接偏好

涉及：新增 `TrayApp.cs`，`MainForm.cs` 增加最小化逻辑；需要引入 `NotifyIcon` 图标资源

### 4. 麦克风静音 / 增益

- 全局热键（默认 `Ctrl+Alt+M`）切换静音，会议中不用切窗口
- PC 端加增益滑块（-12 ~ +12 dB），实时生效
- 手机端也可以做，但 PC 端更贴近使用场景

涉及：`AudioPlayer.cs`（软件增益）、`MainForm.cs`（热键注册 `RegisterHotKey`）

---

## 中优先级 — 体验与音质

### 5. 音频处理链路

- **高通滤波**：去掉空调/风扇的低频隆隆声（80 Hz 以下）
- **噪声抑制**：PC 端接 RNNoise（`rnnoise-net` 或 P/Invoke 原生库）
- **AGC 自动增益**：说话声小时自动提升
- **可选高通/降噪/AGC 三个开关**，让用户自己判断效果

这些对会议音质的提升比调参数本身更明显。

### 6. Opus 压缩

现状纯 PCM 1.5 Mbps。Opus 64 kbps 就能到通话质量，带宽降 20 倍，还自带 FEC 抗丢包。

- 手机端需要 Opus 编码器 → 鸿蒙侧要走 NAPI + C++（`libopus`）
- 或先用 `@ohos.multimedia.media` 的编码能力试水
- 协议侧要开 `ProtoFeatures.compression`（字段已就位）

工作量偏大，但如果要做移动网络场景就必须。

### 7. PC → 手机回传（双向对讲）

协议里有双向通道。做出来后手机插耳机就能同时听电脑声音，变成完整耳麦。

- 手机端加 `AudioRenderer` 播放 PC 推来的音频
- PC 端加 `WasapiLoopbackCapture` 采集系统声音
- 需要在同一 UDP 通道上区分方向（或用独立端口）

### 8. 多手机管理

现状只取发现列表第一台。

- 记录历史设备（名字 + IP），支持手动置顶
- 多设备时界面显示完整列表让用户选
- 适合多台手机/平板都在用的场景

---

## 工程质量

### 9. CI（GitHub Actions）

```yaml
windows-build:  dotnet build + dotnet test
harmony-build:  hvigorw assembleHap（无签名模式）
lint:           dotnet format + arktslint
```

需要先解决鸿蒙端 CI 的 SDK 获取（DevEco SDK 体积大，可考虑只跑编译检查）。

### 10. 协议跨语言对拍测试

`Protocol.cs` 与 `Messages.ets` 是两套独立实现的同一协议，容易改歪。

- 用 Node 脚本分别调用两边的编码逻辑，比对字节流
- 覆盖：`ProtoServerConfig`、`ProtoClientConfig`、`ProtoFeatures`、`StreamData`、`PingRequest/Response`
- 这些函数都是纯函数，好测

### 11. 打包分发

- **Windows**：`--self-contained` 单文件版（免装 .NET 运行时，约 60MB）+ MSI 安装包
- **鸿蒙**：打 release HAP，或走 AGC 分发
- 加应用图标（现在是占位图）

### 12. 自动化测试

`tools/` 下现在是手写验证脚本。整理成正式 `tests/` 目录，默认参数跑本机回环，`npm test` 一键执行。

---

## 低优先级 — 有想法但暂时不急

- **手机端记住上次状态**：打开 App 自动开始服务
- **二维码配对**：电脑端显示二维码，手机扫一下直连，绕开发现失败的情况
- **自定义设备名**：广播名改成「书房的手机」这类更好认的名字
- **Web 版接收端**：加 WebSocket 通道，浏览器也能当接收端
- **Linux / macOS 接收端**：协议已经清楚，移植 `NetworkStream` + PulseAudio/CoreAudio 工作量不大
- **多声道支持**：VB-CABLE 有 `CABLE In 16ch`，可以做环绕声串流

---

## 建议的下一步

**1 + 2 + 3 一起做**，能把项目从「demo 能跑」推到「日常真的用」：

1. 延迟优化 → 开会时不觉得慢半拍
2. 断线重连 → 不用每次手动点重启
3. 托盘 + 自启 → 开机就能用，不用管它

这三条做完，剩下的都是锦上添花。
