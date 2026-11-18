namespace GHT.Services;

/// <summary>极简文件日志：按天分文件 + 内存环形缓冲（供 UI 折叠面板显示 WARN 及以上）。</summary>
public sealed class LogService
{
    private const int RecentCapacity = 500;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly Queue<string> _recent = new();

    public LogService(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    /// <summary>每写一行触发一次（可能在任意线程）。</summary>
    public event Action<string>? LineWritten;

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
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";

        lock (_gate)
        {
            _recent.Enqueue(line);
            while (_recent.Count > RecentCapacity)
            {
                _recent.Dequeue();
            }

            try
            {
                var file = Path.Combine(_directory, $"ght-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine);
            }
            catch
            {
                // 日志写盘失败不影响主流程
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
}
