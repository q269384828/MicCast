using System.Runtime.InteropServices;

namespace MicCastReceiver;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    /// <summary>
    /// 本程序编译成 WinExe（为了双击不弹黑框），代价是即使在命令行里调用也不会附加到父控制台，
    /// Console.WriteLine 的输出会直接丢掉。有命令行参数时手动附加一次，才能看到 --status 之类的输出。
    /// </summary>
    private static void EnsureConsole()
    {
        try { AttachConsole(AttachParentProcess); } catch { }
        try
        {
            var stdout = Console.OpenStandardOutput();
            if (stdout is not null)
            {
                Console.SetOut(new StreamWriter(stdout) { AutoFlush = true });
            }
            var stderr = Console.OpenStandardError();
            if (stderr is not null)
            {
                Console.SetError(new StreamWriter(stderr) { AutoFlush = true });
            }
        }
        catch { }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => a.StartsWith("--") || a.StartsWith("-") || a.StartsWith("/")))
        {
            EnsureConsole();
        }

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

        // --status：连已有后台打印一次状态后退出（不抢占宿主身份）
        if (options.Status)
        {
            Environment.ExitCode = RunStatusAsync().GetAwaiter().GetResult();
            return;
        }

        // --headless：自己当宿主，不开窗口跑接收
        if (options.Headless)
        {
            var host = new ReceiverHost { Profile = options.Latency };
            if (!BackendServer.TryCreate(host, out BackendServer? server) || server is null)
            {
                Console.Error.WriteLine(
                    "ERROR 已经有一个 MicCast 后台在运行（图形界面或无头模式）。");
                Console.Error.WriteLine(
                    "      用 --status 查看它的状态，或先关掉它。");
                host.Dispose();
                Environment.ExitCode = 6;
                return;
            }
            try
            {
                Environment.ExitCode = RunHeadlessAsync(options, host).GetAwaiter().GetResult();
            }
            finally
            {
                host.Dispose();
                server.Dispose();
            }
            return;
        }

        ApplicationConfiguration.Initialize();

        // 逻辑单例：谁拿到 BackendServer 的互斥量谁就是宿主，
        // 其余进程一律作为前端连上去 —— 这样不会再出现两个进程抢 UDP 59100 和声卡。
        var guiHost = new ReceiverHost { Profile = options.Latency };
        BackendServer? guiServer = null;
        IBackend backend;
        if (BackendServer.TryCreate(guiHost, out guiServer) && guiServer is not null)
        {
            backend = guiHost;
        }
        else
        {
            var pipe = PipeBackend.ConnectAsync().GetAwaiter().GetResult();
            guiHost.Dispose();
            if (pipe is null)
            {
                MessageBox.Show(
                    "MicCast 的后台正在运行，但界面连不上它的通信通道。\n\n" +
                    "请先在任务管理器里结束 MicCastReceiver 进程，再重新打开。",
                    "无法连接后台", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            backend = pipe;
        }

        try
        {
            Application.Run(new MainForm(options, backend));
        }
        finally
        {
            backend.Dispose();
            guiServer?.Dispose();
        }
    }

    /// <summary>查询已有后台的状态，一次性输出后退出。</summary>
    private static async Task<int> RunStatusAsync()
    {
        var pipe = await PipeBackend.ConnectAsync(1200);
        if (pipe is null)
        {
            Console.WriteLine("没有正在运行的 MicCast 后台");
            return 7;
        }
        using (pipe)
        {
            // 服务端连上就推一帧完整状态，稍等一下即可
            for (int i = 0; i < 20 && pipe.PhoneIp.Length == 0 && !pipe.Connected; i++)
            {
                await Task.Delay(50);
            }
            await Task.Delay(120);
            Console.WriteLine(
                $"connected={pipe.Connected} want={pipe.WantReceiving} " +
                $"fps={pipe.Fps:F0} kbps={pipe.Kbps:F0} loss={pipe.LossPercent:F2}% " +
                $"buf={pipe.BufferedMs}ms target={pipe.TargetMs}ms jitter={pipe.JitterMs:F1}ms " +
                $"underrun={pipe.UnderrunMs:F0}ms dropped={pipe.DroppedMs:F0}ms " +
                $"rate={pipe.AveragePlaybackRate:F3}x readFrames={pipe.MaxReadFrames} availAtUnderrun={pipe.AvailableAtUnderrun:F0} " +
                $"device={pipe.DeviceName}");
            var servers = pipe.Servers;
            Console.WriteLine($"servers={servers.Count}");
            foreach (var s in servers) Console.WriteLine($"  {s.Name}|{s.Ip}|v{s.Version}");
            if (pipe.LastError.Length > 0) Console.WriteLine($"lastError={pipe.LastError}");
        }
        return 0;
    }

    /// <summary>
    /// 无窗口运行：连上手机、写入声卡、每秒打一行 STATS。
    /// 自动重连由 ReceiverHost 负责，这里只管输出和退出条件。
    /// </summary>
    private static async Task<int> RunHeadlessAsync(StartOptions options, IBackend backend)
    {
        void Say(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");

        int deviceIndex = options.Device;
        if (deviceIndex < 0)
        {
            var devices = backend.GetDevices();
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
            Say($"未指定 --ip，等待手机广播（最多 {options.DiscoverTimeoutSeconds} 秒）…");
            var deadline = DateTime.UtcNow.AddSeconds(options.DiscoverTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                var first = backend.Servers.FirstOrDefault();
                if (first is not null)
                {
                    ip = first.Ip;
                    break;
                }
                await Task.Delay(200);
            }
            if (ip.Length == 0)
            {
                Say($"ERROR {options.DiscoverTimeoutSeconds} 秒内没发现手机，确认手机上已点「启动麦克风服务」");
                return 4;
            }
            Say($"发现手机 {ip}");
        }

        if (options.Quiet)
        {
            backend.LogStream.Subscribe(line =>
            {
                if (line.Contains("失败") || line.Contains("断开") || line.Contains("重连"))
                {
                    Console.WriteLine(line);
                }
            });
        }
        else
        {
            backend.LogStream.Subscribe(line => Console.WriteLine(line));
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        await backend.StartAsync(ip, deviceIndex);
        Say(backend.Connected
            ? "已连接，开始接收"
            : $"连接失败: {(backend.LastError.Length > 0 ? backend.LastError : "未知原因")}，后台会每 2 秒自动重试");

        var stopAt = options.DurationSeconds > 0
            ? DateTime.UtcNow.AddSeconds(options.DurationSeconds)
            : DateTime.MaxValue;

        double lastUnderrunMs = backend.UnderrunMs;
        while (!cts.IsCancellationRequested && DateTime.UtcNow < stopAt)
        {
            try { await Task.Delay(1000, cts.Token); }
            catch (OperationCanceledException) { break; }

            if (options.Quiet) continue;

            // 字段名固定，方便脚本 awk / Select-String 直接取
            Console.WriteLine(
                $"STATS fps={backend.Fps:F0} kbps={backend.Kbps:F0} loss={backend.LossPercent:F2}% " +
                $"buf={backend.BufferedMs}ms target={backend.TargetMs}ms jitter={backend.JitterMs:F1}ms " +
                $"underrun={backend.UnderrunMs:F0}ms underrunDelta={backend.UnderrunMs - lastUnderrunMs:F0}ms " +
                $"dropped={backend.DroppedMs:F0}ms rate={backend.AveragePlaybackRate:F3}x");
            lastUnderrunMs = backend.UnderrunMs;
        }

        await backend.StopAsync();
        Say("已退出");
        return 0;
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
          --minimized            启动后直接藏到托盘，不弹主窗口（配合 --autostart 做开机自启）
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
