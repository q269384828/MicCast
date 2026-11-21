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
            byte[] processed = ApplyProcessing(pcm);
            _buffer.Write(processed, tick);
            // 电平跟着实际送进声卡的数据走：静音时界面应该显示 0，
            // 而不是继续显示麦克风原来的音量。
            Level = ComputeRms(processed);
        }
    }

    private volatile bool _muted;
    private volatile float _gainFactor = 1f;
    private double _gainDb;

    /// <summary>静音：仍然写入等长静音帧，保持 tick 连续，避免缓冲欠载。</summary>
    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    /// <summary>软件增益，单位 dB，范围建议 -12 ~ +12。0 表示原样输出。</summary>
    public double GainDb
    {
        get => _gainDb;
        set
        {
            _gainDb = value;
            _gainFactor = (float)Math.Pow(10, value / 20.0);
        }
    }

    private byte[] ApplyProcessing(byte[] pcm)
    {
        if (_muted) return new byte[pcm.Length];
        if (Math.Abs(_gainDb) < 0.01) return pcm;

        float factor = _gainFactor;
        var outBuf = new byte[pcm.Length];
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            short sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            int scaled = (int)(sample * factor);
            if (scaled > short.MaxValue) scaled = short.MaxValue;
            else if (scaled < short.MinValue) scaled = short.MinValue;
            outBuf[i] = (byte)(scaled & 0xFF);
            outBuf[i + 1] = (byte)((scaled >> 8) & 0xFF);
        }
        return outBuf;
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
