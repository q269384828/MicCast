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
        LatencyProfile.Low => (15, 60, 15),
        LatencyProfile.Stable => (50, 250, 40),
        // Balanced：min 依次 15 → 28 → 42。
        // 实测 min=28 时系统还得靠欠载惩罚把 target 顶到 ~50 才稳，
        // 等于启动后先撞几十次墙再自学；直接把起点放到 42 省掉这段。
        // 多 14ms 延迟在语音里听不出来。
        _ => (42, 120, 20)
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
            // 垫一段静音当启动缓冲，但先不起播：
            // 协议协商完成时手机端的采集器往往还没出第一帧，这时 Play 只会
            // 对着空缓冲连续欠载（实测能积 1 秒多），把自适应水位一路顶到上限。
            // 起播推迟到 Write 收到首个音频包（见 Write）。
            int primeMs = p.MinTarget + p.WasapiLatency;
            _buffer.PrimeSilence(primeMs);
            _buffer.SetTarget(primeMs);
            _primed = true;
            LastDeviceName = deviceName ?? "默认设备";
            SampleRate = sampleRate;
            Bits = bits;
            Channels = channels;
        }
    }

    private bool _primed;

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

            // 首个真实音频包到达才起播，避免对着空缓冲干读
            if (_primed)
            {
                _primed = false;
                _output?.Play();
            }
        }
    }

    private volatile float _gainFactor = 1f;
    private double _gainDb;

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

    /// <summary>诊断：WasapiOut 一次 Read 的最大帧数（× 1/16 ms = 单次拉取跨度）。</summary>
    public int MaxReadFrames => _buffer?.MaxReadFrames ?? 0;

    /// <summary>诊断：欠载时缓冲里平均剩余的帧数。</summary>
    public double AverageAvailableAtUnderrun => _buffer?.AverageAvailableAtUnderrun ?? 0;
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
        _primed = false;
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
