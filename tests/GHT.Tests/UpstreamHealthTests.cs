using System.Net;
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
/// </summary>
public sealed class UpstreamHealthTests : IDisposable
{
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

    [Fact]
    public async Task Real_failure_reports_address_and_elapsed_time()
    {
        var racer = new UpstreamRacer(_log);
        var reported = new List<(IPAddress Ip, long Ms)>();

        // 本机 1 号端口必然没有监听，连接会被立刻拒绝 —— 这是最稳定的「真实失败」样本
        var win = await racer.RaceAsync(
            new[] { IPAddress.Parse("127.0.0.1") },
            1,
            1000,
            2000,
            CancellationToken.None,
            (ip, ms) => reported.Add((ip, ms)));

        Assert.Null(win);
        Assert.Single(reported);
        Assert.Equal(IPAddress.Parse("127.0.0.1"), reported[0].Ip);
        Assert.True(reported[0].Ms >= 0, "耗时必须是有效值，用于判断是「快拒绝」还是「超时」");
    }

    [Fact]
    public async Task Health_survives_save_and_load_across_instances()
    {
        var first = Path.Combine(_directory, "health-a.json");

        var before = new UpstreamRacer(_log);
        await before.RaceAsync(
            new[] { IPAddress.Parse("127.0.0.1") },
            1,
            1000,
            2000,
            CancellationToken.None);

        before.Save(first);
        Assert.True(File.Exists(first), "保存后文件必须存在");

        // 新实例模拟「进程重启」
        var after = new UpstreamRacer(_log);
        after.Load(first);

        var second = Path.Combine(_directory, "health-b.json");
        after.Save(second);

        using var document = JsonDocument.Parse(File.ReadAllText(second));
        var entry = document.RootElement.GetProperty("Addresses").GetProperty("127.0.0.1");

        Assert.Equal(1, entry.GetProperty("Failures").GetInt32());
        Assert.True(entry.GetProperty("LastFailureMs").GetInt64() > 0, "时间必须是绝对时间戳，否则跨进程无效");
    }

    [Fact]
    public void Loading_a_missing_file_is_harmless()
    {
        var racer = new UpstreamRacer(_log);

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
        var racer = new UpstreamRacer(_log);
        var path = Path.Combine(_directory, "health-d.json");

        for (var i = 0; i < 3; i++)
        {
            await racer.RaceAsync(
                new[] { IPAddress.Parse("127.0.0.1") },
                1,
                connectTimeoutMs: 1000,
                batchBudgetMs: 2000,
                CancellationToken.None);

            racer.Save(path);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var entry = document.RootElement.GetProperty("Addresses").GetProperty("127.0.0.1");

        Assert.Equal(3, entry.GetProperty("Failures").GetInt32());
        Assert.False(File.Exists(path + ".tmp"), "临时文件必须被 Move 消费掉，不能留在磁盘上");
    }
}
