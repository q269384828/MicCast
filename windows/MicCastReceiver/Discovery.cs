using System.Net;
using System.Net.Sockets;

namespace MicCastReceiver;

/// <summary>
/// 监听手机端每 2 秒发出的发现广播（UDP 59200，格式为 int32 LE 长度 + ProtoServerConfig），
/// 拿到设备名与来源 IP，供 UI 直接列出可连接的手机。
/// </summary>
public sealed class Discovery : IDisposable
{
    /// <summary>手机端广播间隔 2s，超过这个时长没再收到就认为设备下线</summary>
    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(8);

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, Protocol.DiscoveredServer> _servers = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;
    public event Action<string>? Log;

    public bool Running => _udp is not null;

    public IReadOnlyList<Protocol.DiscoveredServer> Servers
    {
        get
        {
            lock (_servers) return _servers.Values.OrderBy(s => s.Name).ToList();
        }
    }

    public void Start()
    {
        if (_udp is not null) return;
        try
        {
            var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.ExclusiveAddressUse = false;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.DiscoveryPort));
            _udp = udp;
            _cts = new CancellationTokenSource();
            Log?.Invoke($"监听 UDP {Protocol.DiscoveryPort}，等待手机广播…");
            _ = Task.Run(() => LoopAsync(udp, _cts.Token));
            _ = Task.Run(() => PruneLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            Log?.Invoke($"发现端口 {Protocol.DiscoveryPort} 监听失败: {ex.Message}");
            Stop();
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _udp = null;
        _cts?.Dispose();
        _cts = null;
        lock (_servers) _servers.Clear();
    }

    private async Task LoopAsync(UdpClient udp, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(token);
                var ip = result.RemoteEndPoint.Address.ToString();
                var server = Protocol.ParseDiscoveryDatagram(result.Buffer, ip);
                if (server is null) continue;

                bool isNew;
                lock (_servers)
                {
                    isNew = !_servers.ContainsKey(ip);
                    _servers[ip] = server;
                }
                if (isNew)
                {
                    Log?.Invoke($"发现设备: {server.Name} @ {ip}");
                    Changed?.Invoke();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Log?.Invoke($"发现监听异常: {ex.Message}");
        }
    }

    private async Task PruneLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(2000, token);
                bool removed = false;
                lock (_servers)
                {
                    var now = DateTime.Now;
                    foreach (var key in _servers.Where(kv => now - kv.Value.LastSeen > Expiry).Select(kv => kv.Key).ToList())
                    {
                        _servers.Remove(key);
                        removed = true;
                    }
                }
                if (removed) Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        Stop();
    }
}
