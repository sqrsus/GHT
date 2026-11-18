using System.Net;
using System.Net.Sockets;
using System.Text;
using GHT.Models;
using GHT.Services;

namespace GHT.Tests;

/// <summary>临时日志目录。测试结束后连同日志一起删掉，不在用户目录留垃圾。</summary>
internal sealed class TempLog : IDisposable
{
    public TempLog()
    {
        Directory = Path.Combine(Path.GetTempPath(), "ght-it-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Service = new LogService(Directory);
    }

    public string Directory { get; }

    public LogService Service { get; }

    public void Dispose()
    {
        Service.Dispose();

        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch
        {
            // ignore
        }
    }
}

internal static class Net
{
    /// <summary>取一个当前可用的回环端口。绑定后立刻释放，因此后续启动存在极小的竞争窗口。</summary>
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// 全部直连的配置：白名单清空后 <c>ConnectForHostAsync</c> 不会进加速池，
    /// 集成测试才能在没有任何外网、没有任何 DNS 的环境里跑。
    /// </summary>
    public static AppSettings DirectSettings() => SettingsStore.Normalize(new AppSettings
    {
        GlobalDnsWhitelist = string.Empty,
        DnsPool = new List<DnsPoolEntry>(),
        ConnectBudgetMs = 0
    });

    public static async Task<string> ReadUntilAsync(Stream stream, string marker, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[8192];
        var text = new StringBuilder();

        while (text.ToString().IndexOf(marker, StringComparison.Ordinal) < 0)
        {
            int read;

            try
            {
                read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read <= 0)
            {
                break;
            }

            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return text.ToString();
    }

    public static async Task<string> ReadToEndAsync(Stream stream, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[8192];
        var text = new StringBuilder();

        while (true)
        {
            int read;

            try
            {
                read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }

            if (read <= 0)
            {
                break;
            }

            text.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return text.ToString();
    }

    public static async Task WriteAsciiAsync(Stream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>精确读满 count 字节。用于「代理必须把请求体一字不差转过去」这类断言。</summary>
    public static async Task<byte[]> ReadExactAsync(Stream stream, int count, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[count];
        var filled = 0;

        while (filled < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled, count - filled), cts.Token).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            filled += read;
        }

        return buffer[..filled];
    }

    /// <summary>轮询直到条件成立，返回是否成立。用于等「连接被登记」这类异步可见状态。</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        return condition();
    }
}

/// <summary>
/// 本地上游服务器：每个接入的连接交给 <c>handler</c>，测试里用它扮演 GitHub / 任意上游。
/// 只监听回环，不依赖外网，因此在 CI 上也能稳定运行。
/// </summary>
internal sealed class FakeUpstream : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly List<string> _requests = new();
    private readonly object _gate = new();

    public FakeUpstream(Func<Stream, CancellationToken, Task> handler)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await handler(client.GetStream(), _cts.Token).ConfigureAwait(false);
                        }
                        catch
                        {
                            // 测试里上游出错不影响主断言
                        }
                    }
                });
            }
        });
    }

    public int Port { get; }

    /// <summary>收到的原始请求（由 handler 自行登记，便于断言转发出去的内容）。</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public void Record(string request)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            _listener.Stop();
        }
        catch
        {
            // ignore
        }

        await Task.WhenAny(_loop, Task.Delay(500)).ConfigureAwait(false);
        _cts.Dispose();
    }
}
