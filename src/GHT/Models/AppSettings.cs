using System.Text.Json.Serialization;

namespace GHT.Models;

/// <summary>
/// 应用配置。属性名与旧版（CloudflareSmartProxy）保持一致，因此旧 settings.json 可直接读取；
/// 旧配置中已废弃的字段（SpeedTestUrl / PreferredEdges / MitmEnabled ...）会被 System.Text.Json 静默忽略。
///
/// 默认值取向：<b>可用性优先</b>。宁可首连多花一两秒，也不要因为预算太紧而把「其实连得上」的候选掐断。
/// 三个与预算相关的默认值构成一组自洽的组合，改其中一个必须同时检查另外两个：
/// <code>
/// ConnectBudgetMs (12000) &gt; RaceConnectTimeoutMs (8000)           // 总预算要够一次尝试得出确定结论
/// RaceBatchBudgetMs (9000)  &gt;= RaceConnectTimeoutMs (8000)        // 批预算不能先于单地址超时到期
/// RaceBatchBudgetMs (9000)  &lt; ConnectBudgetMs (12000)             // 否则批预算永远不生效
/// </code>
/// 违反第一条会稳定复现「首连预算用尽」：每个地址都还没到自己的超时就被整体取消，
/// 既拿不到可用连接，也拿不到「这个地址不行」的结论。
/// </summary>
public sealed class AppSettings
{
    /// <summary>内置的默认加速域名清单（GitHub 及其静态资源域）。</summary>
    public const string DefaultWhitelist =
        "github.com, *.github.com, githubusercontent.com, *.githubusercontent.com, " +
        "githubassets.com, *.githubassets.com, github.io, *.github.io, " +
        "ghcr.io, *.ghcr.io, github.blog, *.github.blog, " +
        "githubcopilot.com, *.githubcopilot.com, githubstatus.com, *.githubstatus.com";

    // ── 代理 ────────────────────────────────────────────────
    /// <summary>本地代理监听端口（仅绑定 127.0.0.1）。</summary>
    public int ProxyPort { get; set; } = 7890;

    // ── 白名单与加速开关 ────────────────────────────────────
    /// <summary>需要走 DNS 加速的域名清单，逗号或换行分隔，支持 * 通配。</summary>
    public string GlobalDnsWhitelist { get; set; } = DefaultWhitelist;

    /// <summary>总开关：关闭后全部域名直接走系统 DNS。</summary>
    public bool DnsAccelEnabled { get; set; } = true;

    /// <summary>
    /// 首连预算（毫秒）。超时即先回落系统 DNS，DNS 扫描在后台继续。
    /// 设为 0 表示不限预算（等价于改造前「等扫描完成」的行为，作为回滚开关）。
    /// </summary>
    /// <remarks>
    /// 默认值 12000 是「可用性优先」的结果，必须大于 <see cref="RaceConnectTimeoutMs"/>：
    /// 预算比单地址超时还小的话，第一批里只要没有快速连通的地址，整批就会在各自超时之前被取消，
    /// 日志表现为「首连预算用尽」——而真实原因是预算把一次还没做完的尝试掐断了。
    /// 预算略大于失败耗时，竞速才能给出确定结论。设 0 表示不限（回滚开关），
    /// 代价是最坏要等到所有轮次跑完才回落系统 DNS。
    /// </remarks>
    public int ConnectBudgetMs { get; set; } = 12000;

    /// <summary>
    /// 加速池候选全部建连失败后，是否再用系统 DNS 兜底（默认开）。
    /// 系统 DNS 很可能解析出池里没有的另一组地址，直接回 502 对用户没有任何好处；
    /// 设为 false 可恢复「候选试完即失败」的严格语义（便于定位池质量问题）。
    /// </summary>
    public bool FallbackOnCandidatesFailed { get; set; } = true;

    // ── 竞速 ────────────────────────────────────────────────
    /// <summary>单批并发竞速的 IP 数量上限。</summary>
    public int RaceMaxIps { get; set; } = 8;

    /// <summary>单个 IP 的 TCP 建连超时。</summary>
    /// <remarks>
    /// 8000 而不是 5000：跨境链路在丢包/拥塞时，TCP 握手花 5~7 秒才完成并不罕见，
    /// 5 秒会把「慢但能用」的地址判死。放宽它的代价只是坏地址拖得更久，
    /// 而这里的目标是连得上，不是连得快。
    /// </remarks>
    public int RaceConnectTimeoutMs { get; set; } = 8000;

    /// <summary>
    /// 单批竞速的总时长上限。
    /// 必须满足 <c>RaceConnectTimeoutMs &lt;= RaceBatchBudgetMs &lt; ConnectBudgetMs</c>：
    /// 小于单地址超时会把还没试完的地址一起取消；大于等于总预算则永远不会生效。
    /// </summary>
    public int RaceBatchBudgetMs { get; set; } = 9000;

    /// <summary>整批候选都失败后的重赛轮数。</summary>
    public int RaceRetryRounds { get; set; } = 3;

    /// <summary>重赛前的等待时间，给后台扫描留出补充候选的机会。</summary>
    public int RaceRetryDelayMs { get; set; } = 1500;

    // ── DNS 扫描与缓存 ──────────────────────────────────────
    /// <summary>扫描公共 DNS 时的并发数。</summary>
    public int DnsScanConcurrency { get; set; } = 128;

    /// <summary>单个 DNS 查询超时。</summary>
    /// <remarks>3000 给跨境 DNS 留足往返：池里有不少境外解析器，2000 时它们常在窗口内答不完。</remarks>
    public int DnsScanTimeoutMs { get; set; } = 3000;

    /// <summary>解析结果缓存的最小 TTL（秒）。</summary>
    public int DnsCacheMinTtlSec { get; set; } = 300;

    /// <summary>解析结果缓存的最大 TTL（秒）。</summary>
    /// <remarks>
    /// 放宽到 300/1800 是为了减少「反复解析」：每一次重新解析都是一次重新赌候选，
    /// 已经验证能连上的地址多留一会儿，比反复撞新地址更容易连得上。
    /// 地址失效由竞速的健康度与失败退避兜住，不会因为缓存长而卡死在坏地址上。
    /// </remarks>
    public int DnsCacheMaxTtlSec { get; set; } = 1800;

    /// <summary>
    /// 首批结果聚合窗口；0 表示不聚合，拿到第一批立刻返回。
    /// </summary>
    /// <remarks>
    /// 默认 1200：200~600ms 通常只有国内公共 DNS 来得及答复，而它们对同一域名的答案高度趋同，
    /// 去重后往往只剩一个候选 —— 竞速退化成单点赌注。1200ms 能让境外 DNS（实测 45~100ms，
    /// 但排队与丢包重传会拉长）也进入首批，候选数量直接决定「有没有别的路可走」。
    /// </remarks>
    public int FirstBatchCollectMs { get; set; } = 1200;

    /// <summary>首批至少凑够多少个 IP 就提前返回。</summary>
    public int FirstBatchMinIps { get; set; } = 5;

    /// <summary>同一域名并发使用的 DNS 数量（每轮）。</summary>
    public int PoolResolveConcurrency { get; set; } = 24;

    /// <summary>竞速时优先使用 IPv4。</summary>
    public bool PreferIpv4 { get; set; } = true;

    // ── 宿主行为 ────────────────────────────────────────────
    /// <summary>关闭窗口时的行为：Ask / Tray / Exit。</summary>
    public string CloseAction { get; set; } = "Ask";

    /// <summary>开机自启（写入 HKCU Run 键）。</summary>
    public bool AutoStart { get; set; }

    // ── DNS 加速池 ──────────────────────────────────────────
    /// <summary>已应用的公共 DNS 列表（按延迟升序）。</summary>
    public List<DnsPoolEntry> DnsPool { get; set; } = new();

    /// <summary>加速池的探测目标域名。</summary>
    public string DnsPoolDomain { get; set; } = "github.com";

    /// <summary>加速池最后一次更新时间。</summary>
    public DateTimeOffset? DnsPoolUpdatedAt { get; set; }

    /// <summary>最后一次扫描的域名。</summary>
    public string DnsLastScanDomain { get; set; } = string.Empty;

    [JsonIgnore]
    public string CloseActionOrDefault =>
        CloseAction is "Ask" or "Tray" or "Exit" ? CloseAction : "Ask";
}
