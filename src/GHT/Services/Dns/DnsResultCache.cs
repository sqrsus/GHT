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
///
/// 内存纪律（三处都曾经是无界或高频分配的）：
/// 1) 单域名地址数封顶 <see cref="MaxAddressesPerDomain"/>，超出的按「最早过期」淘汰；
/// 2) 排序键（地址字节）在合并时算一次，不再让比较器在 O(n log n) 次比较里重复分配数组；
/// 3) 「答案全是已知地址且无过期项」走快路径，只顺延 TTL，不重建字典/地址数组，也不排序。
/// </summary>
public sealed class DnsResultCache
{
    /// <summary>条目上限。超过后顺手清掉已过期的条目，防止长期运行无限增长。</summary>
    private const int MaxEntries = 512;

    /// <summary>
    /// 单域名的地址上限。一个域名被几十台 DNS 各答一遍时，答案会不断累积；
    /// 64 已经远超「首批 + 后台补齐」的实际需要（后台目标只有 16 个）。
    /// </summary>
    private const int MaxAddressesPerDomain = 64;

    /// <summary>清理的最小间隔。原来每次合并都做一次全表扫描，条目一多就变成每请求 O(n)。</summary>
    private const int PruneIntervalMs = 30_000;

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

    /// <summary>排序项：地址字节只取一次，比较器不再反复 <c>GetAddressBytes()</c>。</summary>
    private readonly record struct SortItem(IPAddress Address, long Expires, byte[] Bytes, bool IsV6);

    private sealed class AddressOrder : IComparer<SortItem>
    {
        private readonly bool _preferIpv4;

        public AddressOrder(bool preferIpv4) => _preferIpv4 = preferIpv4;

        public int Compare(SortItem left, SortItem right)
        {
            if (left.IsV6 != right.IsV6)
            {
                var leftFirst = _preferIpv4 ? !left.IsV6 : left.IsV6;
                return leftFirst ? -1 : 1;
            }

            var leftBytes = left.Bytes;
            var rightBytes = right.Bytes;
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

    private static readonly IComparer<SortItem> V4First = new AddressOrder(true);
    private static readonly IComparer<SortItem> V6First = new AddressOrder(false);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// 自己维护的条目计数。<c>ConcurrentDictionary.Count</c> 要拿全表的锁，
    /// 在「每次合并都判断一下是否超限」这种热路径上代价太高。
    /// </summary>
    private int _entryCount;

    private long _lastPruneMs;

    /// <summary>清理间隔。默认 30 秒；测试里调成 0 以便直接验证硬上限。</summary>
    internal int PruneInterval { get; set; } = PruneIntervalMs;

    public DnsResultCache()
        : this(null)
    {
    }

    /// <summary>测试构造：注入可控时钟，用来验证「不同 TTL 的地址各自过期」。</summary>
    internal DnsResultCache(Func<DateTimeOffset>? clock) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public int Count => Volatile.Read(ref _entryCount);

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

        var entry = GetOrCreateEntry(host);
        var now = NowTicks();
        var expiresAt = now + TimeSpan.FromSeconds(Math.Max(MinTtlSeconds, ttlSeconds)).Ticks;

        lock (entry.Gate)
        {
            MergeLocked(entry, incoming, expiresAt, now, preferIpv4);
        }

        PruneIfNeeded();
    }

    public void Clear()
    {
        // ConcurrentDictionary.Clear 本身线程安全，不需要额外加锁
        _entries.Clear();
        Volatile.Write(ref _entryCount, 0);
    }

    /// <summary>删除单个域名的缓存（诊断面板里手动重查时用）。</summary>
    public bool Remove(string host)
    {
        if (!_entries.TryRemove(host, out _))
        {
            return false;
        }

        Interlocked.Decrement(ref _entryCount);
        return true;
    }

    private Entry GetOrCreateEntry(string host)
    {
        if (_entries.TryGetValue(host, out var existing))
        {
            return existing;
        }

        var fresh = new Entry();

        // TryAdd + 再读一次，而不是 GetOrAdd：工厂在高并发下会多跑几次，
        // 在里面计数就会把 _entryCount 加多（它现在是要拿来判断上限的）。
        if (_entries.TryAdd(host, fresh))
        {
            Interlocked.Increment(ref _entryCount);
            return fresh;
        }

        return _entries.TryGetValue(host, out existing) ? existing : fresh;
    }

    private long NowTicks() => _clock().UtcDateTime.Ticks;

    /// <summary>调用方必须已持有 <paramref name="entry"/> 的锁。</summary>
    private static void MergeLocked(Entry entry, IPAddress[] incoming, long expiresAt, long now, bool preferIpv4)
    {
        var current = entry.Addresses;
        var currentExpiry = entry.ExpiresAtTicks;

        // 快路径：答案全是已知地址、且现有地址一个都没过期 —— 只需要顺延 TTL。
        // 长跑时这是最高频的一条路径（同一批 DNS 反复确认同一组地址），
        // 省掉字典、地址数组和排序三次分配；地址数组的引用不变，读者手上的快照继续有效。
        if (current.Length > 0
            && incoming.Length <= current.Length
            && Volatile.Read(ref entry.MinExpiresAtTicks) > now
            && AllKnown(current, incoming))
        {
            var nextExpiry = new long[current.Length];
            var min = long.MaxValue;

            for (var i = 0; i < current.Length; i++)
            {
                nextExpiry[i] = Contains(incoming, current[i]) ? expiresAt : currentExpiry[i];

                if (nextExpiry[i] < min)
                {
                    min = nextExpiry[i];
                }
            }

            Volatile.Write(ref entry.ExpiresAtTicks, nextExpiry);
            Volatile.Write(ref entry.MinExpiresAtTicks, min);
            return;
        }

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

        // 先把「最早过期」的那些挤出去，再排序：这样被裁掉的总是最久没被确认的地址，
        // 而不是排序之后只看地址字面值的前 64 个。
        if (merged.Count > MaxAddressesPerDomain)
        {
            var drop = merged.Count - MaxAddressesPerDomain;

            foreach (var pair in merged.OrderBy(item => item.Value).Take(drop).ToArray())
            {
                merged.Remove(pair.Key);
            }
        }

        var items = new SortItem[merged.Count];
        var cursor = 0;

        foreach (var pair in merged)
        {
            items[cursor++] = new SortItem(
                pair.Key,
                pair.Value,
                pair.Key.GetAddressBytes(),
                pair.Key.AddressFamily != AddressFamily.InterNetwork);
        }

        Array.Sort(items, preferIpv4 ? V4First : V6First);

        var nextAddresses = new IPAddress[items.Length];
        var nextExpiryValues = new long[items.Length];
        var earliest = long.MaxValue;

        for (var i = 0; i < items.Length; i++)
        {
            nextAddresses[i] = items[i].Address;
            nextExpiryValues[i] = items[i].Expires;

            if (items[i].Expires < earliest)
            {
                earliest = items[i].Expires;
            }
        }

        // 整体替换引用：读者要么看到旧的完整数组，要么看到新的完整数组，不会看到半个
        Volatile.Write(ref entry.ExpiresAtTicks, nextExpiryValues);
        Volatile.Write(ref entry.Addresses, nextAddresses);
        Volatile.Write(ref entry.MinExpiresAtTicks, earliest == long.MaxValue ? 0 : earliest);
    }

    private static bool AllKnown(IPAddress[] current, IPAddress[] incoming)
    {
        foreach (var address in incoming)
        {
            if (!Contains(current, address))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(IPAddress[] items, IPAddress address)
    {
        foreach (var item in items)
        {
            if (item.Equals(address))
            {
                return true;
            }
        }

        return false;
    }

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

    /// <summary>
    /// 条目数超限时清理。两步：先删「整条都已过期」的，仍然超限就按最早到期淘汰到 3/4 ——
    /// 后者是真正的硬上限，前面的 TTL 清理在「所有条目都还新鲜」时不会释放任何东西。
    /// </summary>
    private void PruneIfNeeded()
    {
        if (Volatile.Read(ref _entryCount) <= MaxEntries)
        {
            return;
        }

        var nowMs = Environment.TickCount64;
        var last = Volatile.Read(ref _lastPruneMs);

        if (nowMs - last < PruneInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastPruneMs, nowMs, last) != last)
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
            if (((ICollection<KeyValuePair<string, Entry>>)_entries)
                .Remove(pair))
            {
                Interlocked.Decrement(ref _entryCount);
            }
        }

        if (Volatile.Read(ref _entryCount) <= MaxEntries)
        {
            return;
        }

        var target = MaxEntries - (MaxEntries / 4);
        var overflow = Volatile.Read(ref _entryCount) - target;

        var stale = _entries
            .Select(pair => (Pair: pair, Min: Volatile.Read(ref pair.Value.MinExpiresAtTicks)))
            .OrderBy(item => item.Min)
            .Take(overflow)
            .ToArray();

        foreach (var item in stale)
        {
            if (((ICollection<KeyValuePair<string, Entry>>)_entries).Remove(item.Pair))
            {
                Interlocked.Decrement(ref _entryCount);
            }
        }
    }
}
