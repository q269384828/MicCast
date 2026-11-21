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
    private int _minTargetMs;
    private readonly int _maxTargetMs;

    /// <summary>最大速率偏移。6% 时音调变化不到一个半音，语音听不出来。</summary>
    private const double MaxRateDeviation = 0.06;
    /// <summary>水位偏差多少毫秒时达到最大速率偏移</summary>
    // 变速追平的灵敏度：缓冲水位偏离目标多少毫秒时达到满功率。
    //
    // 原值 100ms 太钝：手机端采集时钟实际比名义慢 ~1.5%（fps 197 vs 200），
    // 按 100ms 满量程要把缓冲压到目标以下 25ms 才能产生 1.5% 的补偿，
    // 而目标水位只有 32ms —— 缓冲被压到 7ms，任何抖动都直接欠载，
    // 实测每秒丢 8ms 音频（0.83%）。
    //
    // 改成 25ms 后，产生 1.5% 补偿只需 6ms 水位偏差，缓冲仍留 ~26ms 余量。
    // 不会引入可闻的变速痕迹：Opus/WebRTC 的 NetEq 用的是同一量级的响应速度。
    private const double RateFullScaleMs = 25.0;

    private int _writeFrame;
    private int _readFrame;
    private double _readFrac;
    private int _filledFrames;

    private double _lastArrivalMs;
    private long _lastTick = -1;
    private double _jitterMs;
    private int _frameCount;
    private int _targetMs;
    private int _targetDropCooldown;
    private long _underrunAvailableSum;
    private int _underrunSamples;
    private bool _targetCalibrated;

    /// <summary>诊断：一次 Read 请求的最大帧数，即 WasapiOut 的批量拉取粒度。</summary>
    public int MaxReadFrames { get; private set; }

    /// <summary>诊断：欠载发生时缓冲里平均还剩多少帧。为 0 说明是真见底了。</summary>
    public double AverageAvailableAtUnderrun =>
        _underrunSamples == 0 ? 0 : (double)_underrunAvailableSum / _underrunSamples;

    private long _overflows;
    private double _droppedMs;
    private long _underruns;
    private long _underrunBytes;
    private double _underrunPenaltyMs;
    private double _rate = 1.0;
    private double _rateSum;
    private long _rateSamples;
    private long _lostFrames;
    private long _expectedFrames;
    private long _outOfOrder;

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

    /// <summary>tick 跳号推断出的丢帧总数（UDP 层面真的没到）</summary>
    public long LostFrames => _lostFrames;
    /// <summary>网络丢包率（百分比），丢帧 / 应到帧</summary>
    public double LossPercent =>
        _expectedFrames > 0 ? _lostFrames * 100.0 / _expectedFrames : 0;
    /// <summary>乱序或重传的旧包次数，不算丢包</summary>
    public long OutOfOrderCount => _outOfOrder;

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
            // tick 跳号说明中间那些 UDP 包没到 —— 这是网络丢包，不是本地调度问题。
            // 两者都会表现为欠载，但只有丢包才需要 FEC / 重传。
            long gap = tick - _lastTick - 1;
            if (gap > 0)
            {
                _lostFrames += gap;
            }
            _expectedFrames += tick - _lastTick;

            double expectedMs = (tick - _lastTick) * Protocol.FrameMs;
            double actualMs = nowMs - _lastArrivalMs;
            double deviation = Math.Abs(actualMs - expectedMs);
            // RFC 3550 的指数平滑，收敛快又不会被单个尖刺带跑
            _jitterMs += (deviation - _jitterMs) / 16.0;
        }
        else if (_lastTick >= 0 && tick <= _lastTick)
        {
            // 乱序或重传的旧包：只计一次，不参与抖动和丢包统计
            _outOfOrder++;
            _lastArrivalMs = nowMs;
            return;
        }
        _lastArrivalMs = nowMs;
        _lastTick = tick;
        _frameCount++;

        if (_frameCount < 20) return;

        int desired = (int)Math.Clamp(
            _minTargetMs + _underrunPenaltyMs + 3.5 * _jitterMs,
            _minTargetMs, _maxTargetMs);

        // 欠载惩罚缓慢衰减：真断续过就保守一阵子，网络稳了再慢慢降回来。
        // 衰减速率必须小于「欠载发生时惩罚增长速度」，否则水位永远回落到欠载区。
        // UpdateJitter 每个音频包调用一次（约 200 次/秒），0.02 → 每秒衰减 4ms；
        // 而每秒只要欠载 1 次就是 +5ms，所以水位会被推高到真正不再欠载的位置。
        _underrunPenaltyMs = Math.Max(0, _underrunPenaltyMs - 0.02);

        if (desired > _targetMs)
        {
            _targetMs = Math.Min(desired, _targetMs + 5);
        }
        else if (--_targetDropCooldown <= 0)
        {
            // 回落比上升慢得多（每 5 个包才降 1ms ≈ 40ms/s）：
            // 从预填充水位降到稳态需要一两秒，期间若真实抖动更大，
            // desired 会立刻把它顶回去，不会一头扎进欠载区。
            _targetDropCooldown = 5;
            _targetMs = Math.Max(desired, _targetMs - 1);
        }
    }

    /// <summary>
    /// 把自适应目标对齐到给定水位。启动预填充后调用：
    /// 否则播放器发现缓冲远高于目标，会按最大速率猛吃，一两秒内既吃光又欠载。
    /// </summary>
    public void SetTarget(int milliseconds)
    {
        lock (_gate)
        {
            _targetMs = Math.Clamp(milliseconds, _minTargetMs, _maxTargetMs);
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

            if (outFrames > MaxReadFrames) MaxReadFrames = outFrames;

            if (!_targetCalibrated)
            {
                // 水位下限必须覆盖 WasapiOut 连续两次完整拉取：
                // 它起播时要连着读几次把 render buffer 填满（实测 418 帧/次 ≈ 26ms），
                // 水位只够一次的话，第二次就直接见底 —— 这是「缓冲看着有 50ms
                // 却每隔一秒欠载一次」的真正原因，跟网络抖动无关。
                _targetCalibrated = true;
                int needed = (int)Math.Ceiling(outFrames * 2 / (double)_framesPerMs) + 10;
                if (needed > _minTargetMs && needed <= _maxTargetMs)
                {
                    _minTargetMs = needed;
                    if (_targetMs < _minTargetMs) _targetMs = _minTargetMs;
                }
            }

            _rate = _frameCount < 20 ? 1.0 : ComputeRate();
            _rateSum += _rate;
            _rateSamples++;

            int produced = 0;
            for (; produced < outFrames; produced++)
            {
                if (_filledFrames < 1) break;

                int dst = offset + produced * _blockAlign;

                if (_filledFrames >= 2)
                {
                    // 正常路径：相邻两帧之间线性插值
                    int srcA = _readFrame * _blockAlign;
                    int srcB = ((_readFrame + 1) % _capacityFrames) * _blockAlign;
                    double t = _readFrac;
                    for (int b = 0; b + 1 < _blockAlign; b += 2)
                    {
                        short s0 = (short)(_ring[srcA + b] | (_ring[srcA + b + 1] << 8));
                        short s1 = (short)(_ring[srcB + b] | (_ring[srcB + b + 1] << 8));
                        short v = (short)(s0 + (s1 - s0) * t);
                        buffer[dst + b] = (byte)(v & 0xFF);
                        buffer[dst + b + 1] = (byte)((v >> 8) & 0xFF);
                    }
                }
                else
                {
                    // 只剩一帧、没有下一帧可插值：直接输出这一帧。
                    // 之前这里会整段补静音，白扔掉最后 5ms 的音频。
                    Buffer.BlockCopy(_ring, _readFrame * _blockAlign, buffer, dst, _blockAlign);
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
                _underrunAvailableSum += _filledFrames;
                _underrunSamples++;
                // 一次欠载就抬高目标水位，下一帧起生效。
                // 单次欠载通常丢掉 5ms 音频，但增长要温和 —— 否则启动瞬态那几次欠载
                // 会一口气把水位推到上限，延迟暴涨后再慢慢回落，来回振荡。
                _underrunPenaltyMs = Math.Min(60, _underrunPenaltyMs + 3);
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
            _underrunAvailableSum = 0;
            _underrunSamples = 0;
            _targetDropCooldown = 0;
            _rate = 1.0;
            _rateSum = 0;
            _rateSamples = 0;
            _lostFrames = 0;
            _expectedFrames = 0;
            _outOfOrder = 0;
        }
    }
}
