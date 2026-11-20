namespace MicCastReceiver;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 统一 UTF-8 输出，否则控制台按 GBK 解码会把中文帮助信息显示成乱码
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        var options = StartOptions.Parse(args);

        if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
        {
            Console.WriteLine(Usage);
            return;
        }

        if (args.Contains("--discover"))
        {
            int seconds = 6;
            int idx = Array.IndexOf(args, "--discover");
            if (idx >= 0 && idx + 1 < args.Length) int.TryParse(args[idx + 1], out seconds);
            RunDiscover(Math.Max(1, seconds));
            return;
        }

        if (args.Contains("--list-devices"))
        {
            var devices = AudioPlayer.GetRenderDevices();
            var sb = new System.Text.StringBuilder();
            foreach (var d in devices) sb.AppendLine($"[{d.Index}] {d.Name}");
            if (options.LogPath.Length > 0) File.WriteAllText(options.LogPath, sb.ToString());
            else Console.WriteLine(sb.ToString());
            return;
        }

        // 无头模式：不开窗口，直接用控制台跑接收。必须在单实例检查之前，
        // 否则会和正在运行的图形界面抢同一个互斥量。
        if (options.Headless)
        {
            Environment.ExitCode = RunHeadlessAsync(options).GetAwaiter().GetResult();
            return;
        }

        // 图形界面只允许一个实例；再次启动就把已有窗口叫到前台
        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.ActivateExisting();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(options));
    }

    /// <summary>
    /// 无窗口运行：发现手机 → 连接 → 写入声卡 → 每秒打一行 STATS，断线自动重连。
    /// 供脚本、开机自启、以及没有图形环境时使用。
    /// </summary>
    private static async Task<int> RunHeadlessAsync(StartOptions options)
    {
        void Say(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");

        int deviceIndex = options.Device;
        if (deviceIndex < 0)
        {
            var devices = AudioPlayer.GetRenderDevices();
            var match = devices.FirstOrDefault(d =>
                d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
            if (match.Name is null)
            {
                Say("ERROR 没找到 CABLE Input，先跑 --list-devices 看有哪些播放设备");
                return 2;
            }
            deviceIndex = match.Index;
            Say($"输出设备自动选中 [{match.Index}] {match.Name}");
        }

        string ip = options.Ip;
        if (ip.Length == 0)
        {
            Say($"未指定 --ip，监听 UDP {Protocol.DiscoveryPort} 等待手机广播…");
            var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var discovery = new Discovery())
            {
                discovery.Changed += () =>
                {
                    var first = discovery.Servers.FirstOrDefault();
                    if (first is not null) found.TrySetResult(first.Ip);
                };
                discovery.Start();
                if (!discovery.Running)
                {
                    Say($"ERROR 无法监听 UDP {Protocol.DiscoveryPort}，端口可能被占用");
                    return 3;
                }
                var completed = await Task.WhenAny(found.Task, Task.Delay(TimeSpan.FromSeconds(options.DiscoverTimeoutSeconds)));
                if (completed != found.Task)
                {
                    Say($"ERROR {options.DiscoverTimeoutSeconds} 秒内没发现手机，确认手机上已点「启动麦克风服务」");
                    return 4;
                }
                ip = await found.Task;
            }
            Say($"发现手机 {ip}");
        }

        using var receiver = new Receiver();
        receiver.Profile = options.Latency;
        // Receiver.Emit 输出的行本身已带时间戳，这里不要再叠加一层
        if (!options.Quiet) receiver.Log += line => Console.WriteLine(line);
        else receiver.Log += line => { if (line.Contains("失败") || line.Contains("断开")) Console.WriteLine(line); };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var deadline = options.DurationSeconds > 0
            ? DateTime.UtcNow.AddSeconds(options.DurationSeconds)
            : DateTime.MaxValue;

        int statsTick = 0;
        double lastUnderrunMs = 0;
        string lastStatLine = string.Empty;

        while (!cts.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            if (!receiver.Connected)
            {
                await receiver.StartAsync(ip, deviceIndex);
                if (!receiver.Connected)
                {
                    Say($"连接失败: {(receiver.LastError.Length > 0 ? receiver.LastError : "未知原因")}，2 秒后重试");
                    if (!await SafeDelayAsync(2000, cts.Token)) break;
                    continue;
                }
                Say("已连接，开始接收");
                lastUnderrunMs = 0;
            }

            if (!await SafeDelayAsync(1000, cts.Token)) break;

            if (receiver.Connected && !options.Quiet)
            {
                statsTick++;
                // 每秒一行，字段名稳定，方便脚本 awk / Select-String 取用
                lastStatLine =
                    $"STATS fps={receiver.Fps:F0} kbps={receiver.Kbps:F0} loss={receiver.LossPercent:F2}% " +
                    $"buf={receiver.BufferedMs}ms target={receiver.TargetMs}ms jitter={receiver.JitterMs:F1}ms " +
                    $"underrun={receiver.UnderrunMs:F0}ms underrunDelta={receiver.UnderrunMs - lastUnderrunMs:F0}ms " +
                    $"dropped={receiver.DroppedMs:F0}ms rate={receiver.AveragePlaybackRate:F3}x";
                Console.WriteLine(lastStatLine);
                lastUnderrunMs = receiver.UnderrunMs;
            }
        }

        try { await receiver.StopAsync(); } catch { }
        Say("已退出");
        return 0;
    }

    private static async Task<bool> SafeDelayAsync(int ms, CancellationToken token)
    {
        try
        {
            await Task.Delay(ms, token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>无界面扫描局域网内的手机，供脚本/自检使用。</summary>
    private static void RunDiscover(int seconds)
    {
        using var discovery = new Discovery();
        discovery.Log += line => Console.WriteLine(line);
        discovery.Changed += () =>
        {
            foreach (var s in discovery.Servers)
            {
                Console.WriteLine($"DEVICE {s.Name} {s.Ip} v{s.Version} os={s.Os} {s.OsVersion}");
            }
        };
        discovery.Start();
        if (!discovery.Running)
        {
            Console.WriteLine($"ERROR 无法监听 UDP {Protocol.DiscoveryPort}");
            return;
        }
        Thread.Sleep(seconds * 1000);
        var found = discovery.Servers;
        Console.WriteLine($"SUMMARY count={found.Count} seconds={seconds}");
        foreach (var s in found)
        {
            Console.WriteLine($"RESULT {s.Name}|{s.Ip}|{s.Version}|{s.OsVersion}");
        }
    }

    private const string Usage = """
        MicCastReceiver — 接收手机麦克风音频并播放到指定输出设备（声渡）

        用法: MicCastReceiver.exe [选项]

        无头模式（不开窗口，适合脚本 / 开机自启 / 无桌面环境）:
          --headless             直接在控制台跑接收，Ctrl+C 退出（别名 --no-gui）
          --duration <秒>         配合 --headless，跑够指定秒数自动退出
          --quiet, -q            只输出连接/断开等关键事件，不打每秒 STATS
          --discover-timeout <秒> 没给 --ip 时等自动发现的最长秒数（默认 30）

          --headless 每秒打印一行，字段名固定，方便脚本取用：
            STATS fps=200 kbps=255 loss=0.00% buf=35ms target=30ms jitter=5.2ms
                  underrun=120ms underrunDelta=0ms dropped=0ms rate=1.001x
          其中 loss 是 UDP 层真实丢包率（由 StreamData.tick 跳号推断），
          underrunDelta 是这一秒新产生的欠载毫秒数 —— 这两个是判断音质的关键。

        通用选项:
          --ip <手机IP>          直接连接指定手机，跳过自动发现
          --device <索引>        输出设备索引，先用 --list-devices 查看（默认按名字自动选 CABLE Input）
          --autostart            图形界面启动后立即开始接收
          --log <文件路径>       把状态/统计追加写入文件，便于无头验证
          --list-devices         列出所有播放设备后退出
          --discover [秒数]      不开界面，扫描局域网内的手机后退出（默认 6 秒）
          --theme <dark|light>   强制深浅色，默认跟随系统
          --latency <low|balanced|stable>
                                 延迟策略，默认 balanced（约 20~40ms 目标缓冲）
                                   low     = 最低延迟，网络差时可能断续
                                   balanced= 自适应（默认）
                                   stable  = 优先稳定，缓冲更大
          --loopback-verify <毫秒> 启动后播放测试音并回环抓取，输出 RMS 校验链路
          --help, -h             显示本帮助

        常用示例:
          MicCastReceiver.exe --headless --ip 192.168.3.168
          MicCastReceiver.exe --headless --discover-timeout 60 --latency stable
          MicCastReceiver.exe --headless --duration 30 --quiet

        图形界面里手机列表会自动出现；若发现不到，可手动填手机 IP。
        图形界面只允许一个实例，重复启动会把已有窗口切到前台
        （--headless / --help / --discover 等控制台模式不受此限制）。

        缺虚拟声卡时：界面顶部会出现提示，可一键下载并安装 VB-CABLE
        （也可手动去 https://vb-audio.com/Cable/ 下载）。

        配合 VB-CABLE 当麦克风用：
          1. 安装 VB-CABLE（https://vb-audio.com/Cable/）
          2. 本程序输出设备选 "CABLE Input"
          3. 需要麦克风的软件里把输入设备选 "CABLE Output (VB-Audio Virtual Cable)"
             或用 Set-DefaultMic.ps1 -Name 'CABLE Output' 把它设为系统默认麦克风
        """;
}

public sealed class StartOptions
{
    public string Ip { get; init; } = string.Empty;
    public int Device { get; init; } = -1;
    public bool AutoStart { get; init; }
    public string LogPath { get; init; } = string.Empty;
    public int LoopbackVerifyMs { get; init; }
    public string Theme { get; init; } = string.Empty;
    public LatencyProfile Latency { get; init; } = LatencyProfile.Balanced;
    /// <summary>不开窗口，直接在控制台跑接收</summary>
    public bool Headless { get; init; }
    /// <summary>无头模式下只输出关键事件，不打每秒 STATS</summary>
    public bool Quiet { get; init; }
    /// <summary>无头模式运行多少秒后自动退出，0 表示一直跑</summary>
    public int DurationSeconds { get; init; }
    /// <summary>没给 --ip 时等自动发现的最长秒数</summary>
    public int DiscoverTimeoutSeconds { get; init; } = 30;

    public static StartOptions Parse(string[] args)
    {
        string ip = string.Empty;
        int device = -1;
        bool auto = false;
        string log = string.Empty;
        int loopback = 0;
        string theme = string.Empty;
        bool headless = false;
        bool quiet = false;
        int duration = 0;
        int discoverTimeout = 30;
        LatencyProfile latency = LatencyProfile.Balanced;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ip" when i + 1 < args.Length:
                    ip = args[++i];
                    auto = true;
                    break;
                case "--device" when i + 1 < args.Length:
                    int.TryParse(args[++i], out device);
                    break;
                case "--autostart":
                    auto = true;
                    break;
                case "--log" when i + 1 < args.Length:
                    log = args[++i];
                    break;
                case "--theme" when i + 1 < args.Length:
                    theme = args[++i];
                    break;
                case "--latency" when i + 1 < args.Length:
                    latency = args[++i].ToLowerInvariant() switch
                    {
                        "low" => LatencyProfile.Low,
                        "stable" => LatencyProfile.Stable,
                        _ => LatencyProfile.Balanced
                    };
                    break;
                case "--loopback-verify" when i + 1 < args.Length:
                    int.TryParse(args[++i], out loopback);
                    break;
                case "--headless" or "--no-gui":
                    headless = true;
                    break;
                case "--quiet" or "-q":
                    quiet = true;
                    break;
                case "--duration" when i + 1 < args.Length:
                    int.TryParse(args[++i], out duration);
                    break;
                case "--discover-timeout" when i + 1 < args.Length:
                    int.TryParse(args[++i], out discoverTimeout);
                    break;
            }
        }
        return new StartOptions
        {
            Ip = ip, Device = device, AutoStart = auto, LogPath = log,
            LoopbackVerifyMs = loopback, Theme = theme, Latency = latency,
            Headless = headless, Quiet = quiet, DurationSeconds = duration,
            DiscoverTimeoutSeconds = Math.Max(1, discoverTimeout)
        };
    }
}
