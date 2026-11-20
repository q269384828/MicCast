using System.Text;

namespace MicCastReceiver;

/// <summary>
/// 极简 protobuf 编解码，只覆盖本项目用到的 wire type：0(varint) / 1(64bit) / 2(length-delimited) / 5(32bit)。
/// </summary>
public static class Pb
{
    public static void WriteVarint(List<byte> buf, ulong value)
    {
        while (value > 0x7F)
        {
            buf.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        buf.Add((byte)value);
    }

    public static void WriteTag(List<byte> buf, int field, int wire) => WriteVarint(buf, (ulong)((field << 3) | wire));

    public static void WriteVarintField(List<byte> buf, int field, ulong value)
    {
        WriteTag(buf, field, 0);
        WriteVarint(buf, value);
    }

    public static void WriteBytesField(List<byte> buf, int field, byte[] data)
    {
        WriteTag(buf, field, 2);
        WriteVarint(buf, (ulong)data.Length);
        buf.AddRange(data);
    }

    public static void WriteStringField(List<byte> buf, int field, string value)
        => WriteBytesField(buf, field, Encoding.UTF8.GetBytes(value));

    public static byte[] Delimit(byte[] message)
    {
        var prefix = new List<byte>();
        WriteVarint(prefix, (ulong)message.Length);
        var result = new byte[prefix.Count + message.Length];
        prefix.CopyTo(result, 0);
        Array.Copy(message, 0, result, prefix.Count, message.Length);
        return result;
    }

    /// <summary>从缓冲区头部取一个 delimited 帧；不足一帧返回 null</summary>
    public static byte[]? TryTakeFrame(List<byte> buffer, out int consumed)
    {
        consumed = 0;
        if (buffer.Count == 0) return null;
        if ((buffer[0] & 0x80) != 0 && buffer.Count < 2) return null;

        ulong length = 0;
        int shift = 0, index = 0;
        while (index < buffer.Count)
        {
            byte b = buffer[index++];
            length += (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 49) return null;
        }
        if ((buffer[index - 1] & 0x80) != 0) return null;
        if (length > int.MaxValue) return null;
        if (buffer.Count < index + (int)length) return null;

        var frame = buffer.GetRange(index, (int)length).ToArray();
        consumed = index + (int)length;
        return frame;
    }

    public sealed record Field(int Number, int Wire, ulong Varint, byte[]? Bytes);

    public static List<Field> ReadFields(ReadOnlySpan<byte> data)
    {
        var fields = new List<Field>();
        int i = 0;
        while (i < data.Length)
        {
            if (!TryReadVarint(data, ref i, out ulong tag)) break;
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            switch (wire)
            {
                case 0:
                    if (!TryReadVarint(data, ref i, out ulong v)) return fields;
                    fields.Add(new Field(field, wire, v, null));
                    break;
                case 2:
                    if (!TryReadVarint(data, ref i, out ulong len)) return fields;
                    if (len > int.MaxValue || i + (int)len > data.Length) return fields;
                    fields.Add(new Field(field, wire, 0, data.Slice(i, (int)len).ToArray()));
                    i += (int)len;
                    break;
                case 1:
                    i += 8;
                    break;
                case 5:
                    i += 4;
                    break;
                default:
                    return fields;
            }
        }
        return fields;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int i, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (i < data.Length)
        {
            byte b = data[i++];
            value += (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 63) return false;
        }
        return false;
    }

    public static string? GetString(List<Field> fields, int number)
    {
        var f = fields.FirstOrDefault(x => x.Number == number && x.Bytes != null);
        return f?.Bytes is null ? null : Encoding.UTF8.GetString(f.Bytes);
    }

    public static ulong GetVarint(List<Field> fields, int number)
        => fields.FirstOrDefault(x => x.Number == number && x.Bytes == null)?.Varint ?? 0;
}

/// <summary>
/// 线缆协议常量与消息构造。
/// 字段号沿用 AudioRelay 0.27.5 的 protobuf schema（从本机 Model.dll 反射核对）——
/// 手机端与接收端都在本仓库内，沿用只是省掉重新联调的成本。
/// 注意：官方 AudioRelay 桌面端虽然能握手，但内部不解码我们推的流，实际不可用。
/// </summary>
public static class Protocol
{
    public const int ControlPort = 59100;
    public const int AudioPort = 59100;
    public const int DiscoveryPort = 59200;
    public const int SampleRate = 48000;
    public const int Bits = 16;
    public const int Channels = 2;
    public const int OsWindows = 1;

    /// <summary>手机端每帧 5ms / 960 字节，抖动估算要用到</summary>
    public const int FrameMs = 5;

    public static byte[] BuildClientConfig()
    {
        var multi = new List<byte>(); Pb.WriteVarintField(multi, 1, 0);
        var streamData = new List<byte>(); Pb.WriteVarintField(streamData, 1, 1);
        var compression = new List<byte>(); Pb.WriteVarintField(compression, 1, 0); Pb.WriteVarintField(compression, 2, 0);
        var messages = new List<byte>(); Pb.WriteVarintField(messages, 1, 1);
        var retrans = new List<byte>(); Pb.WriteVarintField(retrans, 1, 1);
        var serverCfg = new List<byte>(); Pb.WriteVarintField(serverCfg, 1, 1);

        var features = new List<byte>();
        Pb.WriteBytesField(features, 1, multi.ToArray());
        Pb.WriteBytesField(features, 2, streamData.ToArray());
        Pb.WriteBytesField(features, 3, compression.ToArray());
        Pb.WriteBytesField(features, 4, messages.ToArray());
        Pb.WriteBytesField(features, 5, retrans.ToArray());
        Pb.WriteBytesField(features, 7, serverCfg.ToArray());

        var props = new List<byte>();
        Pb.WriteStringField(props, 1, "MicCast Receiver (Windows)");
        Pb.WriteVarintField(props, 2, 0);

        var cfg = new List<byte>();
        Pb.WriteStringField(cfg, 1, "MicCast Receiver (Windows)");
        Pb.WriteStringField(cfg, 3, "1.0.0");
        Pb.WriteVarintField(cfg, 6, SampleRate);
        Pb.WriteVarintField(cfg, 7, Bits);
        Pb.WriteVarintField(cfg, 8, Channels);
        Pb.WriteBytesField(cfg, 9, features.ToArray());
        Pb.WriteVarintField(cfg, 10, 0);
        Pb.WriteVarintField(cfg, 13, SampleRate);
        Pb.WriteVarintField(cfg, 14, Bits);
        Pb.WriteVarintField(cfg, 15, Channels);
        Pb.WriteStringField(cfg, 16, Environment.OSVersion.VersionString);
        Pb.WriteVarintField(cfg, 17, OsWindows);
        Pb.WriteBytesField(cfg, 18, props.ToArray());
        return cfg.ToArray();
    }

    public static byte[] BuildPingResponse(long requestTimestamp)
    {
        var inner = new List<byte>();
        Pb.WriteVarintField(inner, 1, (ulong)requestTimestamp);
        var envelope = new List<byte>();
        Pb.WriteBytesField(envelope, 22, inner.ToArray());
        return envelope.ToArray();
    }

    public static byte[] BuildServerAudioConfigRequest()
    {
        var envelope = new List<byte>();
        Pb.WriteBytesField(envelope, 23, Array.Empty<byte>());
        return envelope.ToArray();
    }

    public sealed record StreamData(long Tick, long TimestampMs, byte[] Payload);

    public static StreamData? ParseStreamData(byte[] datagram)
    {
        var fields = Pb.ReadFields(datagram);
        var payloadField = fields.FirstOrDefault(f => f.Number == 3 && f.Bytes != null);
        if (payloadField?.Bytes is null || payloadField.Bytes.Length == 0) return null;
        long tick = (long)Pb.GetVarint(fields, 1);
        long ts = (long)Pb.GetVarint(fields, 2);
        return new StreamData(tick, ts, payloadField.Bytes);
    }

    public sealed record DiscoveredServer(string Ip, string Name, string Version, string OsVersion, int Os, DateTime LastSeen);

    /// <summary>
    /// 解析发现广播：前 4 字节为小端长度，其后是 ProtoServerConfig。
    /// 名字优先取字段 1，取不到再回落到 initialProperties(7).name。
    /// </summary>
    public static DiscoveredServer? ParseDiscoveryDatagram(byte[] datagram, string ip)
    {
        if (datagram.Length < 5) return null;
        int length = BitConverter.ToInt32(datagram, 0);
        if (length <= 0 || length > datagram.Length - 4) return null;

        var body = new byte[length];
        Array.Copy(datagram, 4, body, 0, length);
        var fields = Pb.ReadFields(body);

        string? name = Pb.GetString(fields, 1);
        if (string.IsNullOrWhiteSpace(name))
        {
            var props = fields.FirstOrDefault(f => f.Number == 7 && f.Bytes != null);
            if (props?.Bytes != null)
            {
                name = Pb.GetString(Pb.ReadFields(props.Bytes), 1);
            }
        }

        return new DiscoveredServer(
            ip,
            string.IsNullOrWhiteSpace(name) ? ip : name!,
            Pb.GetString(fields, 2) ?? string.Empty,
            Pb.GetString(fields, 3) ?? string.Empty,
            (int)Pb.GetVarint(fields, 5),
            DateTime.Now);
    }
}
