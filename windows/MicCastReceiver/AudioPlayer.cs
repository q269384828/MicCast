using NAudio.Wave;

namespace MicCastReceiver;

/// <summary>把收到的 PCM 写入指定播放设备。写进虚拟声卡（如 CABLE Input）后，会议软件即可从对应录音端采集。</summary>
public sealed class AudioPlayer : IDisposable
{
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private readonly object _gate = new();

    public int BufferMilliseconds { get; set; } = 120;
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

    public void Start(int deviceIndex)
    {
        lock (_gate)
        {
            StopInternal();
            var format = new WaveFormat(Protocol.SampleRate, Protocol.Bits, Protocol.Channels);
            _buffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromMilliseconds(600),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

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
                    _output = new WasapiOut(mmDevice, NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 50);
                }
            }
            catch
            {
                _output = null;
            }

            if (_output is null)
            {
                _output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, true, 50);
                deviceName = "默认设备";
            }

            _output.Init(_buffer);
            _output.Play();
            LastDeviceName = deviceName ?? "默认设备";
        }
    }

    public string LastDeviceName { get; private set; } = string.Empty;

    public void Write(byte[] pcm)
    {
        lock (_gate)
        {
            if (_buffer is null) return;
            _buffer.AddSamples(pcm, 0, pcm.Length);
            Level = ComputeRms(pcm);
        }
    }

    public int BufferedMilliseconds
    {
        get
        {
            lock (_gate)
            {
                if (_buffer is null) return 0;
                return (int)(_buffer.BufferedBytes / (double)(Protocol.SampleRate * Protocol.Channels * (Protocol.Bits / 8)) * 1000);
            }
        }
    }

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
