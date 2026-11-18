using System.Text.Json;

namespace GHT.Services.Dns;

/// <summary>内置公共 DNS 清单（内嵌资源 public-dns-servers.json，约 1200 条）。</summary>
public sealed record DnsServerInfo(string Ip, string Hostname);

public static class DnsServerList
{
    private static readonly string[] Fallback =
    {
        "223.5.5.5", "223.6.6.6", "119.29.29.29", "180.76.76.76", "114.114.114.114",
        "1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4", "9.9.9.9", "208.67.222.222"
    };

    private static IReadOnlyList<DnsServerInfo>? _cached;
    private static readonly object Gate = new();

    public static IReadOnlyList<DnsServerInfo> Load()
    {
        lock (Gate)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            var list = new List<DnsServerInfo>(1400);

            try
            {
                var assembly = typeof(DnsServerList).Assembly;
                var resourceName = assembly
                    .GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("public-dns-servers.json", StringComparison.OrdinalIgnoreCase));

                if (resourceName is not null)
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream is not null)
                    {
                        using var document = JsonDocument.Parse(stream);
                        if (document.RootElement.TryGetProperty("servers", out var servers)
                            && servers.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in servers.EnumerateArray())
                            {
                                if (item.ValueKind != JsonValueKind.Object)
                                {
                                    continue;
                                }

                                if (item.TryGetProperty("isIpv6", out var isIpv6) && isIpv6.ValueKind == JsonValueKind.True)
                                {
                                    continue;
                                }

                                if (!item.TryGetProperty("ip", out var ipElement))
                                {
                                    continue;
                                }

                                var ip = ipElement.GetString();
                                if (string.IsNullOrWhiteSpace(ip))
                                {
                                    continue;
                                }

                                var hostname = item.TryGetProperty("hostname", out var hostElement)
                                    ? hostElement.GetString() ?? string.Empty
                                    : string.Empty;

                                list.Add(new DnsServerInfo(ip.Trim(), hostname.Trim()));
                            }
                        }
                    }
                }
            }
            catch
            {
                // 资源损坏时退回内置短清单
            }

            if (list.Count == 0)
            {
                list.AddRange(Fallback.Select(ip => new DnsServerInfo(ip, string.Empty)));
            }

            var deduped = list
                .GroupBy(s => s.Ip, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            _cached = deduped;
            return deduped;
        }
    }
}
