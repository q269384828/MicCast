using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace MicCastReceiver;

/// <summary>
/// 连接手机端服务端：TCP 59100 握手保活，UDP 59100 收 PCM 音频。
/// </summary>
public sealed class Receiver : IDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private readonly AudioPlayer _player = new();
    private readonly List<byte> _rx = new();
    private readonly Stopwatch _sinceLastRate = Stopwatch.StartNew();
    private long _framesAtLastRate;
    private long _bytesAtLastRate;
    private int _deviceIndex = -1;
    private bool _playerStarted;
    private volatile bool _connected;
    private readonly object _playerGate = new();

    public string PhoneIp { get; private set; } = string.Empty;
    public string ServerName { get; private set; } = string.Empty;
    public string ServerVersion { get; private set; } = string.Empty;
    // 不能用 TcpClient.Connected —— 它只反映最后一次 I/O 的结果，对端关掉后仍会返回 true
    public bool Connected => _connected;
    public long Frames { get; private set; }
    public long Bytes { get; private set; }
    public double Fps { get; private set; }
    public double Kbps { get; private set; }
    public float Level => _player.Level;
    public int BufferedMs => _player.BufferedMilliseconds;
    public int TargetMs => _player.TargetMilliseconds;
    public double JitterMs => _player.JitterMilliseconds;
    public long CatchUpCount => _player.CatchUpCount;
    public double DroppedMs => _player.DroppedMilliseconds;
    public long UnderrunCount => _player.UnderrunCount;
    public double UnderrunMs => _player.UnderrunMilliseconds;
    public double PlaybackRate => _player.PlaybackRate;
    public double AveragePlaybackRate => _player.AveragePlaybackRate;
    public string DeviceName => _player.LastDeviceName;

    public LatencyProfile Profile
    {
        get => _player.Profile;
        set => _player.Profile = value;
    }
    public string LastError { get; private set; } = string.Empty;
    public event Action<string>? Log;
    public event Action? StateChanged;
    /// <summary>对端主动断开或网络中断（不含本地 StopAsync）。</summary>
    public event Action? Disconnected;

    private void Emit(string message) => Log?.Invoke($"{DateTime.Now:HH:mm:ss} {message}");

    public List<(int Index, string Name)> GetDevices() => AudioPlayer.GetRenderDevices();

    public async Task StartAsync(string phoneIp, int deviceIndex)
    {
        await StopAsync();
        PhoneIp = phoneIp;
        LastError = string.Empty;
        Frames = 0;
        Bytes = 0;
        _rx.Clear();
        _deviceIndex = deviceIndex;
        _playerStarted = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // 播放器延后到收到 ServerAudioConfigResponse 再启动 —— 采样率/声道数以服务端为准。
        // 同时起一个兜底定时器，万一对端没下发配置也不至于一直没声音。

        // UDP 先绑定，避免握手完成后手机立刻推流而丢前几帧
        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.ReceiveBufferSize = 512 * 1024;
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.AudioPort));
            Emit($"UDP 监听 {Protocol.AudioPort}");
        }
        catch (Exception ex)
        {
            LastError = $"绑定 UDP {Protocol.AudioPort} 失败: {ex.Message}";
            Emit(LastError);
            await StopAsync();
            return;
        }

        try
        {
            _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(phoneIp, Protocol.ControlPort, token);
            _stream = _tcp.GetStream();
            _connected = true;
            Emit($"TCP 已连接 {phoneIp}:{Protocol.ControlPort}");
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            LastError = $"连接手机失败: {ex.Message}";
            Emit(LastError);
            await StopAsync();
            return;
        }

        _ = Task.Run(() => ReadLoopAsync(token), token);
        _ = Task.Run(() => UdpLoopAsync(token), token);
        _ = Task.Run(() => StartPlayerFallbackAsync(token), token);
        _ = Task.Run(() => RateLoopAsync(token), token);
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        var buffer = new byte[8192];
        int handshake = 0;
        try
        {
            while (!token.IsCancellationRequested && _stream != null)
            {
                int read = await _stream.ReadAsync(buffer, token);
                if (read <= 0) break;
                lock (_rx) _rx.AddRange(buffer.Take(read));
                while (true)
                {
                    byte[]? frame;
                    int consumed;
                    lock (_rx)
                    {
                        frame = Pb.TryTakeFrame(_rx, out consumed);
                        if (frame != null) _rx.RemoveRange(0, consumed);
                    }
                    if (frame is null) break;

                    if (handshake == 0)
                    {
                        var fields = Pb.ReadFields(frame);
                        ServerName = Pb.GetString(fields, 1) ?? "MicCast";
                        ServerVersion = Pb.GetString(fields, 2) ?? "?";
                        Emit($"服务端: {ServerName} v{ServerVersion}");
                        await SendAsync(Protocol.BuildClientConfig(), token);
                        Emit("已发送 ProtoClientConfig");
                        handshake = 1;
                    }
                    else if (handshake == 1)
                    {
                        var featureFields = Pb.ReadFields(frame)
                            .Where(f => f.Bytes != null).Select(f => f.Number).ToList();
                        Emit($"特性协商: [{string.Join(",", featureFields)}]");
                        await SendAsync(Protocol.BuildServerAudioConfigRequest(), token);
                        handshake = 2;
                        StateChanged?.Invoke();
                    }
                    else
                    {
                        var fields = Pb.ReadFields(frame);
                        int kind = fields.Count > 0 ? fields[0].Number : -1;
                        if (kind == 21)
                        {
                            var inner = Pb.ReadFields(fields[0].Bytes ?? Array.Empty<byte>());
                            await SendAsync(Protocol.BuildPingResponse((long)Pb.GetVarint(inner, 1)), token);
                        }
                        else if (kind == 24)
                        {
                            // ServerAudioConfigResponse: field 1 = {1: sampleRate, 2: bits, 3: channels}
                            int rate = 0, bits = 0, ch = 0;
                            var conf = Pb.ReadFields(fields[0].Bytes ?? Array.Empty<byte>());
                            var inner = conf.FirstOrDefault(f => f.Number == 1 && f.Bytes != null);
                            if (inner?.Bytes != null)
                            {
                                var c = Pb.ReadFields(inner.Bytes);
                                rate = (int)Pb.GetVarint(c, 1);
                                bits = (int)Pb.GetVarint(c, 2);
                                ch = (int)Pb.GetVarint(c, 3);
                            }
                            if (rate > 0 && bits > 0 && ch > 0)
                            {
                                Emit($"服务端音频配置: {rate}Hz / {bits}bit / {ch}ch");
                                StartPlayer(rate, bits, ch);
                            }
                            else
                            {
                                Emit("服务端音频配置解析失败，沿用默认参数");
                                StartPlayer(Protocol.SampleRate, Protocol.Bits, Protocol.Channels);
                            }
                        }
                    }
                }
            }
            Emit("TCP 读取结束");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LastError = $"TCP 异常: {ex.Message}";
            Emit(LastError);
        }
        finally
        {
            _connected = false;
            StateChanged?.Invoke();
            // 主动 StopAsync 会先 Cancel，此时不该触发重连
            if (!token.IsCancellationRequested)
            {
                Disconnected?.Invoke();
            }
        }
    }

    private async Task SendAsync(byte[] message, CancellationToken token)
    {
        var stream = _stream;
        if (stream is null) return;
        try
        {
            await stream.WriteAsync(Pb.Delimit(message), token);
        }
        catch (Exception ex)
        {
            Emit($"发送失败: {ex.Message}");
        }
    }

    private async Task UdpLoopAsync(CancellationToken token)
    {
        var udp = _udp;
        if (udp is null) return;
        try
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult result = await udp.ReceiveAsync(token);
                if (!result.RemoteEndPoint.Address.Equals(IPAddress.Parse(PhoneIp))) continue;
                var data = Protocol.ParseStreamData(result.Buffer);
                if (data is null) continue;

                // 收到音频但播放器还没起（配置包丢了），立刻按默认参数起播，别一直静音
                if (!_playerStarted)
                {
                    StartPlayer(Protocol.SampleRate, Protocol.Bits, Protocol.Channels);
                }

                Frames++;
                Bytes += data.Payload.Length;
                _player.Write(data.Payload, data.Tick);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Emit($"UDP 异常: {ex.Message}");
        }
    }

    /// <summary>启动播放器（幂等）。参数以服务端下发为准，避免变调。</summary>
    private void StartPlayer(int sampleRate, int bits, int channels)
    {
        lock (_playerGate)
        {
            if (_playerStarted) return;
            _playerStarted = true;
            try
            {
                _player.Start(_deviceIndex, sampleRate, bits, channels);
                Emit($"播放设备: {_player.LastDeviceName}（{sampleRate}Hz/{bits}bit/{channels}ch）");
            }
            catch (Exception ex)
            {
                LastError = $"启动播放失败: {ex.Message}";
                Emit(LastError);
            }
        }
    }

    /// <summary>对端没下发音频配置时的兜底，1.5 秒后按默认参数起播，避免一直没声音。</summary>
    private async Task StartPlayerFallbackAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(1500, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!_playerStarted)
        {
            Emit("未收到服务端音频配置，按默认参数起播");
            StartPlayer(Protocol.SampleRate, Protocol.Bits, Protocol.Channels);
        }
    }

    private async Task RateLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(500, token);
                var elapsed = _sinceLastRate.Elapsed.TotalSeconds;
                long deltaFrames = Frames - _framesAtLastRate;
                long deltaBytes = Bytes - _bytesAtLastRate;
                _framesAtLastRate = Frames;
                _bytesAtLastRate = Bytes;
                if (elapsed > 0.05)
                {
                    Fps = deltaFrames / elapsed;
                    // 按实际收到的载荷字节算，不要假设每帧多少字节（采样率/声道数会变）
                    Kbps = deltaBytes * 8 / 1000.0 / elapsed;
                }
                _sinceLastRate.Restart();
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task StopAsync()
    {
        _connected = false;
        try { _cts?.Cancel(); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Close(); } catch { }
        try { _udp?.Close(); } catch { }
        _stream = null;
        _tcp = null;
        _udp = null;
        lock (_playerGate)
        {
            _playerStarted = false;
        }
        _player.Stop();
        Fps = 0;
        Kbps = 0;
        await Task.Delay(50);
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        _stream?.Dispose();
        _tcp?.Dispose();
        _udp?.Dispose();
        _player.Dispose();
        _cts?.Dispose();
    }
}
