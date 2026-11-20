using System.Diagnostics;
using NAudio.Wave;

namespace MicCastReceiver;

/// <summary>
/// 自适应抖动缓冲 + 变速追平（NetEq 思路的轻量实现）。
///
/// 关键点：追平**不丢弃**音频，而是微调播放速率（±6% 以内）。
///   缓冲偏多 → 播快一点点，几秒内自然追回
///   缓冲偏少 → 播慢一点点，把量攒回来
///   速率变化 &lt;6%，音调偏移人耳基本无感（语音尤其如此）
///
/// 只有缓冲真的溢出/见底时才做硬处理（丢最旧的 / 补静音），那是兜底而非常规手段。
/// </summary>
public sealed class JitterBuffer : IWaveProvider
{
    private readonly object _gate = new();
    private readonly byte[] _ring;
    private readonly int _blockAlign;
    private readonly double _framesPerMs;
    private readonly int _capacityFrames;
    private readonly int _minTargetMs;
    private readonly int _maxTargetMs;

    /// <summary>最大速率偏移。6% 时音调变化不到一个半音，语音听不出来。</summary>
    private const double MaxRateDeviation = 0.06;
    /// <summary>水位偏差多少毫秒时达到最大速率偏移</summary>
    private const double RateFullScaleMs = 100.0;

    private int _writeFrame;
    private int _readFrame;
    private double _readFrac;
    private int _filledFrames;

    private double _lastArrivalMs;
    private long _lastTick = -1;
    private double _jitterMs;
    private int _frameCount;
    private int _targetMs;

    private long _overflows;
    private double _droppedMs;
    private long _underruns;
    private long _underrunBytes;
    private double _underrunPenaltyMs;
    private double _rate = 1.0;
    private double _rateSum;
    private long _rateSamples;

    public WaveFormat WaveFormat { get; }

    public JitterBuffer(WaveFormat format, int minTargetMs = 20, int maxTargetMs = 120,
                        int capacityMs = 500)
    {
        WaveFormat = format;
        _blockAlign = format.BlockAlign;
        _framesPerMs = format.SampleRate / 1000.0;
        _minTargetMs = minTargetMs;
        _maxTargetMs = maxTargetMs;
        _targetMs = minTargetMs;

        int capacityFrames = (int)(capacityMs * _framesPerMs);
        _capacityFrames = Math.Max(capacityFrames, 64);
        _ring = new byte[_capacityFrames * _blockAlign];
    }

    public int TargetMilliseconds => _targetMs;
    public double JitterMilliseconds => _jitterMs;
    /// <summary>缓冲溢出导致的硬丢弃次数（正常情况下应接近 0）</summary>
    public long CatchUpCount => _overflows;
    public double DroppedMilliseconds => _droppedMs;
    public long UnderrunCount => _underruns;
    public double UnderrunMilliseconds =>
        WaveFormat.AverageBytesPerSecond > 0
            ? _underrunBytes * 1000.0 / WaveFormat.AverageBytesPerSecond
            : 0;
    public double CurrentRate => _rate;
    /// <summary>平均播放速率，1.0 表示与实时同步</summary>
    public double AverageRate => _rateSamples > 0 ? _rateSum / _rateSamples : 1.0;

    public double BufferedMilliseconds
    {
        get { lock (_gate) return _filledFrames / _framesPerMs; }
    }

    public int BufferedBytes
    {
        get { lock (_gate) return _filledFrames * _blockAlign; }
    }

    /// <summary>写入一帧 PCM。tick 用于估算网络抖动（相邻帧 tick 差 × 帧时长 = 期望到达间隔）。</summary>
    public void Write(byte[] pcm, long tick)
    {
        double nowMs = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency * 1000.0;
        lock (_gate)
        {
            UpdateJitter(nowMs, tick);
            Append(pcm);
        }
    }

    private void UpdateJitter(double nowMs, long tick)
    {
        if (_lastArrivalMs > 0 && _lastTick >= 0 && tick > _lastTick)
        {
            double expectedMs = (tick - _lastTick) * Protocol.FrameMs;
            double actualMs = nowMs - _lastArrivalMs;
            double deviation = Math.Abs(actualMs - expectedMs);
            // RFC 3550 的指数平滑，收敛快又不会被单个尖刺带跑
            _jitterMs += (deviation - _jitterMs) / 16.0;
        }
        _lastArrivalMs = nowMs;
        _lastTick = tick;
        _frameCount++;

        if (_frameCount < 20) return;

        int desired = (int)Math.Clamp(
            _minTargetMs + _underrunPenaltyMs + 3.0 * _jitterMs,
            _minTargetMs, _maxTargetMs);

        // 欠载惩罚缓慢衰减：真断续过就保守一阵子，网络稳了再慢慢降回来
        _underrunPenaltyMs = Math.Max(0, _underrunPenaltyMs - 0.05);

        if (desired > _targetMs)
        {
            _targetMs = Math.Min(desired, _targetMs + 5);
        }
        else
        {
            _targetMs = Math.Max(desired, _targetMs - 1);
        }
    }

    private void Append(byte[] data)
    {
        int frames = data.Length / _blockAlign;
        int offset = 0;

        for (int i = 0; i < frames; i++)
        {
            if (_filledFrames >= _capacityFrames)
            {
                // 环满：硬丢最旧的一帧并记录（正常靠速率微调不会走到这里）
                AdvanceReadFrame();
                _overflows++;
                _droppedMs += 1.0 / _framesPerMs;
            }

            Buffer.BlockCopy(data, offset, _ring, _writeFrame * _blockAlign, _blockAlign);
            _writeFrame = (_writeFrame + 1) % _capacityFrames;
            _filledFrames++;
            offset += _blockAlign;
        }
    }

    private void AdvanceReadFrame()
    {
        _readFrame = (_readFrame + 1) % _capacityFrames;
        if (_filledFrames > 0) _filledFrames--;
    }

    /// <summary>
    /// 按水位偏差算播放速率：缓冲多就播快，少就播慢。
    /// 幅度限制在 ±6%，一秒能吃掉/补回约 60ms 的偏差。
    /// </summary>
    private double ComputeRate()
    {
        double targetFrames = _targetMs * _framesPerMs;
        double diffMs = (_filledFrames - targetFrames) / _framesPerMs;
        double normalized = Math.Clamp(diffMs / RateFullScaleMs, -1.0, 1.0);
        return 1.0 + normalized * MaxRateDeviation;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            int outFrames = count / _blockAlign;
            if (outFrames <= 0) return count;

            _rate = _frameCount < 20 ? 1.0 : ComputeRate();
            _rateSum += _rate;
            _rateSamples++;

            int produced = 0;
            for (; produced < outFrames; produced++)
            {
                if (_filledFrames < 2) break;

                int srcA = _readFrame * _blockAlign;
                int srcB = ((_readFrame + 1) % _capacityFrames) * _blockAlign;
                double t = _readFrac;
                int dst = offset + produced * _blockAlign;

                // 逐采样线性插值（16bit）
                for (int b = 0; b + 1 < _blockAlign; b += 2)
                {
                    short s0 = (short)(_ring[srcA + b] | (_ring[srcA + b + 1] << 8));
                    short s1 = (short)(_ring[srcB + b] | (_ring[srcB + b + 1] << 8));
                    short v = (short)(s0 + (s1 - s0) * t);
                    buffer[dst + b] = (byte)(v & 0xFF);
                    buffer[dst + b + 1] = (byte)((v >> 8) & 0xFF);
                }

                _readFrac += _rate;
                while (_readFrac >= 1.0)
                {
                    _readFrac -= 1.0;
                    AdvanceReadFrame();
                }
            }

            if (produced < outFrames)
            {
                // 欠载：补静音。IWaveProvider 必须返回完整 count，否则 WasapiOut 会以为流结束。
                int gapBytes = (outFrames - produced) * _blockAlign;
                Array.Clear(buffer, offset + produced * _blockAlign, gapBytes);
                _underruns++;
                _underrunBytes += gapBytes;
                // 记录一次欠载就抬高目标水位，下一帧起生效
                _underrunPenaltyMs = Math.Min(80, _underrunPenaltyMs + 3);
            }

            return count;
        }
    }

    /// <summary>
    /// 预填充静音。启动播放前调用，让缓冲先有量，
    /// 否则 WasapiOut 一开始就读会立刻欠载几十次（听起来是一声爆音）。
    /// </summary>
    public void PrimeSilence(int milliseconds)
    {
        lock (_gate)
        {
            int frames = (int)(milliseconds * _framesPerMs);
            for (int i = 0; i < frames && _filledFrames < _capacityFrames; i++)
            {
                Array.Clear(_ring, _writeFrame * _blockAlign, _blockAlign);
                _writeFrame = (_writeFrame + 1) % _capacityFrames;
                _filledFrames++;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _writeFrame = 0;
            _readFrame = 0;
            _readFrac = 0;
            _filledFrames = 0;
            _jitterMs = 0;
            _frameCount = 0;
            _targetMs = _minTargetMs;
            _lastTick = -1;
            _lastArrivalMs = 0;
            _overflows = 0;
            _droppedMs = 0;
            _underruns = 0;
            _underrunBytes = 0;
            _underrunPenaltyMs = 0;
            _rate = 1.0;
            _rateSum = 0;
            _rateSamples = 0;
        }
    }
}
