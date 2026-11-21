namespace MicCastReceiver;

using System.Reactive.Subjects;

/// <summary>
/// 真正的接收逻辑：发现手机、连接、收流、写声卡、断线重连、维护设备列表。
/// 整个程序里只应存在一份（由 <see cref="BackendServer"/> 的命名管道独占权保证）。
/// 图形界面和命令行都只是它的前端。
///
/// 对外只暴露两条可观察流：状态和日志。前端订阅即可，不需要轮询、不需要自己
/// 处理线程切换，也不需要担心「我是先启动还是后启动」——
/// BehaviorSubject 会重放当前状态，ReplaySubject 会重放历史日志。
/// </summary>
public sealed class ReceiverHost : IBackend
{
    private readonly Receiver _receiver = new();
    private readonly Discovery _discovery = new();
    private readonly System.Threading.Timer _reconnectTimer;
    private readonly SemaphoreSlim _opLock = new(1, 1);
    private readonly BehaviorSubject<StatusDto> _state;
    private readonly ReplaySubject<string> _logs = new(120);

    private string _targetIp = string.Empty;
    private int _deviceIndex = -1;
    private LatencyProfile _profile = LatencyProfile.Balanced;

    public ReceiverHost()
    {
        _state = new BehaviorSubject<StatusDto>(CaptureStatus());

        _receiver.Log += EmitLog;
        _receiver.StateChanged += PublishState;
        _receiver.Disconnected += OnPeerDisconnected;

        _discovery.Log += EmitLog;
        _discovery.Changed += PublishState;
        _discovery.Start();

        _reconnectTimer = new System.Threading.Timer(
            _ => _ = ReconnectTickAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public IObservable<StatusDto> StateStream => _state;
    public IObservable<string> LogStream => _logs;

    private void EmitLog(string line) => _logs.OnNext(line);

    private void PublishState()
    {
        var snapshot = CaptureStatus();
        if (!_state.IsDisposed) _state.OnNext(snapshot);
    }

    private StatusDto CaptureStatus() => new()
    {
        Connected = _receiver.Connected,
        WantReceiving = WantReceiving,
        LastError = _receiver.LastError,
        PhoneIp = _receiver.PhoneIp,
        ServerName = _receiver.ServerName,
        ServerVersion = _receiver.ServerVersion,
        DeviceName = _receiver.DeviceName,
        Frames = _receiver.Frames,
        Fps = _receiver.Fps,
        Kbps = _receiver.Kbps,
        LossPercent = _receiver.LossPercent,
        LostFrames = _receiver.LostFrames,
        OutOfOrderCount = _receiver.OutOfOrderCount,
        CatchUpCount = _receiver.CatchUpCount,
        BufferedMs = _receiver.BufferedMs,
        TargetMs = _receiver.TargetMs,
        JitterMs = _receiver.JitterMs,
        UnderrunMs = _receiver.UnderrunMs,
        UnderrunCount = _receiver.UnderrunCount,
        MaxReadFrames = _receiver.MaxReadFrames,
        AvailableAtUnderrun = _receiver.AvailableAtUnderrun,
        DroppedMs = _receiver.DroppedMs,
        AveragePlaybackRate = _receiver.AveragePlaybackRate,
        Level = _receiver.Level,
        GainDb = _receiver.GainDb,
        Profile = _profile.ToString().ToLowerInvariant(),
        Servers = _discovery.Servers.Select(s => new ServerDto
        {
            Name = s.Name, Ip = s.Ip, Version = s.Version,
            OsVersion = s.OsVersion, Os = s.Os
        }).ToList()
    };

    public bool Connected => _receiver.Connected;
    public bool WantReceiving { get; private set; }
    public string LastError => _receiver.LastError;
    public string PhoneIp => _receiver.PhoneIp;
    public string ServerName => _receiver.ServerName;
    public string ServerVersion => _receiver.ServerVersion;
    public string DeviceName => _receiver.DeviceName;
    public long Frames => _receiver.Frames;
    public double Fps => _receiver.Fps;
    public double Kbps => _receiver.Kbps;
    public double LossPercent => _receiver.LossPercent;
    public long LostFrames => _receiver.LostFrames;
    public long OutOfOrderCount => _receiver.OutOfOrderCount;
    public long CatchUpCount => _receiver.CatchUpCount;
    public int BufferedMs => _receiver.BufferedMs;
    public int TargetMs => _receiver.TargetMs;
    public double JitterMs => _receiver.JitterMs;
    public double UnderrunMs => _receiver.UnderrunMs;
    public long UnderrunCount => _receiver.UnderrunCount;
    public int MaxReadFrames => _receiver.MaxReadFrames;
    public double AvailableAtUnderrun => _receiver.AvailableAtUnderrun;
    public double DroppedMs => _receiver.DroppedMs;
    public double AveragePlaybackRate => _receiver.AveragePlaybackRate;
    public float Level => _receiver.Level;

    public LatencyProfile Profile
    {
        get => _profile;
        set
        {
            _profile = value;
            _receiver.Profile = value;
        }
    }

    public double GainDb
    {
        get => _receiver.GainDb;
        set
        {
            _receiver.GainDb = value;
            PublishState();
        }
    }

    public List<Protocol.DiscoveredServer> Servers => _discovery.Servers.ToList();

    public List<(int Index, string Name)> GetDevices() => AudioPlayer.GetRenderDevices();

    /// <summary>按名字找虚拟声卡；找不到返回 -1 交给驱动层用默认设备。</summary>
    public int AutoPickDevice()
    {
        var devices = GetDevices();
        var match = devices.FirstOrDefault(d =>
            d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase));
        if (match.Name is not null) return match.Index;
        var vb = devices.FirstOrDefault(d =>
            d.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase));
        return vb.Name is not null ? vb.Index : -1;
    }

    public async Task StartAsync(string ip, int deviceIndex)
    {
        await _opLock.WaitAsync();
        try
        {
            if (deviceIndex < 0) deviceIndex = AutoPickDevice();
            _targetIp = ip;
            _deviceIndex = deviceIndex;
            _receiver.Profile = _profile;
            WantReceiving = true;
            await _receiver.StartAsync(ip, deviceIndex);
            if (!_receiver.Connected)
            {
                _reconnectTimer.Change(2000, Timeout.Infinite);
            }
        }
        finally
        {
            _opLock.Release();
        }
        PublishState();
    }

    public async Task StopAsync()
    {
        await _opLock.WaitAsync();
        try
        {
            WantReceiving = false;
            _reconnectTimer.Change(Timeout.Infinite, Timeout.Infinite);
            await _receiver.StopAsync();
        }
        finally
        {
            _opLock.Release();
        }
        PublishState();
    }

    private void OnPeerDisconnected()
    {
        if (!WantReceiving) return;
        EmitLog($"{DateTime.Now:HH:mm:ss} 手机端已断开，正在自动重连…");
        _reconnectTimer.Change(2000, Timeout.Infinite);
    }

    private async Task ReconnectTickAsync()
    {
        // 重连耗时可能超过一个计时周期，必须靠 _opLock 串行化，
        // 否则后一次 StartAsync 里的 StopAsync 会把前一次正在进行的连接取消掉。
        if (!await _opLock.WaitAsync(0)) return;
        try
        {
            if (!WantReceiving || _receiver.Connected) return;
            if (_targetIp.Length == 0) return;
            await _receiver.StartAsync(_targetIp, _deviceIndex);
        }
        finally
        {
            _opLock.Release();
        }
        if (WantReceiving && !_receiver.Connected)
        {
            _reconnectTimer.Change(2000, Timeout.Infinite);
        }
        PublishState();
    }

    public void Dispose()
    {
        try { _reconnectTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        try { _reconnectTimer.Dispose(); } catch { }
        try { _discovery.Dispose(); } catch { }
        try { _receiver.Dispose(); } catch { }
        try { _opLock.Dispose(); } catch { }
    }
}
