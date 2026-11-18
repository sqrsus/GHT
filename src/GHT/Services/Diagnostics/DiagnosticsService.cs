using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GHT.Models;
using GHT.Services.Dns;

namespace GHT.Services.Diagnostics;

/// <summary>单台公共 DNS 在诊断中的表现。</summary>
public sealed record DiagnosticServerResult(string Ip, string Name, int LatencyMs, int AddressCount);

/// <summary>
/// 一次域名诊断的结果。刻意做成纯数据 + <see cref="ToText"/>，
/// 这样 UI 只负责展示与复制，网络逻辑一行都不在窗口里。
/// </summary>
public sealed record DiagnosticReport(
    string Domain,
    int Port,
    string Source,
    IReadOnlyList<DiagnosticServerResult> Servers,
    IReadOnlyList<IPAddress> Addresses,
    string? WinnerIp,
    long? ConnectMs,
    long TotalMs,
    string Verdict)
{
    public string ToText()
    {
        var text = new StringBuilder(1024);

        text.Append("目标：").Append(Domain).Append(':').Append(Port).AppendLine();
        text.Append("DNS 来源：").Append(Source).AppendLine();
        text.Append("结论：").Append(Verdict).AppendLine();
        text.Append("总耗时：").Append(TotalMs).Append(" ms").AppendLine();

        text.AppendLine();
        text.Append("解析到 ").Append(Addresses.Count).AppendLine(" 个候选 IP：");

        foreach (var address in Addresses.Take(12))
        {
            text.Append("  ").AppendLine(address.ToString());
        }

        if (Addresses.Count > 12)
        {
            text.Append("  …（其余 ").Append(Addresses.Count - 12).AppendLine(" 个略）");
        }

        text.AppendLine();

        if (WinnerIp is null)
        {
            text.AppendLine("TCP 建连：全部候选均失败");
        }
        else
        {
            text.Append("TCP 建连：胜出 ").Append(WinnerIp)
                .Append("（").Append(ConnectMs ?? 0).AppendLine(" ms）");
        }

        text.AppendLine();
        text.Append("公共 DNS 明细（共 ").Append(Servers.Count).AppendLine(" 台，按延迟排序）：");

        foreach (var server in Servers.OrderBy(s => s.LatencyMs).Take(15))
        {
            var name = string.IsNullOrEmpty(server.Name) ? "-" : server.Name;
            text.Append("  ").Append(server.Ip.PadRight(16))
                .Append(server.LatencyMs.ToString().PadLeft(5)).Append(" ms  ")
                .Append(server.AddressCount).Append(" 个 IP  ")
                .AppendLine(name);
        }

        if (Servers.Count > 15)
        {
            text.Append("  …（其余 ").Append(Servers.Count - 15).AppendLine(" 台略）");
        }

        return text.ToString();
    }
}

/// <summary>
/// 域名诊断：用加速池（为空则用内置清单前若干台）解析 A/AAAA，再对候选 IP 做一次 TCP 竞速。
/// 复用 <see cref="UdpDnsClient"/> 与 <see cref="UpstreamRacer"/>，不另写一套网络逻辑 ——
/// 否则诊断结果和真实代理行为会悄悄对不上。
/// </summary>
public sealed class DiagnosticsService
{
    /// <summary>加速池为空时用内置清单的前 N 台做诊断，避免为了看一眼结果先扫 1200 台。</summary>
    private const int FallbackServerCount = 24;

    private readonly LogService _log;

    public DiagnosticsService(LogService log)
    {
        _log = log;
    }

    public async Task<DiagnosticReport> RunAsync(
        string domain,
        int port,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var domain0 = (domain ?? string.Empty).Trim();

        var pool = settings.DnsPool ?? new List<DnsPoolEntry>();
        var servers = pool.Count > 0
            ? pool.Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Ip))
                .Select(e => new DnsServerInfo(e.Ip, e.Name))
                .ToList()
            : DnsServerList.Load().Take(FallbackServerCount).ToList();

        var source = pool.Count > 0
            ? $"加速池 · {servers.Count} 个"
            : $"内置清单前 {servers.Count} 个（加速池为空）";

        if (servers.Count == 0 || domain0.Length == 0)
        {
            return new DiagnosticReport(
                domain0,
                port,
                source,
                Array.Empty<DiagnosticServerResult>(),
                Array.Empty<IPAddress>(),
                null,
                null,
                total.ElapsedMilliseconds,
                domain0.Length == 0 ? "域名为空" : "没有可用的公共 DNS，请先扫描并应用加速池");
        }

        var results = new List<DiagnosticServerResult>(servers.Count);
        var addresses = new List<IPAddress>(16);
        var seen = new HashSet<IPAddress>();
        var gate = new object();
        var timeoutMs = Math.Clamp(settings.DnsScanTimeoutMs, 200, 20_000);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(settings.PoolResolveConcurrency, 1, 64),
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(servers, options, async (server, token) =>
        {
            if (!IPAddress.TryParse(server.Ip, out var address))
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();

            // A 与 AAAA 同问：只查 A 会把纯 IPv6 环境误判成「解析失败」
            var answers = await Task.WhenAll(
                    UdpDnsClient.QueryAsync(address, domain0, timeoutMs, token),
                    UdpDnsClient.QueryAsync(address, domain0, timeoutMs, token, ipv6: true))
                .ConfigureAwait(false);

            stopwatch.Stop();

            var found = 0;

            lock (gate)
            {
                foreach (var answer in answers)
                {
                    if (answer is not { } value)
                    {
                        continue;
                    }

                    foreach (var ip in value.Addresses)
                    {
                        if (seen.Add(ip))
                        {
                            addresses.Add(ip);
                            found++;
                        }
                    }
                }

                results.Add(new DiagnosticServerResult(
                    server.Ip,
                    server.Hostname,
                    (int)stopwatch.ElapsedMilliseconds,
                    found));
            }
        }).ConfigureAwait(false);

        IPAddress? winner = null;
        long? connectMs = null;

        if (addresses.Count > 0)
        {
            var preferIpv4 = settings.PreferIpv4;
            var ordered = addresses
                .OrderBy(a => preferIpv4
                    ? (a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                    : (a.AddressFamily == AddressFamily.InterNetwork ? 1 : 0))
                .ToList();

            var racer = new UpstreamRacer(_log);
            var raceWatch = Stopwatch.StartNew();

            var win = await racer.RaceAsync(
                    ordered,
                    port,
                    settings.RaceConnectTimeoutMs,
                    settings.RaceBatchBudgetMs,
                    cancellationToken)
                .ConfigureAwait(false);

            raceWatch.Stop();

            if (win is { } raceWin)
            {
                winner = raceWin.Ip;
                connectMs = (long)raceWatch.ElapsedMilliseconds;
                raceWin.Client.Dispose();
            }
        }

        var verdict = addresses.Count == 0
            ? "解析失败：所选 DNS 都无法解析该域名（可能被污染或域名不存在）"
            : winner is null
                ? "解析成功，但候选 IP 全部无法建立 TCP 连接"
                : "可用：解析与建连均成功";

        return new DiagnosticReport(
            domain0,
            port,
            source,
            results,
            addresses,
            winner?.ToString(),
            connectMs,
            total.ElapsedMilliseconds,
            verdict);
    }
}
