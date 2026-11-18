using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace GHT.Services.Dns;

/// <summary>
/// 域名 → IP 的带 TTL 缓存。支持「渐进补充」：后台扫描每解析出一批新 IP 就追加进去，
/// 因此即使首连因为预算超时回落了系统 DNS，后续请求也能立刻用上新结果。
///
/// 读取路径刻意做成无锁：地址集合以<b>不可变数组</b>发布，合并时整体替换引用，
/// 读者拿到数组后直接使用。旧实现每次读取都要进全局锁并 <c>ToArray()</c> 复制一遍，
/// 高并发下这里是最热的一处竞争点。
///
/// 去重改用 <see cref="IPAddress"/> 相等语义（按地址族 + 字节比较），不再走 <c>ToString()</c>，
/// 既省掉每次合并的字符串分配，也避免把 <c>::ffff:a.b.c.d</c> 与 <c>a.b.c.d</c> 混为一谈。
/// </summary>
public sealed class DnsResultCache
{
    /// <summary>条目上限。超过后顺手清掉已过期的条目，防止长期运行无限增长。</summary>
    private const int MaxEntries = 512;

    private sealed class Entry
    {
        /// <summary>条目级锁：只保护本域名的一次合并，不再让全表共用一把锁。</summary>
        public readonly object Gate = new();

        public IPAddress[] Addresses = Array.Empty<IPAddress>();

        /// <summary>绝对过期时刻（UTC ticks），0 表示尚未写入。</summary>
        public long ExpiresAtTicks;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _entries.Count;

    public IReadOnlyList<IPAddress>? Get(string host)
    {
        if (!_entries.TryGetValue(host, out var entry))
        {
            return null;
        }

        var addresses = Volatile.Read(ref entry.Addresses);

        // 先看空再看过期：绝大多数未命中在这里就返回了，不需要取时间
        if (addresses.Length == 0)
        {
            return null;
        }

        var expiry = Volatile.Read(ref entry.ExpiresAtTicks);
        if (expiry == 0 || expiry <= DateTimeOffset.UtcNow.Ticks)
        {
            return null;
        }

        return addresses;
    }

    public void Merge(string host, IEnumerable<IPAddress> addresses, int ttlSeconds, bool preferIpv4)
    {
        var incoming = addresses as IPAddress[] ?? addresses.ToArray();
        if (incoming.Length == 0)
        {
            return;
        }

        var entry = _entries.GetOrAdd(host, _ => new Entry());

        lock (entry.Gate)
        {
            var current = entry.Addresses;

            // 先用现有集合判重，只有真的出现新地址时才分配新数组并排序
            var set = new HashSet<IPAddress>(current);
            var added = 0;

            foreach (var address in incoming)
            {
                if (address is not null && set.Add(address))
                {
                    added++;
                }
            }

            if (added > 0)
            {
                var merged = new IPAddress[set.Count];
                set.CopyTo(merged);
                Array.Sort(merged, (left, right) => CompareAddress(left, right, preferIpv4));

                // 整体替换引用：读者要么看到旧的完整数组，要么看到新的完整数组，不会看到半个
                Volatile.Write(ref entry.Addresses, merged);
            }

            var expiry = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, ttlSeconds)).Ticks;
            if (expiry > Volatile.Read(ref entry.ExpiresAtTicks))
            {
                Volatile.Write(ref entry.ExpiresAtTicks, expiry);
            }
        }

        PruneIfNeeded();
    }

    public void Clear()
    {
        // ConcurrentDictionary.Clear 本身线程安全，不需要额外加锁
        _entries.Clear();
    }

    /// <summary>删除单个域名的缓存（诊断面板里手动重查时用）。</summary>
    public bool Remove(string host) => _entries.TryRemove(host, out _);

    private void PruneIfNeeded()
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.Ticks;

        foreach (var pair in _entries)
        {
            var expiry = Volatile.Read(ref pair.Value.ExpiresAtTicks);
            if (expiry == 0 || expiry <= now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private static int CompareAddress(IPAddress left, IPAddress right, bool preferIpv4)
    {
        var leftV4 = left.AddressFamily == AddressFamily.InterNetwork;
        var rightV4 = right.AddressFamily == AddressFamily.InterNetwork;

        if (leftV4 != rightV4)
        {
            var leftFirst = preferIpv4 ? leftV4 : !leftV4;
            return leftFirst ? -1 : 1;
        }

        var leftBytes = left.GetAddressBytes();
        var rightBytes = right.GetAddressBytes();

        var length = Math.Min(leftBytes.Length, rightBytes.Length);
        for (var i = 0; i < length; i++)
        {
            if (leftBytes[i] != rightBytes[i])
            {
                return leftBytes[i] < rightBytes[i] ? -1 : 1;
            }
        }

        return leftBytes.Length.CompareTo(rightBytes.Length);
    }
}
