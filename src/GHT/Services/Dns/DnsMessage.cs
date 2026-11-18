using System.Buffers.Binary;
using System.Net;

namespace GHT.Services.Dns;

/// <summary>一次 DNS 解析的结果：A/AAAA 记录与最小 TTL。</summary>
internal readonly record struct DnsQueryResult(IReadOnlyList<IPAddress> Addresses, int TtlSeconds);

/// <summary>手写的最小 DNS 报文编解码：只做「查 A/AAAA + 读答案」，不含压缩域名的完整实现。</summary>
internal static class DnsMessage
{
    public const int TypeA = 1;
    public const int TypeAaaa = 28;
    public const int ClassIn = 1;

    /// <summary>构造标准递归查询报文。</summary>
    public static byte[] BuildQuery(string host, ushort id, bool ipv6 = false)
    {
        var name = host.Trim().TrimEnd('.');
        var labels = name.Split('.', StringSplitOptions.RemoveEmptyEntries);

        var size = 12 + 1 + 4;
        foreach (var label in labels)
        {
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

    /// <summary>解析响应报文；ID 不匹配、非响应、rcode≠0 均返回 null。</summary>
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
            var ttl = (int)BinaryPrimitives.ReadUInt32BigEndian(response[(pos + 4)..]);
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(response[(pos + 8)..]);
            pos += 10;

            if (pos + dataLength > response.Length)
            {
                break;
            }

            if (@class == ClassIn && type == TypeA && dataLength == 4)
            {
                addresses.Add(new IPAddress(response.Slice(pos, 4).ToArray()));
                if (ttl > 0 && ttl < minTtl)
                {
                    minTtl = ttl;
                }
            }
            else if (@class == ClassIn && type == TypeAaaa && dataLength == 16)
            {
                addresses.Add(new IPAddress(response.Slice(pos, 16).ToArray()));
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

    /// <summary>跳过域名（支持 0xC0 压缩指针）。</summary>
    private static bool SkipName(ReadOnlySpan<byte> buffer, ref int pos)
    {
        var guard = 0;
        while (pos < buffer.Length)
        {
            var length = buffer[pos];

            if (length == 0)
            {
                pos += 1;
                return true;
            }

            if ((length & 0xC0) == 0xC0)
            {
                pos += 2;
                return true;
            }

            pos += 1 + length;

            if (++guard > 128)
            {
                return false;
            }
        }

        return false;
    }
}
