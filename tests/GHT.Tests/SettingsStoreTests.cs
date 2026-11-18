using System.Text.Json;
using GHT.Models;
using GHT.Services;
using Xunit;

namespace GHT.Tests;

/// <summary>
/// 配置落盘的原子性。这些用例都在临时目录里做，不碰真实的 settings.json。
///
/// 它们防的是三类真实事故：
/// 1) 强杀进程后 settings.json 变成半截 JSON，下次启动整个配置丢失；
/// 2) 两个事件同时保存，最后落盘的是「端口存了、白名单没存」的混合体；
/// 3) 保存失败后目标文件被删掉，用户既没有新配置也没有旧配置。
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly LogService _log;

    public SettingsStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ght-settings-" + Guid.NewGuid().ToString("N"));
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

    private string Target => Path.Combine(_directory, "settings.json");

    private static AppSettings Sample(int port = 8081) => SettingsStore.Normalize(new AppSettings
    {
        ProxyPort = port,
        GlobalDnsWhitelist = "github.com, *.github.com"
    });

    [Fact]
    public void Save_then_load_round_trips()
    {
        Assert.True(SettingsStore.SaveTo(Sample(8081), _log, Target));

        var loaded = SettingsStore.LoadFrom(Target, _log);

        Assert.Equal(8081, loaded.ProxyPort);
        Assert.Equal("github.com, *.github.com", loaded.GlobalDnsWhitelist);
    }

    [Fact]
    public void Concurrent_saves_never_leave_a_torn_file()
    {
        // 第一次保存建立基线，第二次才会产生 .bak
        Assert.True(SettingsStore.SaveTo(Sample(8081), _log, Target));

        var ports = Enumerable.Range(8081, 16).ToArray();

        Parallel.ForEach(ports, port =>
        {
            var settings = Sample(port);
            settings.GlobalDnsWhitelist = "github.com, *.github.com, *.githubusercontent.com";
            SettingsStore.SaveTo(settings, _log, Target);
        });

        // 无论谁最后落盘，文件都必须是完整可读的，且端口来自那 16 个之一
        var loaded = SettingsStore.LoadFrom(Target, _log);
        Assert.Contains(loaded.ProxyPort, ports);
        Assert.Contains("github.com", loaded.GlobalDnsWhitelist);
    }

    [Fact]
    public void Leftover_temp_files_are_removed()
    {
        Assert.True(SettingsStore.SaveTo(Sample(), _log, Target));
        Assert.True(SettingsStore.SaveTo(Sample(), _log, Target));

        var leftovers = Directory.GetFiles(_directory, "*.tmp");
        Assert.Empty(leftovers);
    }

    [Fact]
    public void Corrupted_target_is_recovered_from_backup()
    {
        Assert.True(SettingsStore.SaveTo(Sample(8081), _log, Target));
        Assert.True(SettingsStore.SaveTo(Sample(9090), _log, Target));

        // 模拟「写到一半被强杀」：目标文件只剩半截，.bak 是上一次的完整内容
        File.WriteAllText(Target, "{ \"ProxyPort\": 90");

        var loaded = SettingsStore.LoadFrom(Target, _log);

        // 回退到备份（上一次保存的值），而不是悄悄用默认值把用户配置清空
        Assert.Equal(8081, loaded.ProxyPort);
    }

    [Fact]
    public void Non_object_root_is_rejected_instead_of_partially_applied()
    {
        File.WriteAllText(Target, "[1, 2, 3]");

        var loaded = SettingsStore.LoadFrom(Target, _log);

        // 没有备份可用时退回默认值，但绝不「读到一半就当成配置」
        Assert.Equal(7890, loaded.ProxyPort);
    }

    [Fact]
    public void Unknown_fields_are_ignored_but_known_fields_still_apply()
    {
        File.WriteAllText(
            Target,
            """
            {
              "ProxyPort": 8888,
              "ThisFieldDoesNotExist": 42,
              "AnotherOne": "ignore me"
            }
            """);

        var loaded = SettingsStore.LoadFrom(Target, _log);

        Assert.Equal(8888, loaded.ProxyPort);
    }

    [Fact]
    public void Save_failure_reports_false_and_leaves_no_temp_file()
    {
        // 目标位置被目录占住：文件替换必然失败
        var blocked = Path.Combine(_directory, "blocked.json");
        Directory.CreateDirectory(blocked);

        Assert.False(SettingsStore.SaveTo(Sample(), _log, blocked));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData("https://github.com/path")]  // 误贴 URL：协议与路径都不该被接受
    [InlineData("github.com:443")]           // 端口
    [InlineData("*.github.com/x")]           // 路径
    [InlineData("*.github.com:*")]           // 通配端口
    [InlineData("git hub.com")]              // 空格（会被拆成两个 token，这里单独校验函数）
    public void Whitelist_rejects_anything_that_is_not_a_host_pattern(string entry)
    {
        Assert.False(SettingsStore.IsValidHostPattern(entry));
    }

    [Theory]
    [InlineData("github.com")]
    [InlineData("*.github.com")]
    [InlineData("objects.githubusercontent.com")]
    [InlineData("localhost")]
    public void Whitelist_accepts_normal_host_patterns(string entry)
    {
        Assert.True(SettingsStore.IsValidHostPattern(entry));
    }

    [Fact]
    public void Whitelist_paste_of_urls_degrades_to_the_valid_part()
    {
        var settings = SettingsStore.Normalize(new AppSettings
        {
            GlobalDnsWhitelist = "https://github.com/login, github.com, *.github.com, github.com:443, "
                                 + "*.githubusercontent.com"
        });

        // 误贴的 URL 与带端口的条目被丢掉，合法的保留并去重（github.com 只出现一次）
        Assert.Equal("github.com, *.github.com, *.githubusercontent.com", settings.GlobalDnsWhitelist);
    }

    [Fact]
    public void Whitelist_is_capped()
    {
        var many = string.Join(", ", Enumerable.Range(1, SettingsStore.MaxWhitelistEntries + 40).Select(i => $"h{i}.example.com"));

        var settings = SettingsStore.Normalize(new AppSettings { GlobalDnsWhitelist = many });

        var count = settings.GlobalDnsWhitelist.Split(", ", StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.Equal(SettingsStore.MaxWhitelistEntries, count);
    }

    [Fact]
    public void Whitelist_duplicates_are_collapsed_case_insensitively()
    {
        var settings = SettingsStore.Normalize(new AppSettings
        {
            GlobalDnsWhitelist = "GitHub.com, github.com, *.GitHub.com"
        });

        Assert.Equal("github.com, *.github.com", settings.GlobalDnsWhitelist);
    }

    [Fact]
    public void Serialized_output_is_plain_utf8_without_bom()
    {
        Assert.True(SettingsStore.SaveTo(Sample(), _log, Target));

        var bytes = File.ReadAllBytes(Target);

        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "带 BOM 的 JSON 会让部分外部工具解析失败");

        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }
}
