using System.Collections.Concurrent;

namespace GHT.Services;

/// <summary>
/// 并发连接准入闸门：一级全局、一级按上游主机。
///
/// 为什么需要它：接入循环原本每接受一条连接就无条件起一个任务，活跃连接只受客户端行为约束。
/// 一条隧道的受控内存约 600KB–1MB（两个 32KB 转发缓冲 + 两端各 256KB 套接字缓冲 + 任务与状态），
/// 没有上限时一个失控的客户端（或一次批量下载）就能把进程内存推到几个 GB。
///
/// 两级而不是一级的理由：全局上限只能保证「总量不失控」，却挡不住「单个站点吃掉全部额度」——
/// 那种情况下其余域名全部被 503，而内存并没有真正被释放（队列里的连接仍然活着）。
///
/// 计数纪律：
/// 1) 全局用 CAS 自增/自减，不持锁；
/// 2) 按主机用「一个主机一个计数对象 + 该对象的锁」，条目在计数归零时立即从字典移除，
///    因此字典规模天然不超过「活跃连接数」，不存在域名维度的无限增长；
/// 3) 归零与移除必须在同一把锁内完成，否则会出现「刚从字典里删掉、另一个线程又拿着旧计数加一」，
///    这条租约就绕过了上限。
/// </summary>
internal sealed class ConnectionBudget
{
    internal sealed class Counter
    {
        public string? Host;
        public int Value;
        public bool Retired;
    }

    /// <summary>
    /// 单主机租约。释放时按「计数对象」而不是「主机名」回退：
    /// 主机名的条目可能已被回收并重建，按名字释放会减到别人的计数上。
    /// </summary>
    internal readonly struct HostLease : IDisposable
    {
        private readonly ConnectionBudget? _owner;
        private readonly Counter? _counter;

        internal HostLease(ConnectionBudget owner, Counter counter)
        {
            _owner = owner;
            _counter = counter;
        }

        public void Dispose() => _owner?.ReleaseHost(_counter);
    }

    private readonly ConcurrentDictionary<string, Counter> _perHost = new(StringComparer.OrdinalIgnoreCase);

    private int _total;
    private int _hosts;
    private long _rejectedTotal;
    private long _rejectedHost;

    /// <summary>当前活跃连接数（含正在写拒绝响应的那些之外的全部已接入连接）。</summary>
    public int Total => Volatile.Read(ref _total);

    /// <summary>字典里仍在计数的上游主机数（诊断用；恒不超过活跃连接数）。</summary>
    public int TrackedHosts => Volatile.Read(ref _hosts);

    public long RejectedTotal => Interlocked.Read(ref _rejectedTotal);

    public long RejectedHost => Interlocked.Read(ref _rejectedHost);

    /// <summary>尝试占用一个全局名额。<paramref name="maxTotal"/> 为 0 表示不限。</summary>
    public bool TryEnter(int maxTotal)
    {
        while (true)
        {
            var current = Volatile.Read(ref _total);

            if (maxTotal > 0 && current >= maxTotal)
            {
                Interlocked.Increment(ref _rejectedTotal);
                return false;
            }

            if (Interlocked.CompareExchange(ref _total, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>释放全局名额。必须与一次成功的 <see cref="TryEnter"/> 一一对应。</summary>
    public void Exit() => Interlocked.Decrement(ref _total);

    /// <summary>
    /// 尝试占用一个上游主机名额。返回 false 时 <paramref name="lease"/> 是无操作值，
    /// 调用方无需判断是否拿到再决定要不要释放。
    /// </summary>
    public bool TryEnterHost(string host, int maxPerHost, out HostLease lease)
    {
        lease = default;

        if (maxPerHost <= 0 || string.IsNullOrEmpty(host))
        {
            return true;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var fresh = new Counter { Host = host };
            var counter = _perHost.GetOrAdd(host, fresh);

            if (ReferenceEquals(counter, fresh))
            {
                Interlocked.Increment(ref _hosts);
            }

            lock (counter)
            {
                if (counter.Retired)
                {
                    // 拿到的是一份刚被回收的计数：重取一次，否则这条租约不会计入上限
                    continue;
                }

                if (counter.Value >= maxPerHost)
                {
                    Interlocked.Increment(ref _rejectedHost);
                    return false;
                }

                counter.Value++;
                lease = new HostLease(this, counter);
                return true;
            }
        }

        // 极端竞争（同一瞬间反复归零/重建）下不再纠缠：放行这一条。
        // 把连接卡死的代价，远大于上限被短暂放宽一条。
        return true;
    }

    private void ReleaseHost(Counter? counter)
    {
        if (counter is null)
        {
            return;
        }

        lock (counter)
        {
            if (counter.Value > 0)
            {
                counter.Value--;
            }

            if (counter.Value > 0 || counter.Retired)
            {
                return;
            }

            counter.Retired = true;

            // 按 key+value 精确移除：并发下同一主机可能已经被换成了新计数
            if (counter.Host is { } host)
            {
                ((ICollection<KeyValuePair<string, Counter>>)_perHost)
                    .Remove(new KeyValuePair<string, Counter>(host, counter));
            }

            Interlocked.Decrement(ref _hosts);
        }
    }
}
