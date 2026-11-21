namespace MicCastReceiver;

/// <summary>
/// 命令行 / 启动参数。纯逻辑，WinForms 与 WinUI 两端共用。
/// </summary>
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
    /// <summary>连已有后台打印一次状态后退出</summary>
    public bool Status { get; init; }
    /// <summary>无头模式下只输出关键事件，不打每秒 STATS</summary>
    public bool Quiet { get; init; }
    /// <summary>无头模式运行多少秒后自动退出，0 表示一直跑</summary>
    public int DurationSeconds { get; init; }
    /// <summary>没给 --ip 时等自动发现的最长秒数</summary>
    public int DiscoverTimeoutSeconds { get; init; } = 30;
    /// <summary>启动后直接藏到托盘，不弹主窗口（配合 --autostart 做开机自启）</summary>
    public bool Minimized { get; init; }

    public static StartOptions Parse(string[] args)
    {
        string ip = string.Empty;
        int device = -1;
        bool auto = false;
        string log = string.Empty;
        int loopback = 0;
        string theme = string.Empty;
        bool headless = false;
        bool minimized = false;
        bool status = false;
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
                case "--status":
                    status = true;
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
                case "--minimized":
                    minimized = true;
                    break;
            }
        }
        return new StartOptions
        {
            Ip = ip, Device = device, AutoStart = auto, LogPath = log,
            LoopbackVerifyMs = loopback, Theme = theme, Latency = latency,
            Headless = headless, Status = status, Quiet = quiet, DurationSeconds = duration,
            DiscoverTimeoutSeconds = Math.Max(1, discoverTimeout),
            Minimized = minimized
        };
    }
}
