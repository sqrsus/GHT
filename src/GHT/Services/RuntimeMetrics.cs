using System.Diagnostics;

namespace GHT.Services;

/// <summary>
/// 运行期内存与并发的可观测指标。
///
/// 为什么要有它：此前统计只覆盖「代理做了什么」（请求、池胜、首连耗时），
/// 没有任何一项反映「进程为此付出了多少」—— 内存涨到 1GB 时，面板上看不出任何异常，
/// 只能靠任务管理器猜。这里补的是一组<b>可以直接对照基线</b>的量：
/// 托管堆大小、累计分配量、Gen0/1/2 回收次数、活跃/拒绝连接数、各字典的条目数与日志队列深度。
///
/// 采样本身必须便宜：全部是 O(1) 计数器读数，唯一略重的是 <see cref="GC.GetTotalMemory(bool)"/>，
/// 它不触发回收（<c>forceFullCollection: false</c>），只读当前提交的堆大小。
/// </summary>
public sealed class RuntimeMetrics
{
    private long _lastAllocatedBytes;
    private long _lastStamp;

    /// <summary>采样一次。第一次调用没有速率基线，分配速率为 0。</summary>
    public RuntimeMetricsSample Sample(
        int activeConnections,
        int maxConnections,
        long rejectedConnections,
        int cachedDomains,
        int trackedSweeps,
        int serverHealth,
        int addressHealth,
        int ipSources,
        int logQueueDepth)
    {
        var allocated = GC.GetTotalAllocatedBytes(precise: false);
        var stamp = Stopwatch.GetTimestamp();

        double allocRate = 0;

        if (_lastStamp != 0)
        {
            var seconds = (stamp - _lastStamp) / (double)Stopwatch.Frequency;

            // 采样间隔太短时差值全是噪声，直接给 0，避免面板上出现几十 MB/s 的假峰值
            if (seconds >= 0.2)
            {
                allocRate = Math.Max(0, allocated - _lastAllocatedBytes) / seconds;
            }
        }

        _lastAllocatedBytes = allocated;
        _lastStamp = stamp;

        return new RuntimeMetricsSample(
            activeConnections,
            maxConnections,
            rejectedConnections,
            GC.GetTotalMemory(forceFullCollection: false),
            allocated,
            (long)allocRate,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            cachedDomains,
            trackedSweeps,
            serverHealth,
            addressHealth,
            ipSources,
            logQueueDepth,
            ThreadPool.PendingWorkItemCount,
            ThreadPool.CompletedWorkItemCount);
    }
}

/// <summary>某一时刻的运行期快照（全部是计数，不含任何地址或域名）。</summary>
public readonly record struct RuntimeMetricsSample(
    int ActiveConnections,
    int MaxConnections,
    long RejectedConnections,
    long ManagedHeapBytes,
    long TotalAllocatedBytes,
    long AllocRateBytesPerSec,
    int Gen0,
    int Gen1,
    int Gen2,
    int CachedDomains,
    int TrackedSweeps,
    int ServerHealth,
    int AddressHealth,
    int IpSources,
    int LogQueueDepth,
    long ThreadPoolPending,
    long ThreadPoolCompleted);
