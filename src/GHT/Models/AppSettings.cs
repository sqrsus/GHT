using System.Text.Json.Serialization;

namespace GHT.Models;

/// <summary>
/// 应用配置。属性名与旧版（CloudflareSmartProxy）保持一致，因此旧 settings.json 可直接读取；
/// 旧配置中已废弃的字段（SpeedTestUrl / PreferredEdges / MitmEnabled ...）会被 System.Text.Json 静默忽略。
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
    /// 默认值从 1500 提到 3000 是有实测依据的：不可达的上游地址（SYN 无响应）
    /// 建连失败要 3000ms 左右才返回，1500ms 会在「还没试出结论」时就掐断竞速，
    /// 日志表现为「首连预算用尽」，而真实原因可能是候选里混了坏地址。
    /// 预算略大于失败耗时，才能让竞速给出确定结论而不是被截断。
    /// </remarks>
    public int ConnectBudgetMs { get; set; } = 3000;

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
    public int RaceConnectTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// 单批竞速的总时长上限。
    /// 应当小于 <see cref="ConnectBudgetMs"/>：批预算比总预算还大时，它永远不会生效，
    /// 等于只有一个隐形的总预算在起作用，调它没有任何意义。
    /// </summary>
    public int RaceBatchBudgetMs { get; set; } = 3000;

    /// <summary>整批候选都失败后的重赛轮数。</summary>
    public int RaceRetryRounds { get; set; } = 2;

    /// <summary>重赛前的等待时间，给后台扫描留出补充候选的机会。</summary>
    public int RaceRetryDelayMs { get; set; } = 800;

    // ── DNS 扫描与缓存 ──────────────────────────────────────
    /// <summary>扫描公共 DNS 时的并发数。</summary>
    public int DnsScanConcurrency { get; set; } = 128;

    /// <summary>单个 DNS 查询超时。</summary>
    public int DnsScanTimeoutMs { get; set; } = 2000;

    /// <summary>解析结果缓存的最小 TTL（秒）。</summary>
    public int DnsCacheMinTtlSec { get; set; } = 60;

    /// <summary>解析结果缓存的最大 TTL（秒）。</summary>
    public int DnsCacheMaxTtlSec { get; set; } = 600;

    /// <summary>
    /// 首批结果聚合窗口；0 表示不聚合，拿到第一批立刻返回。
    /// </summary>
    /// <remarks>
    /// 默认值从 200 提到 600：200ms 时通常只有国内公共 DNS 来得及答复，
    /// 而它们对同一域名的答案高度趋同，去重后往往只剩一个候选 —— 竞速退化成单点赌注。
    /// 600ms 能让境外 DNS（实测 45~100ms）也进入首批，候选从 1 个变成多个。
    /// </remarks>
    public int FirstBatchCollectMs { get; set; } = 600;

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
