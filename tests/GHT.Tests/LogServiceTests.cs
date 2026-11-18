using System.Text;
using GHT.Services;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// 日志改成「队列 + 后台批量落盘」后，最容易退化的两点是：
/// 1) 退出时最后几行没刷出去（往往正是崩溃/退出原因）；
/// 2) 内存环形缓冲被异步写盘逻辑带偏，Recent() 拿到的不是最近 500 条。
/// </summary>
public sealed class LogServiceTests
{
    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ght-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Flushes_every_line_on_dispose()
    {
        var directory = CreateTempDirectory();

        try
        {
            using (var log = new LogService(directory))
            {
                for (var i = 0; i < 200; i++)
                {
                    log.Info($"line-{i}");
                }
            }

            var file = Directory.GetFiles(directory, "ght-*.log").Single();
            var text = File.ReadAllText(file, Encoding.UTF8);

            Assert.Contains("line-0", text);
            Assert.Contains("line-199", text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Flattens_newlines_in_a_single_line()
    {
        var directory = CreateTempDirectory();

        try
        {
            using (var log = new LogService(directory))
            {
                log.Warn("first\r\nsecond\nthird");
            }

            var file = Directory.GetFiles(directory, "ght-*.log").Single();
            var text = File.ReadAllText(file, Encoding.UTF8);

            Assert.Contains("first | second third", text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Recent_is_bounded()
    {
        var directory = CreateTempDirectory();

        try
        {
            using var log = new LogService(directory);

            for (var i = 0; i < 620; i++)
            {
                log.Info($"line-{i}");
            }

            var recent = log.Recent();

            Assert.Equal(500, recent.Count);
            Assert.Contains("line-619", recent[^1]);

            // 620 - 500 = 120：最早的 120 条已被挤出缓冲
            Assert.Contains("line-120", recent[0]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
