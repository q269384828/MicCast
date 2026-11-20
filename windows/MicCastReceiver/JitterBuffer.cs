using System.Diagnostics;
using NAudio.Wave;

namespace MicCastReceiver;

/// <summary>
/// 自适应抖动缓冲。
///
/// 普通的 BufferedWaveProvider 只有一个固定容量，一旦生产快于消费就会一路积压到溢出，
/// 表现出「延迟越来越大直到爆音」。这里改成按到达抖动动态设定目标水位：
///   - 抖动大 → 抬高目标，多存一点，避免断续
///   - 抖动小 → 压低目标，把延迟降下来
///   - 积压超过目标 + 迟滞阈值 → 主动丢弃多余数据快速追平
/// </summary>
public sealed class JitterBuffer : IWaveProvider
{
    private readonly object _gate = new();
    private readonly byte[] _ring;
    private readonly int _blockAlign;
    private readonly double _bytesPerMs;
    private readonly int _minTargetMs;
    private readonly int _maxTargetMs;
    private readonly int _hysteresisBytes;
    /// <summary>下游一次 Read 的字节数。追平必须给它留位置，否则刚追平就被读空。</summary>
    private readonly int _readChunkBytes;

    private int _readPos;
    private int _writePos;
    private int _filled;

    private double _lastArrivalMs;
    private long _lastTick = -1;
    private double _jitterMs;
    private int _frameCount;
    private int _targetMs;

    private long _catchUps;
    private double _droppedMs;
    private long _underruns;
    private double _underrunPenaltyMs;
    private long _underrunBytes;

    public WaveFormat WaveFormat { get; }

    /// <param name="minTargetMs">目标水位下限（指一次 Read 之后应该剩下的量）</param>
    /// <param name="maxTargetMs">目标水位上限，网络再差也不无限涨</param>
    /// <param name="capacityMs">环形缓冲区总容量</param>
    /// <param name="catchUpHysteresisMs">超过目标多少才追平，防止来回抖</param>
    /// <param name="readChunkMs">下游一次读走多少毫秒（= WasapiOut 的 latency）</param>
    public JitterBuffer(WaveFormat format, int minTargetMs = 20, int maxTargetMs = 120,
                        int capacityMs = 500, int catchUpHysteresisMs = 8, int readChunkMs = 20)
    {
        WaveFormat = format;
        _blockAlign = format.BlockAlign;
        _bytesPerMs = format.AverageBytesPerSecond / 1000.0;
        _minTargetMs = minTargetMs;
        _maxTargetMs = maxTargetMs;
        _targetMs = minTargetMs;
        _hysteresisBytes = (int)(catchUpHysteresisMs * _bytesPerMs);
        _readChunkBytes = (int)(readChunkMs * _bytesPerMs);

        int capacity = (int)(capacityMs * _bytesPerMs);
        capacity -= capacity % _blockAlign;
        _ring = new byte[Math.Max(capacity, _blockAlign * 8)];
    }

    public int TargetMilliseconds => _targetMs;
    public double JitterMilliseconds => _jitterMs;
    public long CatchUpCount => _catchUps;
    public double DroppedMilliseconds => _droppedMs;
    public long UnderrunCount => _underruns;
    /// <summary>累计欠载时长（毫秒）。判断断续严重程度看这个，光看次数会高估。</summary>
    public double UnderrunMilliseconds => _underrunBytes / _bytesPerMs;

    public int BufferedMilliseconds
    {
        get
        {
            lock (_gate)
            {
                return (int)(_filled / _bytesPerMs);
            }
        }
    }

    public int BufferedBytes
    {
        get
        {
            lock (_gate) return _filled;
        }
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

        if (_frameCount < 20)
        {
            // 预热阶段先按上限攒够，避免一开始就欠载
            return;
        }

        int desired = (int)Math.Clamp(
            _minTargetMs + _underrunPenaltyMs + 3.0 * _jitterMs,
            _minTargetMs, _maxTargetMs);

        // 欠载惩罚缓慢衰减：真断续过就保守一阵子，网络稳了再慢慢降回来
        _underrunPenaltyMs = Math.Max(0, _underrunPenaltyMs - 0.05);

        if (desired > _targetMs)
        {
            // 抖动变大时快速抬高，宁可多 20ms 也不断续
            _targetMs = Math.Min(desired, _targetMs + 5);
        }
        else
        {
            // 抖动静下来后缓慢回落，避免关键时刻忽然欠载
            _targetMs = Math.Max(desired, _targetMs - 1);
        }
    }

    private void Append(byte[] data)
    {
        int offset = 0;
        int remaining = data.Length;

        // 环满了先扔最旧的，保证新数据能进来
        while (remaining > 0 && _ring.Length - _filled < remaining)
        {
            int need = remaining - (_ring.Length - _filled);
            DropOldest(Snap(need));
        }

        while (remaining > 0)
        {
            int space = Math.Min(remaining, _ring.Length - _writePos);
            Buffer.BlockCopy(data, offset, _ring, _writePos, space);
            _writePos = (_writePos + space) % _ring.Length;
            _filled += space;
            offset += space;
            remaining -= space;
        }
    }

    private void DropOldest(int bytes)
    {
        if (bytes <= 0 || _filled <= 0) return;
        bytes = Math.Min(bytes, _filled);
        _readPos = (_readPos + bytes) % _ring.Length;
        _filled -= bytes;
        _droppedMs += bytes / _bytesPerMs;
    }

    private int Snap(int bytes)
    {
        int aligned = bytes - bytes % _blockAlign;
        return aligned > 0 ? aligned : _blockAlign;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            // 积压超过目标 + 迟滞就追平。迟滞别设太大，否则会长期停在「目标 + 迟滞」附近。
            if (_frameCount >= 20)
            {
                int keepBytes = (int)(_targetMs * _bytesPerMs) + _readChunkBytes;
                int excess = _filled - keepBytes;
                if (excess > _hysteresisBytes)
                {
                    int skip = Snap(excess);
                    _readPos = (_readPos + skip) % _ring.Length;
                    _filled -= skip;
                    _droppedMs += skip / _bytesPerMs;
                    _catchUps++;
                }
            }

            int available = Math.Min(count, _filled);
            available -= available % _blockAlign;

            int copied = 0;
            while (copied < available)
            {
                int chunk = Math.Min(available - copied, _ring.Length - _readPos);
                Buffer.BlockCopy(_ring, _readPos, buffer, offset + copied, chunk);
                _readPos = (_readPos + chunk) % _ring.Length;
                copied += chunk;
            }
            _filled -= copied;

            if (copied < count)
            {
                // 欠载，补静音。必须返回完整 count（IWaveProvider 的 ReadFully 语义），
                // 否则 WasapiOut 会以为流结束。
                int gap = count - copied;
                Array.Clear(buffer, offset + copied, gap);
                _underruns++;
                _underrunBytes += gap;
                // 记录一次欠载就抬高目标水位，下一帧起生效
                _underrunPenaltyMs = Math.Min(80, _underrunPenaltyMs + 3);
            }

            return count;
        }
    }

    /// <summary>
    /// 预填充静音。在启动播放前调用，让缓冲先有量，
    /// 否则 WasapiOut 一开始就读会立刻欠载几十次（听起来是一声爆音）。
    /// </summary>
    public void PrimeSilence(int milliseconds)
    {
        lock (_gate)
        {
            int bytes = (int)(milliseconds * _bytesPerMs);
            bytes -= bytes % _blockAlign;
            if (bytes > 0)
            {
                Append(new byte[bytes]);
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _readPos = 0;
            _writePos = 0;
            _filled = 0;
            _jitterMs = 0;
            _frameCount = 0;
            _targetMs = _minTargetMs;
            _lastTick = -1;
            _lastArrivalMs = 0;
            _catchUps = 0;
            _droppedMs = 0;
            _underruns = 0;
            _underrunBytes = 0;
            _underrunPenaltyMs = 0;
        }
    }
}
