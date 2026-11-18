using System.Net;
using System.Net.Sockets;
using System.Text;
using GHT.Services;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// 生命周期一致性：启停串行化、端口占用、停止时活跃连接必须被收掉。
///
/// 这些用例驱动的是真实的 <see cref="ProxyServer"/> 与真实 socket，而不是纯逻辑桩 ——
/// 它们覆盖的正是「停止后隧道还在跑」「重启后监听状态错乱」这类只在真实网络上才暴露的问题。
/// </summary>
public sealed class ProxyLifecycleTests
{
    private static ProxyServer CreateServer(TempLog log) =>
        new(Net.DirectSettings(), log.Service, Path.Combine(log.Directory, "health.json"));

    [Fact]
    public async Task Restart_on_the_same_port_is_consistent()
    {
        using var log = new TempLog();
        var port = Net.FreePort();

        await using var server = CreateServer(log);

        for (var i = 0; i < 3; i++)
        {
            await server.StartAsync(port);

            Assert.True(server.IsRunning, $"第 {i + 1} 次启动后状态应为 Running");
            Assert.Equal(port, server.Port);

            await server.StopAsync();

            Assert.False(server.IsRunning, $"第 {i + 1} 次停止后状态应为 Stopped");
            Assert.Equal(0, server.ActiveConnections);
        }
    }

    [Fact]
    public async Task Start_is_idempotent_while_running()
    {
        using var log = new TempLog();
        var port = Net.FreePort();

        await using var server = CreateServer(log);

        await server.StartAsync(port);
        await server.StartAsync(port);

        Assert.True(server.IsRunning);
        Assert.Equal(port, server.Port);

        await server.StopAsync();
    }

    [Fact]
    public async Task Start_on_an_occupied_port_throws_and_leaves_the_server_stopped()
    {
        using var log = new TempLog();

        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var busyPort = ((IPEndPoint)blocker.LocalEndpoint).Port;

        await using var server = CreateServer(log);

        try
        {
            await Assert.ThrowsAnyAsync<SocketException>(() => server.StartAsync(busyPort));
            Assert.False(server.IsRunning);

            // 失败之后必须还能正常启动 —— 旧实现把状态留在 Stopping/Running 时会卡死在这里
            var freePort = Net.FreePort();
            await server.StartAsync(freePort);
            Assert.True(server.IsRunning);
            Assert.Equal(freePort, server.Port);
        }
        finally
        {
            blocker.Stop();
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Stop_aborts_a_live_tunnel_instead_of_leaving_it_running()
    {
        using var log = new TempLog();

        // 上游接了连接就不说话：模拟「隧道已建立、长时间空闲」的真实长连接
        await using var upstream = new FakeUpstream(async (stream, token) =>
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false));

        var port = Net.FreePort();

        await using var server = CreateServer(log);
        await server.StartAsync(port);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();

        await Net.WriteAsciiAsync(stream, $"CONNECT 127.0.0.1:{upstream.Port} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");

        var response = await Net.ReadUntilAsync(stream, "\r\n\r\n");
        Assert.Contains("200", response);

        // 隧道建立后应当被登记进连接表
        Assert.True(await Net.WaitUntilAsync(() => server.ActiveConnections == 1), "隧道未被跟踪，停止时无从收尾");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await server.StopAsync();
        watch.Stop();

        Assert.False(server.IsRunning);
        Assert.Equal(0, server.ActiveConnections);

        // 停止不能靠「等连接自己结束」：这条连接永远不会自己结束，所以必须在有界时间内强关
        Assert.True(
            watch.ElapsedMilliseconds < 5000,
            $"停止耗时 {watch.ElapsedMilliseconds}ms，说明没有走「有界等待 + 强关」路径");

        // 客户端这一侧应当观察到连接被关闭（读到 0 或抛 IO 异常），而不是继续挂着
        var buffer = new byte[64];
        using var cts = new CancellationTokenSource(2000);

        try
        {
            var read = await stream.ReadAsync(buffer, cts.Token);
            Assert.Equal(0, read);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail("停止后客户端仍能保持连接，说明隧道没有被真正关闭");
        }
        catch (IOException)
        {
            // 对端强制关闭，同样算已关闭
        }
    }

    [Fact]
    public async Task Stop_is_safe_to_call_twice()
    {
        using var log = new TempLog();
        var port = Net.FreePort();

        await using var server = CreateServer(log);

        await server.StartAsync(port);
        await server.StopAsync();
        await server.StopAsync();

        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task Client_that_disconnects_mid_head_is_cleaned_up()
    {
        using var log = new TempLog();
        var port = Net.FreePort();

        await using var server = CreateServer(log);
        await server.StartAsync(port);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();

            // 只发半个请求头就断开：旧实现会让这个任务一直挂着等数据
            await Net.WriteAsciiAsync(stream, "GET http://example.com/ HTTP/1.1\r\nHost: exa");
        }

        Assert.True(
            await Net.WaitUntilAsync(() => server.ActiveConnections == 0, 5000),
            "客户端断开后连接没有被回收");

        await server.StopAsync();
    }
}
