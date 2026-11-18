using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace GHT.Services.Github;

/// <summary>
/// <c>api.github.com/meta</c> 的一份地址快照。只保留「客户端可以直接当作入口」的四个分组：
/// <c>web</c>（github.com 网页与 git 传输）、<c>git</c>、<c>api</c>、<c>pages</c>。
///
/// 刻意<b>不</b>收录 <c>hooks</c> / <c>importer</c> / <c>github_enterprise_importer</c> /
/// <c>actions</c> / <c>actions_macos</c>：前三个是 GitHub 主动连到你的服务器（webhook 回调、
/// 仓库导入），后两个是托管 runner 的出站地址。把它们当客户端出口用，只会拉长竞速、污染
/// 地址健康度，还可能把流量引到根本不做入站的机器上。
/// </summary>
public sealed class GithubMetaSnapshot
{
    public static readonly GithubMetaSnapshot Empty = new(
        Array.Empty<IPAddress>(),
        Array.Empty<IPAddress>(),
        Array.Empty<IPAddress>(),
        Array.Empty<IPAddress>(),
        DateTimeOffset.MinValue,
        fromCache: false,
        stale: false);

    public GithubMetaSnapshot(
        IReadOnlyList<IPAddress> web,
        IReadOnlyList<IPAddress> git,
        IReadOnlyList<IPAddress> api,
        IReadOnlyList<IPAddress> pages,
        DateTimeOffset fetchedAt,
        bool fromCache,
        bool stale)
    {
        Web = web;
        Git = git;
        Api = api;
        Pages = pages;
        FetchedAt = fetchedAt;
        FromCache = fromCache;
        Stale = stale;
    }

    public IReadOnlyList<IPAddress> Web { get; }

    public IReadOnlyList<IPAddress> Git { get; }

    public IReadOnlyList<IPAddress> Api { get; }

    public IReadOnlyList<IPAddress> Pages { get; }

    public DateTimeOffset FetchedAt { get; }

    /// <summary>本次快照来自磁盘缓存（而非刚拉取的响应）。</summary>
    public bool FromCache { get; }

    /// <summary>缓存已超过刷新周期但仍在可接受范围内，UI 用来提示「地址可能已过时」。</summary>
    public bool Stale { get; }

    public bool IsEmpty => Web.Count + Git.Count + Api.Count + Pages.Count == 0;

    public int TotalCount => Web.Count + Git.Count + Api.Count + Pages.Count;
}

/// <summary>
/// 从 <c>api.github.com/meta</c> 取 GitHub 官方公布的边缘地址，作为加速池候选的<b>补充来源</b>。
///
/// 它解决的问题只有两个（不要指望更多）：
/// 1) 公共 DNS 对 <c>github.com</c> 返回被投毒/被劫持的地址时，这里有一份可信的候选；
/// 2) GeoDNS 把你分到远端区域（实测同一份列表里既有 japaneast 也有 eastus / southafricanorth）时，
///    可以换一个区域试。
/// 它<b>不</b>改变跨境链路质量，也不覆盖 release 资产与 LFS（GitHub 明确说 meta 不是全量清单）。
///
/// 三条硬约束（都是实测踩出来的）：
/// 1) 只能按主机绑定分组：<c>pages</c> 的地址拿去服务 <c>github.com</c> 会返回 404；
/// 2) 实测同一份列表里存在「TCP 能通、TLS 直接失败」的地址，所以候选必须再过 TLS 校验
///    （见 <see cref="UpstreamValidator"/>），不能只靠竞速；
/// 3) 地址会变，必须定期重新拉取并落盘，绝不写进 hosts 或硬编码成常量。
/// </summary>
public sealed class GithubMetaProvider : IDisposable
{
    private const string MetaUrl = "https://api.github.com/meta";

    /// <summary>超过这个时长的缓存不再使用（GitHub 明说地址会变，一周已是容忍上限）。</summary>
    private static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(7);

    /// <summary>正常刷新周期。GitHub 说「不时更改」，一天一次足够，失败时下一次调用会重试。</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(12);

    /// <summary>每个 CIDR 段取多少个候选地址。/28 取 2 个：实测 Azure 区域入口常常正是网段首地址。</summary>
    private const int AddressesPerRange = 2;

    private readonly LogService _log;
    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>(host|count|天) → 已经轮换好的种子。避免每建一条连接就把 100 多个地址重排一遍。</summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<IPAddress>> _seedMemo =
        new(StringComparer.OrdinalIgnoreCase);

    private volatile GithubMetaSnapshot _snapshot = GithubMetaSnapshot.Empty;

    public GithubMetaProvider(LogService log)
        : this(log, Path.Combine(AppPaths.Root, "github-meta.json"))
    {
    }

    internal GithubMetaProvider(LogService log, string cachePath)
    {
        _log = log;
        _cachePath = cachePath;

        // 必须绕开系统代理：本程序自己就可能是系统代理，走代理取 meta 会绕回自身，
        // 而且取这份数据的意义正是「在不信任本地解析链路时仍能拿到官方地址」。
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(6)
        })
        {
            Timeout = TimeSpan.FromSeconds(12)
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GHT/1.3 (+https://github.com/sqrsus/GHT)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public GithubMetaSnapshot Snapshot => _snapshot;

    /// <summary>
    /// 启动时调用：先同步读磁盘缓存（几十 KB，几毫秒），再按需在后台刷新。
    /// 同步读是有意的 —— 首条连接不应该因为「还没读缓存」而退化成没有静态候选。
    /// </summary>
    public void BeginLoad()
    {
        LoadCached();

        if (NeedsRefresh())
        {
            _ = RefreshAsync(CancellationToken.None);
        }
    }

    public bool NeedsRefresh()
    {
        var snapshot = _snapshot;
        if (snapshot.IsEmpty)
        {
            return true;
        }

        return DateTimeOffset.UtcNow - snapshot.FetchedAt >= RefreshInterval;
    }

    /// <summary>给 UI 用的一行状态。不做任何 I/O。</summary>
    public string Describe()
    {
        var snapshot = _snapshot;
        if (snapshot.IsEmpty)
        {
            return "官方地址：未加载";
        }

        var age = DateTimeOffset.UtcNow - snapshot.FetchedAt;
        var ageText = age.TotalHours < 1
            ? $"{Math.Max(0, (int)age.TotalMinutes)} 分钟前"
            : $"{age.TotalHours:0.#} 小时前";

        var suffix = snapshot.Stale ? "，已过期" : snapshot.FromCache ? "，缓存" : string.Empty;
        return $"官方地址：{snapshot.TotalCount} 个（{ageText}{suffix}）";
    }

    /// <summary>
    /// 取某个主机的完整静态候选池（已去重、已按当日哈希轮换过顺序）。
    /// 只对「能确认同一套边缘会服务该主机」的主机返回地址，其余一律返回空 ——
    /// 猜错会拿到证书不匹配或直接 404 的后端。
    ///
    /// 返回<b>全部</b>候选而不是截断前 N 个，是为了让调用方按地址健康度排序后再取前 N
    /// （见 <see cref="UpstreamRacer.RankSeeds"/>）：否则每天轮换会把已经验证好用的地址挤出窗口，
    /// 学到的结论第二天就作废。
    /// </summary>
    public IReadOnlyList<IPAddress> GetPool(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return Array.Empty<IPAddress>();
        }

        var snapshot = _snapshot;
        if (snapshot.IsEmpty)
        {
            return Array.Empty<IPAddress>();
        }

        var groups = SelectGroups(host, snapshot);
        if (groups.Count == 0)
        {
            return Array.Empty<IPAddress>();
        }

        var day = DateTime.UtcNow.DayOfYear;
        var memoKey = $"{host}|{day}|{snapshot.FetchedAt.UtcTicks}";

        if (_seedMemo.TryGetValue(memoKey, out var cached))
        {
            return cached;
        }

        var pool = BuildSeeds(groups, day);
        _seedMemo[memoKey] = pool;
        return pool;
    }

    /// <summary>
    /// 主机 → 分组。这张表是整套机制里最容易做错的地方，规则只能来自「实测该组地址确实服务该主机」：
    /// <list type="bullet">
    /// <item><c>github.com</c>：web ∪ git（两组互相都能 200 服务该主机）；</item>
    /// <item><c>api.github.com</c>：只有 api；</item>
    /// <item>其它 <c>*.github.com</c>：git（codeload 等由同一套边缘服务）；</item>
    /// <item><c>github.io</c> / <c>*.github.io</c>：只有 pages。</item>
    /// </list>
    /// <c>githubusercontent.com</c> / <c>githubassets.com</c> / <c>ghcr.io</c> 在 meta 里没有对应分组，
    /// 因此不给静态候选，继续依赖 DNS 池。
    /// </summary>
    internal static List<IReadOnlyList<IPAddress>> SelectGroups(string host, GithubMetaSnapshot snapshot)
    {
        var groups = new List<IReadOnlyList<IPAddress>>(2);

        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return groups;
        }

        if (normalized == "github.com")
        {
            AddIfAny(groups, snapshot.Web);
            AddIfAny(groups, snapshot.Git);
            return groups;
        }

        if (normalized == "api.github.com")
        {
            AddIfAny(groups, snapshot.Api);
            return groups;
        }

        if (normalized.EndsWith(".github.com", StringComparison.Ordinal))
        {
            AddIfAny(groups, snapshot.Git);
            return groups;
        }

        if (normalized == "github.io" || normalized.EndsWith(".github.io", StringComparison.Ordinal))
        {
            AddIfAny(groups, snapshot.Pages);
            return groups;
        }

        return groups;
    }

    private static void AddIfAny(List<IReadOnlyList<IPAddress>> groups, IReadOnlyList<IPAddress> group)
    {
        if (group.Count > 0)
        {
            groups.Add(group);
        }
    }

    /// <summary>
    /// 合并分组去重后，按「IP+当日序号」的稳定哈希重排。
    ///
    /// 为什么要重排而不是保持原序：地址表是按 Azure 区域排列的，直接按原序取前 N 会永远只试同一两个区域。
    /// 为什么要按天固定：同一天内顺序稳定，地址健康度（EWMA / 失败退避）才有机会在同一批候选上积累；
    /// 而健康度排序（<see cref="UpstreamRacer.RankSeeds"/>）会把验证过的地址稳定顶到前面，轮换只影响未知地址。
    /// </summary>
    internal static IReadOnlyList<IPAddress> BuildSeeds(
        IReadOnlyList<IReadOnlyList<IPAddress>> groups,
        int day)
    {
        var pool = new List<IPAddress>(128);
        var seen = new HashSet<IPAddress>();

        foreach (var group in groups)
        {
            foreach (var address in group)
            {
                if (seen.Add(address))
                {
                    pool.Add(address);
                }
            }
        }

        if (pool.Count <= 1)
        {
            return pool;
        }

        var keyed = new List<(IPAddress Address, ulong Key)>(pool.Count);
        foreach (var address in pool)
        {
            keyed.Add((address, StableHash(address.ToString() + "#" + day)));
        }

        keyed.Sort((left, right) => left.Key.CompareTo(right.Key));

        var ordered = new List<IPAddress>(keyed.Count);
        foreach (var item in keyed)
        {
            ordered.Add(item.Address);
        }

        return ordered;
    }

    /// <summary>
    /// FNV-1a。不能用 <c>string.GetHashCode</c>：它每个进程都加随机种子，
    /// 会导致同一天内重启前后选出的候选完全不同，健康度学习随之失效。
    /// </summary>
    internal static ulong StableHash(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }

    // ── CIDR 展开 ───────────────────────────────────────────────────────

    /// <summary>
    /// 展开一个 CIDR 段。只处理 IPv4，且只处理 /25 及更细的段：
    /// <list type="bullet">
    /// <item>/32 取该地址本身；</item>
    /// <item>/25 ~ /31 取段首起 <paramref name="perRange"/> 个地址（跳过 .0/.255 这类网络/广播地址）；</item>
    /// <item>/24 及更宽的段（140.82.112.0/20、185.199.108.0/22 等 GitHub 自有 AS 的连续分配）直接跳过 ——
    /// 具体主机位由 DNS 给出，在这里猜偏移只会得到一堆不做入站的地址。</item>
    /// </list>
    /// </summary>
    internal static void ExpandCidr(string cidr, List<IPAddress> target, int perRange = AddressesPerRange)
    {
        if (string.IsNullOrWhiteSpace(cidr))
        {
            return;
        }

        var text = cidr.Trim();
        var slash = text.IndexOf('/');
        if (slash <= 0)
        {
            return;
        }

        if (!IPAddress.TryParse(text.Substring(0, slash), out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
        {
            // IPv6 入口暂不引入：与 PreferIpv4 的既有语义冲突，且双栈探测失败会拖慢竞速
            return;
        }

        if (!int.TryParse(text.AsSpan(slash + 1), out var prefix) || prefix < 25 || prefix > 32)
        {
            return;
        }

        var network = ToUInt32(address) & PrefixMask(prefix);
        var size = 1u << (32 - prefix);
        var count = Math.Min((uint)perRange, size);

        for (uint i = 0; i < count; i++)
        {
            var value = network + i;
            var lastOctet = (byte)(value & 0xFF);

            if (lastOctet is 0 or 255)
            {
                continue;
            }

            target.Add(FromUInt32(value));
        }
    }

    internal static GithubMetaSnapshot Parse(string json, DateTimeOffset fetchedAt, bool fromCache, bool stale)
    {
        var web = new List<IPAddress>();
        var git = new List<IPAddress>();
        var api = new List<IPAddress>();
        var pages = new List<IPAddress>();

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        ReadGroup(root, "web", web);
        ReadGroup(root, "git", git);
        ReadGroup(root, "api", api);
        ReadGroup(root, "pages", pages);

        return new GithubMetaSnapshot(web, git, api, pages, fetchedAt, fromCache, stale);
    }

    private static void ReadGroup(JsonElement root, string name, List<IPAddress> target)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                ExpandCidr(item.GetString() ?? string.Empty, target);
            }
        }
    }

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint value) =>
        new(new[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        });

    /// <summary>prefix 为 0 时不能写成 <c>0xFFFFFFFFu &lt;&lt; 32</c>：C# 的移位量按 5 位取模，会变成不移位。</summary>
    private static uint PrefixMask(int prefix) =>
        prefix <= 0 ? 0u : prefix >= 32 ? 0xFFFFFFFFu : 0xFFFFFFFFu << (32 - prefix);

    // ── 拉取与缓存 ──────────────────────────────────────────────────────

    private void LoadCached()
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return;
            }

            var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(_cachePath);
            if (age > MaxCacheAge)
            {
                _log.Warn($"GitHub 官方地址缓存已超过 {MaxCacheAge.TotalDays:0} 天，忽略：{_cachePath}");
                return;
            }

            var json = File.ReadAllText(_cachePath);
            var snapshot = Parse(json, new DateTimeOffset(File.GetLastWriteTimeUtc(_cachePath), TimeSpan.Zero), fromCache: true, stale: age >= RefreshInterval);

            _snapshot = snapshot;
            _seedMemo.Clear();
            _log.Info($"已载入 GitHub 官方地址缓存：web {snapshot.Web.Count} / git {snapshot.Git.Count} / api {snapshot.Api.Count} / pages {snapshot.Pages.Count}");
        }
        catch (Exception ex)
        {
            _log.Warn($"读取 GitHub 官方地址缓存失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>拉取并更新快照。失败时保留旧快照（旧地址大概率仍可用，比清空更有价值）。</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // 已有一次刷新在进行，不必排队
            return false;
        }

        try
        {
            string json;
            using (var response = await _http.GetAsync(MetaUrl, cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    _log.Warn($"拉取 GitHub 官方地址失败：HTTP {(int)response.StatusCode}");
                    return false;
                }

                json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            var fetchedAt = DateTimeOffset.UtcNow;
            var snapshot = Parse(json, fetchedAt, fromCache: false, stale: false);

            if (snapshot.IsEmpty)
            {
                _log.Warn("GitHub 官方地址响应为空或格式不认识，保留旧快照");
                return false;
            }

            _snapshot = snapshot;
            _seedMemo.Clear();

            SaveCache(json, fetchedAt);
            _log.Info($"GitHub 官方地址已刷新：web {snapshot.Web.Count} / git {snapshot.Git.Count} / api {snapshot.Api.Count} / pages {snapshot.Pages.Count}");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.Warn($"拉取 GitHub 官方地址失败：{ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void SaveCache(string json, DateTimeOffset fetchedAt)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath) ?? AppPaths.Root);

            var temp = _cachePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _cachePath, overwrite: true);

            // 拉取时间写进文件时间戳：缓存格式保持「就是 meta 的原始响应」，
            // 不需要额外的包装结构，也不会因为结构升级导致旧缓存读不出来。
            File.SetLastWriteTimeUtc(_cachePath, fetchedAt.UtcDateTime);
        }
        catch (Exception ex)
        {
            _log.Warn($"写入 GitHub 官方地址缓存失败：{ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _refreshGate.Dispose();
    }
}
