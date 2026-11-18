using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GHT.Services;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// 竞速的两个新语义：
/// <list type="number">
/// <item>真实建连失败必须回报「哪个地址、卡了多久」，否则日志里只有一句「预算用尽」，无法定位；</item>
/// <item>健康度必须能跨进程存活 —— 只在内存里的话，每次重启都要用首连预算重新试错一遍。</item>
/// </list>
///
/// 建连动作通过 <c>UpstreamRacer</c> 的内部构造注入，而不是去打真实网络。
/// 这不是为了省事：第一版用「连本机 1 号端口必然被立刻拒绝」当失败样本，在 Linux 上成立，
/// 在 CI 的 Windows 上却是「RST 要 2043~2073ms 才回来」，1s 的竞速预算先取消，
/// 于是「失败」变成了「没来得及试」——三个用例一起挂。断言的本类逻辑，就该由本类喂输入。
/// </summary>
public sealed class UpstreamHealthTests : IDisposable
{
    private const string Sample = "127.0.0.1";

    private readonly string _directory;
    private readonly LogService _log;

    public UpstreamHealthTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ght-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _log = new LogService(_directory);
    }

    public void Dispose()
    {
        _log.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>建连被明确拒绝（上游回了 RST / ICMP）。这是真实的地址失败。</summary>
    private static Func<IPAddress, int, CancellationToken, Task<TcpClient>> Refused(int delayMs = 0) =>
        async (_, _, _) =>
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
            }

            throw new SocketException((int)SocketError.ConnectionRefused);
        };

    /// <summary>建连一直不返回，只能被预算取消。这是「没来得及试」，不是地址失败。</summary>
    private static Func<IPAddress, int, CancellationToken, Task<TcpClient>> Hanging() =>
        async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            return new TcpClient();
        };

    [Fact]
    public async Task Real_failure_reports_address_and_elapsed_time()
    {
        var racer = new UpstreamRacer(_log, Refused(delayMs: 100));
        var reported = new List<(IPAddress Ip, long Ms)>();

        var win = await racer.RaceAsync(
            new[] { IPAddress.Parse(Sample) },
            1,
            5000,
            5000,
            CancellationToken.None,
            (ip, ms) => reported.Add((ip, ms)));

        Assert.Null(win);
        Assert.Single(reported);
        Assert.Equal(IPAddress.Parse(Sample), reported[0].Ip);

        // 只断下界：卡了 100ms 就一定不会报出更小的值，负载再高也只会更大
        Assert.True(reported[0].Ms >= 50, $"耗时必须反映真实等待（实测 {reported[0].Ms}ms），用于区分「快拒绝」与「超时」");
    }

    [Fact]
    public async Task Budget_cancellation_is_not_recorded_as_address_failure()
    {
        var racer = new UpstreamRacer(_log, Hanging());
        var reported = new List<(IPAddress Ip, long Ms)>();

        var win = await racer.RaceAsync(
            new[] { IPAddress.Parse(Sample) },
            1,
            connectTimeoutMs: 5000,
            batchBudgetMs: 150,
            CancellationToken.None,
            (ip, ms) => reported.Add((ip, ms)));

        Assert.Null(win);
        Assert.Empty(reported);

        // 关键：预算取消不能写进健康度。旧实现每耗尽一次预算就给全部候选记一次失败，
        // 好地址会因为「预算小」被永久降权，这正是「胜出 283 次的同时预算用尽 125 次」的成因之一。
        var path = Path.Combine(_directory, "health-cancel.json");
        racer.Save(path);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(
            document.RootElement.GetProperty("Addresses").TryGetProperty(Sample, out _),
            "被取消的建连不该产生健康度记录");
    }

    [Fact]
    public async Task Health_survives_save_and_load_across_instances()
    {
        var first = Path.Combine(_directory, "health-a.json");

        var before = new UpstreamRacer(_log, Refused());
        await before.RaceAsync(
            new[] { IPAddress.Parse(Sample) },
            1,
            5000,
            5000,
            CancellationToken.None);

        before.Save(first);
        Assert.True(File.Exists(first), "保存后文件必须存在");

        // 新实例模拟「进程重启」
        var after = new UpstreamRacer(_log, Refused());
        after.Load(first);

        var second = Path.Combine(_directory, "health-b.json");
        after.Save(second);

        using var document = JsonDocument.Parse(File.ReadAllText(second));
        var entry = document.RootElement.GetProperty("Addresses").GetProperty(Sample);

        Assert.Equal(1, entry.GetProperty("Failures").GetInt32());
        Assert.True(entry.GetProperty("LastFailureMs").GetInt64() > 0, "时间必须是绝对时间戳，否则跨进程无效");
    }

    [Fact]
    public void Loading_a_missing_file_is_harmless()
    {
        var racer = new UpstreamRacer(_log, Refused());

        // 全新安装时没有这份文件，不能因此抛异常或影响启动
        racer.Load(Path.Combine(_directory, "does-not-exist.json"));

        // 再保存一次应当能正常创建
        var path = Path.Combine(_directory, "health-c.json");
        racer.Save(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Health_file_is_valid_json_after_repeated_saves()
    {
        var racer = new UpstreamRacer(_log, Refused());
        var path = Path.Combine(_directory, "health-d.json");

        for (var i = 0; i < 3; i++)
        {
            await racer.RaceAsync(
                new[] { IPAddress.Parse(Sample) },
                1,
                connectTimeoutMs: 5000,
                batchBudgetMs: 5000,
                CancellationToken.None);

            racer.Save(path);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var entry = document.RootElement.GetProperty("Addresses").GetProperty(Sample);

        Assert.Equal(3, entry.GetProperty("Failures").GetInt32());
        Assert.False(File.Exists(path + ".tmp"), "临时文件必须被 Move 消费掉，不能留在磁盘上");
    }

    [Fact]
    public async Task Default_connect_path_wins_against_a_real_listener()
    {
        // 唯一保留真实 socket 的用例：它要证明「生产默认实现」这一根线是通的。
        // 用本机监听中的端口而不是不可达地址——握手由内核直接完成，不依赖任何路由或防火墙行为，
        // 即便在没有外网、甚至禁止外部连接的 CI 上也能稳定通过。
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            var racer = new UpstreamRacer(_log);
            var win = await racer.RaceAsync(
                new[] { IPAddress.Loopback },
                port,
                connectTimeoutMs: 5000,
                batchBudgetMs: 5000,
                CancellationToken.None);

            Assert.NotNull(win);
            Assert.Equal(IPAddress.Loopback, win!.Value.Ip);

            win.Value.Client.Dispose();
        }
        finally
        {
            listener.Stop();
        }
    }
}
