using System.Buffers.Binary;
using System.Net;
using System.Text;
using GHT.Services.Dns;
using Xunit;

namespace GHT.Tests;

public sealed class DnsMessageTests
{
    [Fact]
    public void Builds_A_query()
    {
        var query = DnsMessage.BuildQuery("github.com", 0x1234);

        Assert.NotNull(query);
        Assert.Equal(0x12, query![0]);
        Assert.Equal(0x34, query[1]);
        Assert.Equal(DnsMessage.TypeA, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(query.Length - 4)));
    }

    [Fact]
    public void Builds_AAAA_query()
    {
        var query = DnsMessage.BuildQuery("github.com", 1, ipv6: true);

        Assert.NotNull(query);
        Assert.Equal(DnsMessage.TypeAaaa, BinaryPrimitives.ReadUInt16BigEndian(query!.AsSpan(query.Length - 4)));
    }

    [Fact]
    public void Converts_chinese_domain_to_punycode()
    {
        var query = DnsMessage.BuildQuery("中文.cn", 1);

        Assert.NotNull(query);

        // 报文里只能出现 ASCII —— 直接按 UTF-8 写字节会让 DNS 服务器解析出别的名字
        Assert.All(query!, b => Assert.True(b < 128));
        Assert.Contains("xn--", Encoding.ASCII.GetString(query!), StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_overlong_label()
    {
        Assert.Null(DnsMessage.BuildQuery(new string('a', 64) + ".com", 1));
    }

    [Fact]
    public void Rejects_empty_host()
    {
        Assert.Null(DnsMessage.BuildQuery(string.Empty, 1));
    }

    [Fact]
    public void Parses_answer_with_compression_pointer()
    {
        var parsed = DnsMessage.Parse(BuildResponse(), 0x1234);

        Assert.NotNull(parsed);
        Assert.Equal(new[] { IPAddress.Parse("1.2.3.4") }, parsed!.Value.Addresses.ToArray());
        Assert.Equal(300, parsed.Value.TtlSeconds);
    }

    [Fact]
    public void Rejects_truncated_response()
    {
        var response = BuildResponse();
        var truncated = response.AsSpan(0, response.Length - 2).ToArray();

        Assert.Null(DnsMessage.Parse(truncated, 0x1234));
    }

    [Fact]
    public void Rejects_mismatched_id()
    {
        Assert.Null(DnsMessage.Parse(BuildResponse(), 0x9999));
    }

    [Fact]
    public void Rejects_out_of_range_pointer()
    {
        var response = BuildResponse();
        var answerNameOffset = 12 + 12 + 4; // 头部 12 + 问题域名 12 + QTYPE/QCLASS 4

        response[answerNameOffset] = 0xC0;
        response[answerNameOffset + 1] = 0xFF;

        Assert.Null(DnsMessage.Parse(response, 0x1234));
    }

    /// <summary>构造一个「问题区 + 一条 A 记录（名字用压缩指针）」的响应报文。</summary>
    private static byte[] BuildResponse()
    {
        var buffer = new List<byte>(64);

        void U16(int value)
        {
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)value);
        }

        U16(0x1234); // id
        U16(0x8180); // flags: QR=1 RD=1 RA=1 rcode=0
        U16(1);      // qdcount
        U16(1);      // ancount
        U16(0);      // nscount
        U16(0);      // arcount

        foreach (var label in new[] { "github", "com" })
        {
            buffer.Add((byte)label.Length);
            buffer.AddRange(Encoding.ASCII.GetBytes(label));
        }

        buffer.Add(0);
        U16(DnsMessage.TypeA);
        U16(DnsMessage.ClassIn);

        buffer.Add(0xC0); // 压缩指针 → 偏移 12（问题区的域名）
        buffer.Add(0x0C);
        U16(DnsMessage.TypeA);
        U16(DnsMessage.ClassIn);
        U16(0);
        U16(300);         // TTL
        U16(4);           // RDLENGTH
        buffer.AddRange(new byte[] { 1, 2, 3, 4 });

        return buffer.ToArray();
    }
}
