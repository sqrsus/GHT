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
/// TTL 语义：<b>每个地址各自记过期时刻</b>。旧实现整条缓存只存一个过期时刻并取「最大值」，
/// 于是「A 的记录 TTL=60s、B 的记录 TTL=600s」时，A 会在 60s 后仍被当成有效地址使用，
/// 最多能滞留到 600s。现在合并时：新出现的地址按本次 TTL 从当前时刻计时，
/// 已被 DNS 再次确认的地址顺延，没被确认的保留原到期时刻，读时逐个剔除。
/// </summary>
public sealed class DnsResultCache
{
    /// <summary>条目上限。超过后顺手清掉已过期的条目，防止长期运行无限增长。</summary>
    private const int MaxEntries = 512;

    /// <summary>TTL 下限：低于 5 秒的结果没有缓存价值，还会把扫描频率放大。</summary>
    private const int MinTtlSeconds = 5;

    private sealed class Entry
    {
        /// <summary>条目级锁：只保护本域名的一次合并，不再让全表共用一把锁。</summary>
        public readonly object Gate = new();

        public IPAddress[] Addresses = Array.Empty<IPAddress>();

        /// <summary>与 <see cref="Addresses"/> 一一对应的过期时刻（UTC ticks）。</summary>
        public long[] ExpiresAtTicks = Array.Empty<long>();

        /// <summary>所有地址里最早的那个过期时刻。读路径先看它，未过期就完全不加锁。</summary>
        public long MinExpiresAtTicks;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;

    public DnsResultCache()
        : this(null)
    {
    }

    /// <summary>测试构造：注入可控时钟，用来验证「不同 TTL 的地址各自过期」。</summary>
    internal DnsResultCache(Func<DateTimeOffset>? clock) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

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

        var now = NowTicks();
        if (now < Volatile.Read(ref entry.MinExpiresAtTicks))
        {
            return addresses;
        }

        // 有条目过期（每个 TTL 周期才会走到这里一次）：只剔除过期的那几个地址，
        // 不能因为「有一个过期」就把整批仍然有效的候选一起丢掉。
        lock (entry.Gate)
        {
            var alive = PruneLocked(entry, now);
            return alive.Length == 0 ? null : alive;
        }
    }

    public void Merge(string host, IEnumerable<IPAddress> addresses, int ttlSeconds, bool preferIpv4)
    {
        var incoming = addresses as IPAddress[] ?? addresses.ToArray();
        if (incoming.Length == 0)
        {
            return;
        }

        var entry = _entries.GetOrAdd(host, _ => new Entry());
        var now = NowTicks();
        var expiresAt = now + TimeSpan.FromSeconds(Math.Max(MinTtlSeconds, ttlSeconds)).Ticks;

        lock (entry.Gate)
        {
            var current = entry.Addresses;
            var currentExpiry = entry.ExpiresAtTicks;

            // 地址 → 过期时刻。已过期的旧地址不再搬进新表，顺手完成一次清理。
            var merged = new Dictionary<IPAddress, long>(current.Length + incoming.Length);

            for (var i = 0; i < current.Length; i++)
            {
                if (currentExpiry[i] > now)
                {
                    merged[current[i]] = currentExpiry[i];
                }
            }

            var changed = false;

            foreach (var address in incoming)
            {
                if (address is null)
                {
                    continue;
                }

                // 刚被 DNS 确认过一次：TTL 从此刻重新计时，而不是在旧值与新值之间取 max/min 去猜
                merged[address] = expiresAt;
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            var nextAddresses = new IPAddress[merged.Count];
            var nextExpiry = new long[merged.Count];
            var cursor = 0;
            var min = long.MaxValue;

            foreach (var pair in merged)
            {
                nextAddresses[cursor] = pair.Key;
                nextExpiry[cursor] = pair.Value;

                if (pair.Value < min)
                {
                    min = pair.Value;
                }

                cursor++;
            }

            Array.Sort(
                nextAddresses,
                nextExpiry,
                Comparer<IPAddress>.Create((left, right) => CompareAddress(left, right, preferIpv4)));

            // 整体替换引用：读者要么看到旧的完整数组，要么看到新的完整数组，不会看到半个
            Volatile.Write(ref entry.ExpiresAtTicks, nextExpiry);
            Volatile.Write(ref entry.Addresses, nextAddresses);
            Volatile.Write(ref entry.MinExpiresAtTicks, min);
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

    private long NowTicks() => _clock().UtcDateTime.Ticks;

    /// <summary>在条目锁内剔除已过期地址，并把新数组发布出去。返回仍然有效的地址。</summary>
    private static IPAddress[] PruneLocked(Entry entry, long now)
    {
        var addresses = entry.Addresses;
        var expiry = entry.ExpiresAtTicks;

        var alive = 0;
        for (var i = 0; i < addresses.Length && i < expiry.Length; i++)
        {
            if (expiry[i] > now)
            {
                alive++;
            }
        }

        if (alive == addresses.Length)
        {
            return addresses;
        }

        var nextAddresses = new IPAddress[alive];
        var nextExpiry = new long[alive];
        var cursor = 0;
        var min = long.MaxValue;

        for (var i = 0; i < addresses.Length && i < expiry.Length; i++)
        {
            if (expiry[i] <= now)
            {
                continue;
            }

            nextAddresses[cursor] = addresses[i];
            nextExpiry[cursor] = expiry[i];

            if (expiry[i] < min)
            {
                min = expiry[i];
            }

            cursor++;
        }

        Volatile.Write(ref entry.ExpiresAtTicks, nextExpiry);
        Volatile.Write(ref entry.Addresses, nextAddresses);
        Volatile.Write(ref entry.MinExpiresAtTicks, min == long.MaxValue ? 0 : min);

        return nextAddresses;
    }

    private void PruneIfNeeded()
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        var now = NowTicks();

        foreach (var pair in _entries)
        {
            var min = Volatile.Read(ref pair.Value.MinExpiresAtTicks);
            if (min != 0 && min > now)
            {
                continue;
            }

            // 按 key+value 精确移除：并发合并可能刚把同一个 key 换成新条目，直接 TryRemove(key) 会误删
            var collection = (ICollection<KeyValuePair<string, Entry>>)_entries;
            collection.Remove(pair);
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
