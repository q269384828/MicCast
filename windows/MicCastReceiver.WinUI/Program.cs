using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace MicCastReceiver.WinUI;

/// <summary>
/// 入口。UI 之外的模式（--status / --headless / --list-devices / --discover / --help）
/// 与 WinForms 版完全一致，共用 Core 里的 ReceiverHost / PipeBackend / Protocol。
/// </summary>
public static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    /// <summary>
    /// 编译成 WinExe 是为了双击不弹黑框，代价是命令行里调用时不会附加到父控制台，
    /// Console.WriteLine 的输出会直接丢掉。有命令行参数时手动附加一次。
    ///
    /// 用 AttachConsole 而不是 AllocConsole：后者会抢着开一个新控制台窗口，
    /// 在输出被重定向时还会把管道句柄弄乱，表现为命令挂住不返回。
    /// </summary>
    private static void EnsureConsole()
    {
        try { AttachConsole(AttachParentProcess); } catch { }
        try
        {
            var stdout = Console.OpenStandardOutput();
            if (stdout is not null) Console.SetOut(new StreamWriter(stdout) { AutoFlush = true });
            var stderr = Console.OpenStandardError();
            if (stderr is not null) Console.SetError(new StreamWriter(stderr) { AutoFlush = true });
        }
        catch { }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a.StartsWith("--") || a.StartsWith("-") || a.StartsWith("/")))
        {
            EnsureConsole();
        }
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var options = StartOptions.Parse(args);

        if (args.Contains("--discover"))
        {
            int seconds = 6;
            int idx = Array.IndexOf(args, "--discover");
            if (idx >= 0 && idx + 1 < args.Length) int.TryParse(args[idx + 1], out seconds);
            RunDiscover(Math.Max(1, seconds));
            return 0;
        }

        if (args.Contains("--list-devices"))
        {
            foreach (var d in AudioPlayer.GetRenderDevices())
            {
                Console.WriteLine($"[{d.Index}] {d.Name}");
            }
            return 0;
        }

        if (options.Status)
        {
            return RunStatusAsync().GetAwaiter().GetResult();
        }

        if (options.Headless)
        {
            return RunHeadless(options);
        }

        // GUI 模式：起 WinUI 消息循环
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(queue));
            _ = new App(options);
        });
        return 0;
    }

    /// <summary>扫描局域网里的手机，打印结果后退出。</summary>
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

    /// <summary>连已有后台打印一次状态后退出（不抢占宿主身份）。</summary>
    private static async Task<int> RunStatusAsync()
    {
        var pipe = await PipeBackend.ConnectAsync();
        if (pipe is null)
        {
            Console.Error.WriteLine("ERROR 没有正在运行的 MicCast 后台。");
            return 3;
        }
        using (pipe)
        {
            for (int i = 0; i < 10 && pipe.ServerName.Length == 0; i++)
            {
                await Task.Delay(120);
            }
            Console.WriteLine(
                $"connected={pipe.Connected} want={pipe.WantReceiving} " +
                $"fps={pipe.Fps:F0} kbps={pipe.Kbps:F0} loss={pipe.LossPercent:F2}% " +
                $"buf={pipe.BufferedMs}ms target={pipe.TargetMs}ms jitter={pipe.JitterMs:F1}ms " +
                $"underrun={pipe.UnderrunMs:F0}ms dropped={pipe.DroppedMs:F0}ms " +
                $"rate={pipe.AveragePlaybackRate:F3}x readFrames={pipe.MaxReadFrames} " +
                $"availAtUnderrun={pipe.AvailableAtUnderrun:F0} device={pipe.DeviceName}");
        }
        return 0;
    }

    private static int RunHeadless(StartOptions options)
    {
        var host = new ReceiverHost { Profile = options.Latency };
        if (!BackendServer.TryCreate(host, out BackendServer? server) || server is null)
        {
            Console.Error.WriteLine("ERROR 已经有一个 MicCast 后台在运行（图形界面或无头模式）。");
            host.Dispose();
            return 6;
        }
        try
        {
            return RunHeadlessLoop(options, host).GetAwaiter().GetResult();
        }
        finally
        {
            host.Dispose();
            server.Dispose();
        }
    }

    private static async Task<int> RunHeadlessLoop(StartOptions options, IBackend backend)
    {
        string ip = options.Ip;
        if (ip.Length == 0)
        {
            var deadline = DateTime.Now.AddSeconds(options.DiscoverTimeoutSeconds);
            while (DateTime.Now < deadline && backend.Servers.Count == 0)
            {
                await Task.Delay(200);
            }
            if (backend.Servers.Count > 0) ip = backend.Servers[0].Ip;
        }

        if (!System.Net.IPAddress.TryParse(ip, out _))
        {
            Console.Error.WriteLine("ERROR 没找到手机。请用 --ip 指定，或确保手机已开启服务。");
            return 4;
        }

        using var logSub = backend.LogStream.Subscribe(line =>
        {
            if (!options.Quiet || !line.StartsWith("STATS")) Console.WriteLine(line);
        });

        await backend.StartAsync(ip, options.Device);
        Console.WriteLine($"STARTED {ip}");

        var started = DateTime.Now;
        while (true)
        {
            await Task.Delay(500);
            if (options.DurationSeconds > 0 &&
                (DateTime.Now - started).TotalSeconds >= options.DurationSeconds)
            {
                break;
            }
        }

        await backend.StopAsync();
        return 0;
    }

    private const string Usage = """
        MicCast — 把手机麦克风变成一个 Windows 麦克风

        用法：
          MicCastReceiver.exe                     打开图形界面
          MicCastReceiver.exe --autostart         启动后自动开始接收
          MicCastReceiver.exe --minimized         启动后直接进托盘，不弹窗口
          MicCastReceiver.exe --status            打印后台状态后退出
          MicCastReceiver.exe --headless          不开窗口，直接跑接收
          MicCastReceiver.exe --ip 192.168.1.5    指定手机 IP
          MicCastReceiver.exe --device 3          指定输出设备序号
          MicCastReceiver.exe --list-devices      列出所有播放设备
          MicCastReceiver.exe --discover 6        扫描局域网里的手机
          MicCastReceiver.exe --theme dark|light  强制深浅色
          MicCastReceiver.exe --latency low|balanced|stable
          MicCastReceiver.exe --help              显示本帮助
        """;
}