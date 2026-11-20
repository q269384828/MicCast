using NAudio.Wave;

namespace MicCastReceiver;

public enum LatencyProfile
{
    Low,
    Balanced,
    Stable
}

/// <summary>把收到的 PCM 写入指定播放设备。写进虚拟声卡（如 CABLE Input）后，会议软件即可从对应录音端采集。</summary>
public sealed class AudioPlayer : IDisposable
{
    private WasapiOut? _output;
    private JitterBuffer? _buffer;
    private readonly object _gate = new();

    public LatencyProfile Profile { get; set; } = LatencyProfile.Balanced;
    public float Level { get; private set; }

    public static List<(int Index, string Name)> GetRenderDevices()
    {
        var list = new List<(int, string)>();
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render,
                NAudio.CoreAudioApi.DeviceState.Active);
            for (int i = 0; i < devices.Count; i++)
            {
                list.Add((i, devices[i].FriendlyName));
            }
        }
        catch
        {
            // 枚举失败时退回 WaveOut 列表
            for (int i = 0; i < WaveOut.DeviceCount; i++)
            {
                list.Add((i, WaveOut.GetCapabilities(i).ProductName));
            }
        }
        return list;
    }

    private (int MinTarget, int MaxTarget, int WasapiLatency) ProfileParams() => Profile switch
    {
        LatencyProfile.Low => (10, 50, 15),
        LatencyProfile.Stable => (50, 250, 40),
        _ => (15, 100, 20)
    };

    public void Start(int deviceIndex, int sampleRate, int bits, int channels)
    {
        lock (_gate)
        {
            StopInternal();
            var format = new WaveFormat(sampleRate, bits, channels);
            var p = ProfileParams();
            _buffer = new JitterBuffer(format, p.MinTarget, p.MaxTarget, capacityMs: 500);

            string? deviceName = null;
            try
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render,
                    NAudio.CoreAudioApi.DeviceState.Active);
                if (deviceIndex >= 0 && deviceIndex < devices.Count)
                {
                    var mmDevice = devices[deviceIndex];
                    deviceName = mmDevice.FriendlyName;
                    _output = new WasapiOut(mmDevice, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, p.WasapiLatency);
                }
            }
            catch
            {
                _output = null;
            }

            if (_output is null)
            {
                _output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, p.WasapiLatency);
                deviceName = "默认设备";
            }

            _output.Init(_buffer);
            // 先垫一段静音，避免播放起步瞬间连续欠载。
            // 多垫 30ms：WASAPI 一启动就会连着读好几次，而手机端第一批包还在路上，
            // 垫不够就会在启动阶段积一堆欠载，把自适应水位顶到上限。
            _buffer.PrimeSilence(p.MinTarget + p.WasapiLatency + 30);
            _output.Play();
            LastDeviceName = deviceName ?? "默认设备";
            SampleRate = sampleRate;
            Bits = bits;
            Channels = channels;
        }
    }

    public string LastDeviceName { get; private set; } = string.Empty;
    public int SampleRate { get; private set; } = Protocol.SampleRate;
    public int Bits { get; private set; } = Protocol.Bits;
    public int Channels { get; private set; } = Protocol.Channels;
    public bool Running => _buffer is not null;

    public void Write(byte[] pcm, long tick)
    {
        lock (_gate)
        {
            if (_buffer is null) return;
            _buffer.Write(pcm, tick);
            Level = ComputeRms(pcm);
        }
    }

    public int BufferedMilliseconds => (int)(_buffer?.BufferedMilliseconds ?? 0);
    public int TargetMilliseconds => _buffer?.TargetMilliseconds ?? 0;
    public double JitterMilliseconds => _buffer?.JitterMilliseconds ?? 0;
    public long CatchUpCount => _buffer?.CatchUpCount ?? 0;
    public double DroppedMilliseconds => _buffer?.DroppedMilliseconds ?? 0;
    public long UnderrunCount => _buffer?.UnderrunCount ?? 0;
    public double UnderrunMilliseconds => _buffer?.UnderrunMilliseconds ?? 0;
    public double PlaybackRate => _buffer?.CurrentRate ?? 1.0;
    public double AveragePlaybackRate => _buffer?.AverageRate ?? 1.0;
    public double LossPercent => _buffer?.LossPercent ?? 0;
    public long LostFrames => _buffer?.LostFrames ?? 0;
    public long OutOfOrderCount => _buffer?.OutOfOrderCount ?? 0;

    private static float ComputeRms(byte[] pcm)
    {
        if (pcm.Length < 4) return 0;
        int count = pcm.Length / 2;
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            short sample = BitConverter.ToInt16(pcm, i * 2);
            double v = sample / 32768.0;
            sum += v * v;
        }
        return (float)Math.Sqrt(sum / count);
    }

    private void StopInternal()
    {
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        _output = null;
        _buffer = null;
        Level = 0;
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopInternal();
        }
    }

    public void Dispose() => Stop();
}
