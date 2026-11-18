using System.Text;

namespace GHT.Services;

/// <summary>
/// 极简文件日志：按天分文件 + 单文件超限自动换卷 + 保留 14 天 + 内存环形缓冲（供 UI 折叠面板显示）。
///
/// 两条纪律：
/// 1) DEBUG 默认只进内存、不落盘 —— 竞速与首连预算是高频路径，长期运行会把磁盘写满。
///    需要排查时设环境变量 <c>GHT_LOG_DEBUG=1</c> 再启动。
/// 2) 行内容做清洗：换行被压平、超长被截断，避免代理转发来的畸形数据把日志文件搅成不可读的形态。
/// </summary>
public sealed class LogService
{
    private const int RecentCapacity = 500;
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private const int RetentionDays = 14;
    private const int MaxLineLength = 4000;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly Queue<string> _recent = new();

    private string _currentDate = string.Empty;
    private string _currentFile = string.Empty;
    private long _currentSize;
    private int _fileIndex;

    public LogService(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        Verbose = string.Equals(Environment.GetEnvironmentVariable("GHT_LOG_DEBUG"), "1", StringComparison.Ordinal);
        CleanupExpired();
    }

    /// <summary>每写一行触发一次（可能在任意线程）。</summary>
    public event Action<string>? LineWritten;

    /// <summary>是否把 DEBUG 也写进文件。</summary>
    public bool Verbose { get; }

    public void Debug(string message) => Write("DEBUG", message);

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} :: {exception.GetType().Name}: {exception.Message}");

    public IReadOnlyList<string> Recent()
    {
        lock (_gate)
        {
            return _recent.ToArray();
        }
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {Flatten(message)}";

        lock (_gate)
        {
            _recent.Enqueue(line);
            while (_recent.Count > RecentCapacity)
            {
                _recent.Dequeue();
            }

            if (level != "DEBUG" || Verbose)
            {
                AppendToFile(line);
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

    private void AppendToFile(string line)
    {
        try
        {
            var today = DateTime.Now.ToString("yyyyMMdd");
            var bytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;

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

            File.AppendAllText(_currentFile, line + Environment.NewLine);
            _currentSize += bytes;
        }
        catch
        {
            // 日志写盘失败不影响主流程
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
