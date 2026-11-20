namespace MicCastReceiver;

/// <summary>
/// 接收逻辑的统一门面。属性与 <see cref="Receiver"/> 同名同义，
/// 这样图形界面既可以直连进程内宿主，也可以走命名管道连另一个进程里的宿主，
/// 调用方代码不用区分自己连的是本地还是远端。
/// </summary>
public interface IBackend : IDisposable
{
    bool Connected { get; }
    bool WantReceiving { get; }
    string LastError { get; }
    string PhoneIp { get; }
    string ServerName { get; }
    string ServerVersion { get; }
    string DeviceName { get; }
    long Frames { get; }
    double Fps { get; }
    double Kbps { get; }
    double LossPercent { get; }
    long LostFrames { get; }
    long OutOfOrderCount { get; }
    long CatchUpCount { get; }
    int BufferedMs { get; }
    int TargetMs { get; }
    double JitterMs { get; }
    double UnderrunMs { get; }
    long UnderrunCount { get; }
    double DroppedMs { get; }
    double AveragePlaybackRate { get; }
    float Level { get; }
    LatencyProfile Profile { get; set; }

    /// <summary>自动发现到的手机，由宿主统一维护，避免多进程重复监听 UDP 59200。</summary>
    List<Protocol.DiscoveredServer> Servers { get; }

    /// <summary>
    /// 状态流。BehaviorSubject 语义：**后订阅的也能立刻拿到当前值**，
    /// 所以界面不必关心自己比后台早开还是晚开，也不会出现
    /// 「只同步了第一帧、之后再也不更新」这类时序问题。
    /// </summary>
    IObservable<StatusDto> StateStream { get; }

    /// <summary>
    /// 日志流。ReplaySubject 语义：**重放最近 N 条**，
    /// 所以后连上来的界面也能看到之前发生了什么，而不是一片空白。
    /// </summary>
    IObservable<string> LogStream { get; }

    List<(int Index, string Name)> GetDevices();
    Task StartAsync(string ip, int deviceIndex);
    Task StopAsync();
}
