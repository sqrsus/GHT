using System.Collections.Concurrent;
using System.Net;

namespace GHT.Services.Dns;

/// <summary>
/// 域名 → IP 的带 TTL 缓存。支持「渐进补充」：后台扫描每解析出一批新 IP 就追加进去，
/// 因此即使首连因为预算超时回落了系统 DNS，后续请求也能立刻用上新结果。
/// </summary>
public sealed class DnsResultCache
{
    private sealed class Entry
    {
        public readonly List<IPAddress> Addresses = new();
        public DateTimeOffset ExpiresAt = DateTimeOffset.MinValue;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public int Count => _entries.Count;

    public IReadOnlyList<IPAddress>? Get(string host)
    {
        if (!_entries.TryGetValue(host, out var entry))
        {
            return null;
        }

        lock (_gate)
        {
            if (entry.ExpiresAt <= DateTimeOffset.UtcNow || entry.Addresses.Count == 0)
            {
                return null;
            }

            return entry.Addresses.ToArray();
        }
    }

    public void Merge(string host, IEnumerable<IPAddress> addresses, int ttlSeconds, bool preferIpv4)
    {
        var entry = _entries.GetOrAdd(host, _ => new Entry());

        lock (_gate)
        {
            var known = new HashSet<string>(entry.Addresses.Select(a => a.ToString()), StringComparer.OrdinalIgnoreCase);

            foreach (var address in addresses)
            {
                if (known.Add(address.ToString()))
                {
                    entry.Addresses.Add(address);
                }
            }

            if (preferIpv4)
            {
                entry.Addresses.Sort(CompareAddress);
            }

            var expiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, ttlSeconds));
            if (expiry > entry.ExpiresAt)
            {
                entry.ExpiresAt = expiry;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    private static int CompareAddress(IPAddress left, IPAddress right)
    {
        var leftV4 = left.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        var rightV4 = right.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

        if (leftV4 != rightV4)
        {
            return leftV4 ? -1 : 1;
        }

        return string.CompareOrdinal(left.ToString(), right.ToString());
    }
}
