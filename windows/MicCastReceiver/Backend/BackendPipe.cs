using System.IO.Pipes;
using System.Reactive.Subjects;
using System.Text.Json;

namespace MicCastReceiver;

/// <summary>后端状态的传输格式，同时也是管道协议里 status 事件的载荷。</summary>
public sealed class StatusDto
{
    public bool Connected { get; set; }
    public bool WantReceiving { get; set; }
    public string LastError { get; set; } = string.Empty;
    public string PhoneIp { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string ServerVersion { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public long Frames { get; set; }
    public double Fps { get; set; }
    public double Kbps { get; set; }
    public double LossPercent { get; set; }
    public long LostFrames { get; set; }
    public long OutOfOrderCount { get; set; }
    public long CatchUpCount { get; set; }
    public int BufferedMs { get; set; }
    public int TargetMs { get; set; }
    public double JitterMs { get; set; }
    public double UnderrunMs { get; set; }
    public long UnderrunCount { get; set; }
    public double DroppedMs { get; set; }
    public double AveragePlaybackRate { get; set; }
    public float Level { get; set; }
    public string Profile { get; set; } = "balanced";
    public List<ServerDto> Servers { get; set; } = new();
    public List<DeviceDto> Devices { get; set; } = new();
}

public sealed class ServerDto
{
    public string Name { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public int Os { get; set; }
}

public sealed class DeviceDto
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>前端发给宿主的命令。</summary>
public sealed class CommandDto
{
    public string Cmd { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public int Device { get; set; } = -1;
    public string Latency { get; set; } = string.Empty;
}

internal static class BackendJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static StatusDto Capture(IBackend b) => new()
    {
        Connected = b.Connected,
        WantReceiving = b.WantReceiving,
        LastError = b.LastError,
        PhoneIp = b.PhoneIp,
        ServerName = b.ServerName,
        ServerVersion = b.ServerVersion,
        DeviceName = b.DeviceName,
        Frames = b.Frames,
        Fps = b.Fps,
        Kbps = b.Kbps,
        LossPercent = b.LossPercent,
        LostFrames = b.LostFrames,
        OutOfOrderCount = b.OutOfOrderCount,
        CatchUpCount = b.CatchUpCount,
        BufferedMs = b.BufferedMs,
        TargetMs = b.TargetMs,
        JitterMs = b.JitterMs,
        UnderrunMs = b.UnderrunMs,
        UnderrunCount = b.UnderrunCount,
        DroppedMs = b.DroppedMs,
        AveragePlaybackRate = b.AveragePlaybackRate,
        Level = b.Level,
        Profile = b.Profile.ToString().ToLowerInvariant(),
        Servers = b.Servers.Select(s => new ServerDto
        {
            Name = s.Name, Ip = s.Ip, Version = s.Version,
            OsVersion = s.OsVersion, Os = s.Os
        }).ToList()
    };
}

/// <summary>
/// 宿主侧的命名管道服务。持有互斥量 + 管道，是「逻辑单例」的实际执行者：
/// 拿不到互斥量就说明已经有宿主在跑，本次启动只能当客户端。
/// </summary>
public sealed class BackendServer : IDisposable
{
    public const string PipeName = "MicCastReceiver.Backend";
    private const string MutexName = @"Local\MicCastReceiver.Backend";

    private static Mutex? _mutex;
    private readonly ReceiverHost _host;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<StreamWriter> _clients = new();
    private readonly object _clientsGate = new();
    private readonly List<string> _logHistory = new();
    private readonly object _logGate = new();

    private BackendServer(ReceiverHost host)
    {
        _host = host;
        // 订阅宿主的日志流：一份推给已连上的前端，一份留作缓存给后连上来的前端回放
        _host.LogStream.Subscribe(line =>
        {
            lock (_logGate)
            {
                _logHistory.Add(line);
                if (_logHistory.Count > 120) _logHistory.RemoveAt(0);
            }
            _ = BroadcastAsync(new { ev = "log", line });
        });
    }

    private List<string> SnapshotLogs()
    {
        lock (_logGate) return _logHistory.ToList();
    }

    /// <summary>尝试成为宿主。返回 false 表示已有宿主在跑，应改用 <see cref="PipeBackend"/>。</summary>
    public static bool TryCreate(ReceiverHost host, out BackendServer? server)
    {
        server = null;
        try
        {
            var mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return false;
            }
            _mutex = mutex;
        }
        catch
        {
            return false;
        }

        server = new BackendServer(host);
        server.Start();
        return true;
    }

    private void Start()
    {
        _ = Task.Run(AcceptLoopAsync);
        _ = Task.Run(PushLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_cts.Token);
                var writer = new StreamWriter(pipe) { AutoFlush = true };
                lock (_clientsGate) _clients.Add(writer);
                _ = Task.Run(() => ClientLoopAsync(pipe, writer));
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                return;
            }
            catch
            {
                // 管道名被占用或对方断开，稍后重试
                pipe?.Dispose();
                await Task.Delay(500, _cts.Token).ContinueWith(_ => { });
            }
        }
    }

    private async Task ClientLoopAsync(NamedPipeServerStream pipe, StreamWriter writer)
    {
        try
        {
            using var reader = new StreamReader(pipe);
            // 先把历史日志回放一遍，界面连上就能看到之前发生了什么
            foreach (var line in SnapshotLogs())
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(
                    new { ev = "log", line }, BackendJson.Options));
            }
            // 再推一次完整状态，客户端不用等下一个周期
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new { ev = "status", data = BackendJson.Capture(_host) }, BackendJson.Options));
            while (!_cts.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(_cts.Token);
                if (line is null) break;
                await HandleCommandAsync(line);
            }
        }
        catch { }
        finally
        {
            lock (_clientsGate) _clients.Remove(writer);
            try { pipe.Dispose(); } catch { }
        }
    }

    private async Task HandleCommandAsync(string line)
    {
        CommandDto? cmd;
        try { cmd = JsonSerializer.Deserialize<CommandDto>(line, BackendJson.Options); }
        catch { return; }
        if (cmd is null) return;

        switch (cmd.Cmd.ToLowerInvariant())
        {
            case "start":
                if (cmd.Latency.Length > 0)
                {
                    _host.Profile = cmd.Latency.ToLowerInvariant() switch
                    {
                        "low" => LatencyProfile.Low,
                        "stable" => LatencyProfile.Stable,
                        _ => LatencyProfile.Balanced
                    };
                }
                await _host.StartAsync(cmd.Ip, cmd.Device);
                break;
            case "stop":
                await _host.StopAsync();
                break;
            case "shutdown":
                _cts.Cancel();
                break;
        }
    }

    /// <summary>把一条消息写给所有已连接的前端。</summary>
    private Task BroadcastAsync(object payload)
    {
        List<StreamWriter> targets;
        lock (_clientsGate) targets = _clients.ToList();
        if (targets.Count == 0) return Task.CompletedTask;
        string text = JsonSerializer.Serialize(payload, BackendJson.Options);
        return Task.Run(async () =>
        {
            foreach (var w in targets)
            {
                try { await w.WriteLineAsync(text); }
                catch
                {
                    lock (_clientsGate) _clients.Remove(w);
                }
            }
        });
    }

    /// <summary>每 200ms 把状态推给所有前端，前端因此不需要轮询。</summary>
    private async Task PushLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(200, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            List<StreamWriter> targets;
            lock (_clientsGate) targets = _clients.ToList();
            if (targets.Count == 0) continue;

            string payload = JsonSerializer.Serialize(
                new { ev = "status", data = BackendJson.Capture(_host) }, BackendJson.Options);
            foreach (var w in targets)
            {
                try { await w.WriteLineAsync(payload); }
                catch
                {
                    lock (_clientsGate) _clients.Remove(w);
                }
            }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        lock (_clientsGate)
        {
            foreach (var w in _clients) { try { w.Dispose(); } catch { } }
            _clients.Clear();
        }
        try { _cts.Dispose(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
    }
}

/// <summary>
/// 前端侧的管道客户端。当另一个进程已经是宿主时，图形界面和命令行都用它，
/// 这样两边看到的是同一份状态、操作的是同一个声卡。
/// </summary>
public sealed class PipeBackend : IBackend
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamWriter _writer;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly BehaviorSubject<StatusDto> _state = new(new StatusDto());
    private readonly ReplaySubject<string> _logs = new(120);
    private StatusDto _status = new();
    private LatencyProfile _profile = LatencyProfile.Balanced;
    private bool _disposed;

    private PipeBackend(NamedPipeClientStream pipe, StreamWriter writer)
    {
        _pipe = pipe;
        _writer = writer;
        _ = Task.Run(ReadLoopAsync);
    }

    public IObservable<StatusDto> StateStream => _state;
    public IObservable<string> LogStream => _logs;

    public static async Task<PipeBackend?> ConnectAsync(int timeoutMs = 1500)
    {
        try
        {
            var pipe = new NamedPipeClientStream(".", BackendServer.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(timeoutMs);
            await pipe.ConnectAsync(cts.Token);
            var writer = new StreamWriter(pipe) { AutoFlush = true };
            return new PipeBackend(pipe, writer);
        }
        catch
        {
            return null;
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            using var reader = new StreamReader(_pipe);
            while (!_cts.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(_cts.Token);
                if (line is null) break;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("ev", out var ev)) continue;

                if (ev.GetString() == "status" && root.TryGetProperty("data", out var data))
                {
                    var dto = data.Deserialize<StatusDto>(BackendJson.Options);
                    if (dto is not null)
                    {
                        lock (_gate) _status = dto;
                        try { if (!_state.IsDisposed) _state.OnNext(dto); } catch { }
                    }
                }
                else if (ev.GetString() == "log" && root.TryGetProperty("line", out var logLine))
                {
                    string? text = logLine.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        // ReplaySubject 会把这条留住，界面晚订阅也能收到
                        try { if (!_logs.IsDisposed) _logs.OnNext(text); } catch { }
                    }
                }
            }
        }
        catch { }
    }

    private StatusDto S { get { lock (_gate) return _status; } }

    public bool Connected => S.Connected;
    public bool WantReceiving => S.WantReceiving;
    public string LastError => S.LastError;
    public string PhoneIp => S.PhoneIp;
    public string ServerName => S.ServerName;
    public string ServerVersion => S.ServerVersion;
    public string DeviceName => S.DeviceName;
    public long Frames => S.Frames;
    public double Fps => S.Fps;
    public double Kbps => S.Kbps;
    public double LossPercent => S.LossPercent;
    public long LostFrames => S.LostFrames;
    public long OutOfOrderCount => S.OutOfOrderCount;
    public long CatchUpCount => S.CatchUpCount;
    public int BufferedMs => S.BufferedMs;
    public int TargetMs => S.TargetMs;
    public double JitterMs => S.JitterMs;
    public double UnderrunMs => S.UnderrunMs;
    public long UnderrunCount => S.UnderrunCount;
    public double DroppedMs => S.DroppedMs;
    public double AveragePlaybackRate => S.AveragePlaybackRate;
    public float Level => S.Level;

    public LatencyProfile Profile
    {
        get => _profile;
        set => _profile = value;
    }

    public List<Protocol.DiscoveredServer> Servers =>
        S.Servers.Select(s => new Protocol.DiscoveredServer(
            s.Ip, s.Name, s.Version, s.OsVersion, s.Os, DateTime.Now)).ToList();

    // 设备列表来自宿主，但宿主只在状态里带一次完整信息；这里退化为查询本地枚举，
    // 因为设备枚举是纯读取操作，不会和宿主抢资源。
    public List<(int Index, string Name)> GetDevices() => AudioPlayer.GetRenderDevices();

    public Task StartAsync(string ip, int deviceIndex) => SendAsync(new CommandDto
    {
        Cmd = "start", Ip = ip, Device = deviceIndex,
        Latency = _profile.ToString().ToLowerInvariant()
    });

    public Task StopAsync() => SendAsync(new CommandDto { Cmd = "stop" });

    private async Task SendAsync(CommandDto cmd)
    {
        try
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(cmd, BackendJson.Options));
        }
        catch (Exception ex)
        {
            _logs.OnNext($"{DateTime.Now:HH:mm:ss} 与后台进程通信失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _writer.Dispose(); } catch { }
        try { _pipe.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
