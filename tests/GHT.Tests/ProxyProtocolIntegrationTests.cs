using System.Net;
using System.Net.Sockets;
using System.Text;
using GHT.Services;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// 代理的网络边界集成测试：驱动真实的 <see cref="ProxyServer"/> 与本地上游 socket。
///
/// 为什么必须有这一层：单元测试链接的是纯逻辑源码，帧解析、缓存、竞速都被覆盖了，
/// 但「代理真的能转发一个请求」「畸形请求真的回 400 而不是挂死」这类结论只有真开 socket 才能验证。
/// 上游固定是本机回环，不依赖外网，因此在 CI 上同样可跑。
/// </summary>
public sealed class ProxyProtocolIntegrationTests
{
    private static async Task<(ProxyServer Server, int Port, TcpClient Client, Stream Stream)> ConnectAsync(
        TempLog log,
        int port)
    {
        var server = new ProxyServer(Net.DirectSettings(), log.Service, Path.Combine(log.Directory, "health.json"));
        await server.StartAsync(port);

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        client.NoDelay = true;

        return (server, port, client, client.GetStream());
    }

    [Fact]
    public async Task Plain_http_is_rewritten_to_origin_form_and_body_is_relayed()
    {
        using var log = new TempLog();
        var requests = new List<string>();

        await using var upstream = new FakeUpstream(async (stream, token) =>
        {
            var head = await Net.ReadUntilAsync(stream, "\r\n\r\n");
            lock (requests)
            {
                requests.Add(head);
            }

            await Net.WriteAsciiAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nhi");
        });

        var port = Net.FreePort();
        var (server, _, client, stream) = await ConnectAsync(log, port);

        using (client)
        await using (server)
        {
            await Net.WriteAsciiAsync(
                stream,
                $"GET http://127.0.0.1:{upstream.Port}/path?q=1 HTTP/1.1\r\nHost: 127.0.0.1:{upstream.Port}\r\n\r\n");

            var response = await Net.ReadToEndAsync(stream);

            Assert.Contains("200 OK", response);
            Assert.EndsWith("hi", response);

            string recorded;
            lock (requests)
            {
                Assert.Single(requests);
                recorded = requests[0];
            }

            // 绝对地址必须被改写成 origin-form，否则真实服务器会把整条 URL 当成路径
            Assert.StartsWith("GET /path?q=1 HTTP/1.1\r\n", recorded);
            Assert.Contains($"Host: 127.0.0.1:{upstream.Port}", recorded);
            Assert.DoesNotContain("http://", recorded);
        }
    }

    [Fact]
    public async Task Chunked_request_body_is_forwarded_verbatim()
    {
        using var log = new TempLog();
        var received = new StringBuilder();

        await using var upstream = new FakeUpstream(async (stream, token) =>
        {
            // 一次读到结束块为止：读到的内容里既有头也有体，最后再按帧尾断言
            var all = await Net.ReadUntilAsync(stream, "0\r\n\r\n");

            lock (received)
            {
                received.Append(all);
            }

            await Net.WriteAsciiAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        });

        var port = Net.FreePort();
        var (server, _, client, stream) = await ConnectAsync(log, port);

        using (client)
        await using (server)
        {
            const string frame = "5\r\nhello\r\n0\r\n\r\n";

            await Net.WriteAsciiAsync(
                stream,
                $"POST http://127.0.0.1:{upstream.Port}/upload HTTP/1.1\r\n"
                + $"Host: 127.0.0.1:{upstream.Port}\r\n"
                + "Transfer-Encoding: chunked\r\n\r\n"
                + frame);

            var response = await Net.ReadToEndAsync(stream);
            Assert.Contains("200 OK", response);

            string text;
            lock (received)
            {
                text = received.ToString();
            }

            // 分块帧必须一字不差：代理只搬字节，不重新编码，否则上游会错帧
            Assert.EndsWith(frame, text);
        }
    }

    [Fact]
    public async Task Connect_tunnel_relays_bytes_in_both_directions()
    {
        using var log = new TempLog();

        await using var upstream = new FakeUpstream(async (stream, token) =>
        {
            // 回显：收到什么原样送回，用来验证隧道是双向透传
            var buffer = new byte[4096];

            while (true)
            {
                var read = await stream.ReadAsync(buffer, token);
                if (read <= 0)
                {
                    return;
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), token);
            }
        });

        var port = Net.FreePort();
        var (server, _, client, stream) = await ConnectAsync(log, port);

        using (client)
        await using (server)
        {
            await Net.WriteAsciiAsync(stream, $"CONNECT 127.0.0.1:{upstream.Port} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");

            var established = await Net.ReadUntilAsync(stream, "\r\n\r\n");
            Assert.Contains("200", established);

            await Net.WriteAsciiAsync(stream, "ping-pong");

            var echoed = await Net.ReadExactAsync(stream, 9);
            Assert.Equal("ping-pong", Encoding.ASCII.GetString(echoed));
        }
    }

    [Theory]
    [InlineData("GET http://127.0.0.1:1/ HTTP/1.1\r\nBadHeaderWithoutColon\r\n\r\n")]      // 头里没有冒号
    [InlineData("GET http://127.0.0.1:1/ HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n")]         // 重复 Host
    [InlineData("GET http://127.0.0.1:1/ HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: gzip\r\n\r\n")] // 不支持的 TE
    [InlineData("GET /\r\n\r\n")]                                                          // 缺版本
    public async Task Malformed_request_head_gets_400(string request)
    {
        using var log = new TempLog();

        var port = Net.FreePort();
        var (server, _, client, stream) = await ConnectAsync(log, port);

        using (client)
        await using (server)
        {
            await Net.WriteAsciiAsync(stream, request);

            var response = await Net.ReadToEndAsync(stream);
            Assert.Contains("400", response);
        }
    }

    [Fact]
    public async Task Oversized_request_head_is_rejected_without_hanging()
    {
        using var log = new TempLog();

        var port = Net.FreePort();
        var (server, _, client, stream) = await ConnectAsync(log, port);

        using (client)
        await using (server)
        {
            // 超过 32KB 的头：不能无限缓冲，也不能一直等到超时
            var payload = new StringBuilder("GET http://127.0.0.1:1/ HTTP/1.1\r\n");

            while (payload.Length < 40 * 1024)
            {
                payload.Append("X-Pad: ").Append('a', 100).Append("\r\n");
            }

            try
            {
                await Net.WriteAsciiAsync(stream, payload.ToString());
            }
            catch (IOException)
            {
                // 代理提前断开时客户端写入会失败：这本身就是可接受的结果之一
            }

            // 断言「不会挂住」而不是「一定收到 400」：
            // 目标端被主动关闭时，不同系统可能直接把回应丢掉（RST），能证明拒绝即可
            var response = await Net.ReadToEndAsync(stream, 5000);
            Assert.DoesNotContain("200", response);
            Assert.True(server.IsRunning, "拒绝一条畸形请求不该影响服务本身");
        }
    }

    [Fact]
    public async Task Expect_100_continue_is_answered_by_the_proxy_then_body_is_forwarded()
    {
        using var log = new TempLog();
        var body = new List<byte>();
        var seenHead = new StringBuilder();

        await using var upstream = new FakeUpstream(async (stream, token) =>
        {
            var head = await Net.ReadUntilAsync(stream, "\r\n\r\n");
            lock (seenHead)
            {
                seenHead.Append(head);
            }

            var payload = await Net.ReadExactAsync(stream, 5);
            lock (body)
            {
                body.AddRange(payload);
            }

            await Net.WriteAsciiAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        });

        var port = Net.FreePort();
        var (server, _, client, stream) = await ConnectAsync(log, port);

        using (client)
        await using (server)
        {
            await Net.WriteAsciiAsync(
                stream,
                $"POST http://127.0.0.1:{upstream.Port}/upload HTTP/1.1\r\n"
                + $"Host: 127.0.0.1:{upstream.Port}\r\n"
                + "Content-Length: 5\r\n"
                + "Expect: 100-continue\r\n\r\n");

            var interim = await Net.ReadUntilAsync(stream, "\r\n\r\n");
            Assert.Contains("100", interim);

            await Net.WriteAsciiAsync(stream, "hello");

            var rest = await Net.ReadToEndAsync(stream);
            Assert.Contains("200 OK", rest);

            lock (body)
            {
                Assert.Equal("hello", Encoding.ASCII.GetString(body.ToArray()));
            }

            // 代理自己应答 100-continue 并丢掉这个头，否则它会与客户端互等（curl 上传必踩）
            lock (seenHead)
            {
                Assert.DoesNotContain("Expect", seenHead.ToString(), StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Malformed_chunked_body_closes_the_connection_without_killing_the_server()
    {
        using var log = new TempLog();

        await using var upstream = new FakeUpstream(async (stream, token) =>
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false));

        var port = Net.FreePort();
        var (server, _, client, stream) = await ConnectAsync(log, port);

        using (client)
        await using (server)
        {
            // 分块长度不是十六进制：代理必须放弃这条连接，而不是把半个帧写进上游
            await Net.WriteAsciiAsync(
                stream,
                $"POST http://127.0.0.1:{upstream.Port}/ HTTP/1.1\r\n"
                + $"Host: 127.0.0.1:{upstream.Port}\r\n"
                + "Transfer-Encoding: chunked\r\n\r\n"
                + "ZZZZ\r\nnope\r\n");

            var response = await Net.ReadToEndAsync(stream);
            Assert.DoesNotContain("200", response);

            // 关键：出错只该结束这一条连接，服务本身必须还能继续接新请求
            using var second = new TcpClient();
            await second.ConnectAsync(IPAddress.Loopback, port);
            Assert.True(server.IsRunning);
        }
    }

    [Fact]
    public async Task Upstream_closing_early_looks_like_eof_to_the_client()
    {
        using var log = new TempLog();

        await using var upstream = new FakeUpstream(async (stream, token) =>
        {
            // 收到请求头后立刻关闭，一个字节的响应都不给
            await Net.ReadUntilAsync(stream, "\r\n\r\n");
        });

        var port = Net.FreePort();
        var (server, _, client, stream) = await ConnectAsync(log, port);

        using (client)
        await using (server)
        {
            await Net.WriteAsciiAsync(
                stream,
                $"GET http://127.0.0.1:{upstream.Port}/ HTTP/1.1\r\nHost: 127.0.0.1:{upstream.Port}\r\n\r\n");

            var response = await Net.ReadToEndAsync(stream, 5000);

            // 上游提前关闭 = 客户端读到 EOF，而不是代理报错或挂住
            Assert.DoesNotContain("502", response);
            Assert.True(server.IsRunning);
        }
    }

    [Fact]
    public async Task Request_to_unreachable_upstream_gets_502()
    {        using var log = new TempLog();

        // 拿一个确定没人监听的端口
        var dead = Net.FreePort();

        var port = Net.FreePort();
        var (server, _, client, stream) = await ConnectAsync(log, port);

        using (client)
        await using (server)
        {
            await Net.WriteAsciiAsync(
                stream,
                $"GET http://127.0.0.1:{dead}/ HTTP/1.1\r\nHost: 127.0.0.1:{dead}\r\n\r\n");

            var response = await Net.ReadToEndAsync(stream, 15000);
            Assert.Contains("502", response);
        }
    }

    /// <summary>
    /// 全局并发上限：占满之后新连接必须收到 503，而不是继续被接入。
    /// 这条断言对应的是一个真实的内存风险 —— 旧实现对每条连接都无条件起任务，活跃连接没有上界。
    /// </summary>
    [Fact]
    public async Task Connection_over_the_global_limit_gets_503()
    {
        using var log = new TempLog();

        var settings = Net.DirectSettings();
        settings.MaxConcurrentConnections = 1;
        settings.MaxConnectionsPerHost = 0;

        var port = Net.FreePort();
        await using var server = new ProxyServer(settings, log.Service, Path.Combine(log.Directory, "health.json"));
        await server.StartAsync(port);

        // 这条连接什么都不发，停在读请求头上，占住唯一的名额
        using var busy = new TcpClient();
        await busy.ConnectAsync(IPAddress.Loopback, port);

        Assert.True(
            await Net.WaitUntilAsync(() => server.BudgetedConnections == 1),
            "第一条连接必须被计入并发名额");

        using var rejected = new TcpClient();
        await rejected.ConnectAsync(IPAddress.Loopback, port);

        // 拒绝发生在接入时，不需要客户端先发请求
        var response = await Net.ReadUntilAsync(rejected.GetStream(), "\r\n\r\n", 5000);

        Assert.Contains("503", response);
        Assert.Equal(1, server.RejectedConnections);
    }

    /// <summary>
    /// 单主机上限：同一目标占满后，第二条隧道收到 503，而其他目标不受影响。
    /// </summary>
    [Fact]
    public async Task Connection_over_the_per_host_limit_gets_503()
    {
        using var log = new TempLog();

        await using var upstream = new FakeUpstream(async (_, token) =>
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false));

        var settings = Net.DirectSettings();
        settings.MaxConcurrentConnections = 10;
        settings.MaxConnectionsPerHost = 1;

        var port = Net.FreePort();
        await using var server = new ProxyServer(settings, log.Service, Path.Combine(log.Directory, "health.json"));
        await server.StartAsync(port);

        using var first = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, port);

        var firstStream = first.GetStream();
        await Net.WriteAsciiAsync(
            firstStream,
            $"CONNECT 127.0.0.1:{upstream.Port} HTTP/1.1\r\nHost: 127.0.0.1:{upstream.Port}\r\n\r\n");

        var established = await Net.ReadUntilAsync(firstStream, "\r\n\r\n", 5000);
        Assert.Contains("200", established);

        using var second = new TcpClient();
        await second.ConnectAsync(IPAddress.Loopback, port);

        var secondStream = second.GetStream();
        await Net.WriteAsciiAsync(
            secondStream,
            $"CONNECT 127.0.0.1:{upstream.Port} HTTP/1.1\r\nHost: 127.0.0.1:{upstream.Port}\r\n\r\n");

        var rejected = await Net.ReadUntilAsync(secondStream, "\r\n\r\n", 5000);
        Assert.Contains("503", rejected);
    }
}
