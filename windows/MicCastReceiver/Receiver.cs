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

    public string PhoneIp { get; private set; } = string.Empty;
    public string ServerName { get; private set; } = string.Empty;
    public string ServerVersion { get; private set; } = string.Empty;
    public bool Connected => _tcp?.Connected == true;
    public long Frames { get; private set; }
    public long Bytes { get; private set; }
    public double Fps { get; private set; }
    public double Kbps { get; private set; }
    public float Level => _player.Level;
    public int BufferedMs => _player.BufferedMilliseconds;
    public string DeviceName => _player.LastDeviceName;
    public string LastError { get; private set; } = string.Empty;
    public event Action<string>? Log;
    public event Action? StateChanged;

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
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _player.Start(deviceIndex);
        Emit($"播放设备: {_player.LastDeviceName}");

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
                            Emit("已收到服务端音频配置");
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
            StateChanged?.Invoke();
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
                Frames++;
                Bytes += data.Payload.Length;
                _player.Write(data.Payload);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Emit($"UDP 异常: {ex.Message}");
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
                _framesAtLastRate = Frames;
                if (elapsed > 0.05)
                {
                    Fps = deltaFrames / elapsed;
                    Kbps = deltaFrames * 960 * 8 / 1000.0 / elapsed;
                }
                _sinceLastRate.Restart();
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Close(); } catch { }
        try { _udp?.Close(); } catch { }
        _stream = null;
        _tcp = null;
        _udp = null;
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
