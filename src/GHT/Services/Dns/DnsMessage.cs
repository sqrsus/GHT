using System.Buffers.Binary;
using System.Globalization;
using System.Net;

namespace GHT.Services.Dns;

/// <summary>一次 DNS 解析的结果：A/AAAA 记录与最小 TTL。</summary>
internal readonly record struct DnsQueryResult(IReadOnlyList<IPAddress> Addresses, int TtlSeconds);

/// <summary>
/// 手写的最小 DNS 报文编解码：只做「查 A/AAAA + 读答案」。
///
/// 解析全程按 span 边界校验：报文来自公共 DNS，可能被投毒、被截断或本身就是畸形的。
/// 任何越界都返回 null（当作没解析出来），绝不让半截数据进入候选池。
/// </summary>
internal static class DnsMessage
{
    public const int TypeA = 1;
    public const int TypeAaaa = 28;
    public const int ClassIn = 1;

    private const int MaxNameLength = 253;
    private const int MaxLabelLength = 63;

    /// <summary>
    /// 构造递归查询报文。域名先转 Punycode，标签长度与总长不合法时返回 null
    /// （这类输入不可能有合法答案，直接放弃比发一个畸形查询更安全）。
    /// </summary>
    public static byte[]? BuildQuery(string host, ushort id, bool ipv6 = false)
    {
        var ascii = ToAscii(host);
        if (ascii is null)
        {
            return null;
        }

        var labels = ascii.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length == 0 || labels.Length > 127)
        {
            return null;
        }

        var size = 12 + 1 + 4;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > MaxLabelLength)
            {
                return null;
            }

            size += label.Length + 1;
        }

        var buffer = new byte[size];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0), id);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), 0x0100); // RD=1
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4), 1);      // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(10), 0);

        var pos = 12;
        foreach (var label in labels)
        {
            buffer[pos++] = (byte)label.Length;
            for (var i = 0; i < label.Length; i++)
            {
                buffer[pos++] = (byte)label[i];
            }
        }

        buffer[pos++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(pos), ipv6 ? (ushort)TypeAaaa : (ushort)TypeA);
        pos += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(pos), ClassIn);

        return buffer;
    }

    /// <summary>解析响应报文；ID 不匹配、非响应、rcode≠0、越界均返回 null。</summary>
    public static DnsQueryResult? Parse(ReadOnlySpan<byte> response, ushort expectedId)
    {
        if (response.Length < 12)
        {
            return null;
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(response) != expectedId)
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(response[2..]);
        if ((flags & 0x8000) == 0)
        {
            return null; // 不是响应
        }

        if ((flags & 0x000F) != 0)
        {
            return null; // NXDOMAIN / SERVFAIL 等
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(response[4..]);
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response[6..]);

        var pos = 12;
        for (var i = 0; i < questionCount; i++)
        {
            if (!SkipName(response, ref pos))
            {
                return null;
            }

            // 问题区固定 QTYPE + QCLASS 共 4 字节，越界说明报文被截断
            if (pos + 4 > response.Length)
            {
                return null;
            }

            pos += 4;
        }

        var addresses = new List<IPAddress>(4);
        var minTtl = int.MaxValue;

        for (var i = 0; i < answerCount; i++)
        {
            if (!SkipName(response, ref pos))
            {
                break;
            }

            if (pos + 10 > response.Length)
            {
                break;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(response[pos..]);
            var @class = BinaryPrimitives.ReadUInt16BigEndian(response[(pos + 2)..]);
            var ttlRaw = BinaryPrimitives.ReadUInt32BigEndian(response[(pos + 4)..]);
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(response[(pos + 8)..]);
            pos += 10;

            if (pos + dataLength > response.Length)
            {
                break;
            }

            // TTL 是 32 位无符号，直接转 int 会溢出成负数
            var ttl = ttlRaw > int.MaxValue ? int.MaxValue : (int)ttlRaw;

            if (@class == ClassIn && type == TypeA && dataLength == 4)
            {
                addresses.Add(new IPAddress(response.Slice(pos, 4)));
                if (ttl > 0 && ttl < minTtl)
                {
                    minTtl = ttl;
                }
            }
            else if (@class == ClassIn && type == TypeAaaa && dataLength == 16)
            {
                addresses.Add(new IPAddress(response.Slice(pos, 16)));
                if (ttl > 0 && ttl < minTtl)
                {
                    minTtl = ttl;
                }
            }

            pos += dataLength;
        }

        if (addresses.Count == 0)
        {
            return null;
        }

        return new DnsQueryResult(addresses, minTtl == int.MaxValue ? 60 : minTtl);
    }

    /// <summary>
    /// 跳过域名（支持 0xC0 压缩指针）。压缩指针只验证「指向报文内」，不跟随展开，
    /// 因为我们不关心名字本身，只关心跳过它需要多少字节。
    /// </summary>
    private static bool SkipName(ReadOnlySpan<byte> buffer, ref int pos)
    {
        var cursor = pos;
        var guard = 0;
        var nameLength = 0;

        while (cursor < buffer.Length)
        {
            var length = buffer[cursor];

            if (length == 0)
            {
                pos = cursor + 1;
                return true;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= buffer.Length)
                {
                    return false;
                }

                var pointer = ((length & 0x3F) << 8) | buffer[cursor + 1];
                if (pointer >= buffer.Length)
                {
                    return false;
                }

                pos = cursor + 2;
                return true;
            }

            // 0x40 / 0x80 是保留位，出现即非法
            if ((length & 0xC0) != 0)
            {
                return false;
            }

            cursor += 1 + length;
            nameLength += length + 1;

            if (nameLength > MaxNameLength + 1)
            {
                return false;
            }

            if (++guard > 128)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>把域名转成可写入报文的 ASCII（中文域名转 Punycode）。非法输入返回 null。</summary>
    private static string? ToAscii(string host)
    {
        var name = (host ?? string.Empty).Trim().TrimEnd('.');
        if (name.Length == 0)
        {
            return null;
        }

        try
        {
            name = new IdnMapping().GetAscii(name);
        }
        catch (ArgumentException)
        {
            // 含非法字符或标签超长
            return null;
        }

        return name.Length > MaxNameLength ? null : name;
    }
}
