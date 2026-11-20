namespace MicCastReceiver;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
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

        // 图形界面只允许一个实例；再次启动就把已有窗口叫到前台
        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.ActivateExisting();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(options));
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

          --ip <手机IP>          直接连接指定手机（隐含 --autostart）
          --device <索引>        输出设备索引，先用 --list-devices 查看（默认按名字自动选 CABLE Input）
          --autostart            启动后立即开始接收（不填 --ip 时等待自动发现）
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

        图形界面里手机列表会自动出现；若发现不到，可手动填手机 IP。
        图形界面只允许一个实例，重复启动会把已有窗口切到前台（--help/--discover 等控制台模式不受限制）。

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

    public static StartOptions Parse(string[] args)
    {
        string ip = string.Empty;
        int device = -1;
        bool auto = false;
        string log = string.Empty;
        int loopback = 0;
        string theme = string.Empty;
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
            }
        }
        return new StartOptions
        {
            Ip = ip, Device = device, AutoStart = auto, LogPath = log,
            LoopbackVerifyMs = loopback, Theme = theme, Latency = latency
        };
    }
}
