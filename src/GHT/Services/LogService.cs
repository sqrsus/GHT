using System.Text;
using System.Threading.Channels;

namespace GHT.Services;

/// <summary>
/// 极简文件日志：按天分文件 + 单文件超限自动换卷 + 保留 14 天 + 内存环形缓冲（供 UI 折叠面板显示）。
///
/// 三条纪律：
/// 1) <b>调用方永不碰磁盘</b>：日志先进入有界 Channel，由单个后台任务按批写盘。
///    旧实现把 <c>File.AppendAllText</c> 放在调用线程的锁里，杀软或磁盘抖动时会把所有代理线程一起拖住。
/// 2) DEBUG 默认只进内存、不落盘 —— 竞速与首连预算是高频路径，长期运行会把磁盘写满。
///    需要排查时设环境变量 <c>GHT_LOG_DEBUG=1</c> 再启动。
/// 3) 行内容做清洗：换行被压平、超长被截断，避免代理转发来的畸形数据把日志文件搅成不可读的形态。
/// </summary>
public sealed class LogService : IDisposable
{
    private const int RecentCapacity = 500;
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private const int RetentionDays = 14;
    private const int MaxLineLength = 4000;

    /// <summary>队列上限。写盘持续落后时丢最旧的（宁可丢早期日志，也要保住现场最近的记录）。</summary>
    private const int QueueCapacity = 8192;

    /// <summary>单批最大字节数：避免一次拼出巨大的字符串。</summary>
    private const int MaxBatchChars = 64 * 1024;

    /// <summary>保护内存环形缓冲。与文件写入分离，避免写盘时阻塞生产方。</summary>
    private readonly object _recentGate = new();

    /// <summary>只由后台写盘任务持有；生产方不参与，因此不会因为磁盘慢而阻塞。</summary>
    private readonly object _fileGate = new();

    private readonly Channel<string> _queue;
    private readonly Task _writer;
    private readonly string _directory;
    private readonly Queue<string> _recent = new();

    private string _currentDate = string.Empty;
    private string _currentFile = string.Empty;
    private long _currentSize;
    private int _fileIndex;
    private long _dropped;
    private int _disposed;

    public LogService(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        Verbose = string.Equals(Environment.GetEnvironmentVariable("GHT_LOG_DEBUG"), "1", StringComparison.Ordinal);

        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        CleanupExpired();
        _writer = Task.Run(WriterLoopAsync);
    }

    /// <summary>每写一行触发一次（可能在任意线程）。</summary>
    public event Action<string>? LineWritten;

    /// <summary>是否把 DEBUG 也写进文件。</summary>
    public bool Verbose { get; }

    /// <summary>因队列满或已关闭而被丢弃的行数（仅用于诊断，不进日志本身）。</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Debug(string message) => Write("DEBUG", message);

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} :: {exception.GetType().Name}: {exception.Message}");

    public IReadOnlyList<string> Recent()
    {
        lock (_recentGate)
        {
            return _recent.ToArray();
        }
    }

    /// <summary>
    /// 停止接收并等待队列写完。退出前必须调用，否则最后几行（往往正是退出原因）会丢。
    /// 幂等，重复调用安全。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _queue.Writer.TryComplete();
        }
        catch
        {
            // ignore
        }

        try
        {
            _writer.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // 写盘任务异常不应阻断退出
        }
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {Flatten(message)}";

        lock (_recentGate)
        {
            _recent.Enqueue(line);
            while (_recent.Count > RecentCapacity)
            {
                _recent.Dequeue();
            }
        }

        if (level != "DEBUG" || Verbose)
        {
            // TryWrite 不阻塞：队列满时由 BoundedChannelFullMode 丢掉最旧的一条。
            if (!_queue.Writer.TryWrite(line))
            {
                Interlocked.Increment(ref _dropped);
            }
        }

        try
        {
            LineWritten?.Invoke(line);
        }
        catch
        {
            // 订阅方异常不影响主流程
        }
    }

    private async Task WriterLoopAsync()
    {
        var batch = new StringBuilder(MaxBatchChars);

        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            Drain(batch);
        }

        // 通道已关闭：把残余项写完再退出
        Drain(batch);
    }

    private void Drain(StringBuilder batch)
    {
        batch.Clear();

        while (batch.Length < MaxBatchChars && _queue.Reader.TryRead(out var line))
        {
            batch.Append(line).Append(Environment.NewLine);
        }

        if (batch.Length == 0)
        {
            return;
        }

        try
        {
            AppendBatch(batch.ToString());
        }
        catch
        {
            // 日志写盘失败不影响主流程
        }
    }

    private void AppendBatch(string text)
    {
        lock (_fileGate)
        {
            var today = DateTime.Now.ToString("yyyyMMdd");
            var bytes = Encoding.UTF8.GetByteCount(text);

            if (_currentFile.Length == 0 || !string.Equals(today, _currentDate, StringComparison.Ordinal))
            {
                _currentDate = today;
                _fileIndex = 0;
                _currentFile = Path.Combine(_directory, $"ght-{today}.log");
                _currentSize = File.Exists(_currentFile) ? new FileInfo(_currentFile).Length : 0;
            }

            if (_currentSize + bytes > MaxFileBytes)
            {
                _fileIndex++;
                _currentFile = Path.Combine(_directory, $"ght-{today}-{_fileIndex}.log");
                _currentSize = File.Exists(_currentFile) ? new FileInfo(_currentFile).Length : 0;
            }

            File.AppendAllText(_currentFile, text, Encoding.UTF8);
            _currentSize += bytes;
        }
    }

    /// <summary>压平换行并截断超长内容，防止日志注入与单行爆量。</summary>
    private static string Flatten(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var text = message
            .Replace("\r\n", " | ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');

        return text.Length <= MaxLineLength ? text : text[..MaxLineLength] + "…";
    }

    private void CleanupExpired()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);

            foreach (var file in Directory.EnumerateFiles(_directory, "ght-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // 单个文件删不掉无所谓
                }
            }
        }
        catch
        {
            // 目录不可读时忽略
        }
    }
}
