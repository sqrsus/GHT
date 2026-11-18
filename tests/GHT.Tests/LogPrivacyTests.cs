using System.Net;
using System.Net.Sockets;
using System.Text;
using GHT.Services;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// 日志隐私：代理会看到完整的 URL（含 query）与全部请求头，这些内容一旦落盘，
/// 就等于把用户访问过的每一个地址、每一个 token 明文写到本地文件里。
///
/// 现有实现只记录 host:port 与统计数字。这个用例把「不记录」变成可回归的约束 ——
/// 以后任何人在日志里拼 <c>head.Target</c> 或某个 header，都会立刻被它拦住。
/// </summary>
public sealed class LogPrivacyTests
{
    private const string SecretInUrl = "SUPERSECRETTOKEN";
    private const string SecretInHeader = "BEARERHEADERSECRET";

    [Fact]
    public async Task Url_query_and_headers_are_never_written_to_the_log()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ght-privacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var log = new LogService(directory);

        try
        {
            await using var upstream = new FakeUpstream(async (stream, token) =>
            {
                await Net.ReadUntilAsync(stream, "\r\n\r\n");
                await Net.WriteAsciiAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
            });

            var port = Net.FreePort();
            var server = new ProxyServer(Net.DirectSettings(), log, Path.Combine(directory, "health.json"));

            await server.StartAsync(port);

            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                var stream = client.GetStream();

                await Net.WriteAsciiAsync(
                    stream,
                    $"GET http://127.0.0.1:{upstream.Port}/private/repo?token={SecretInUrl} HTTP/1.1\r\n"
                    + $"Host: 127.0.0.1:{upstream.Port}\r\n"
                    + $"Authorization: Bearer {SecretInHeader}\r\n\r\n");

                await Net.ReadToEndAsync(stream);
            }

            await server.StopAsync();
        }
        finally
        {
            log.Dispose();
        }

        try
        {
            var files = Directory.GetFiles(directory, "*.log");
            Assert.NotEmpty(files);

            var text = string.Join("\n", files.Select(file => File.ReadAllText(file, Encoding.UTF8)));

            // 日志确实写入了内容（否则下面两条断言是因为「文件是空的」而假通过）
            Assert.Contains("127.0.0.1", text);

            Assert.DoesNotContain(SecretInUrl, text);
            Assert.DoesNotContain(SecretInHeader, text);
            Assert.DoesNotContain("/private/repo", text);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // ignore
            }
        }
    }
}
