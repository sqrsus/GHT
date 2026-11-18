using System.IO.Pipes;

namespace GHT.Services;

/// <summary>
/// 单实例闸门：全局 Mutex 判断主实例，命名管道用于「再次双击时唤出已有窗口」。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Global\GHT.SingleInstance";
    private const string PipeName = "GHT.Wake";

    private Mutex? _mutex;
    private bool _owned;
    private CancellationTokenSource? _cts;

    public bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            _owned = createdNew;
            return createdNew;
        }
        catch (AbandonedMutexException)
        {
            _owned = true;
            return true;
        }
        catch
        {
            // 创建 Mutex 失败（权限/名称冲突）时退化为「允许多开」，不阻塞使用
            return true;
        }
    }

    /// <summary>通知已在运行的实例把窗口显示出来。</summary>
    public void SignalExisting()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(800);
            client.WriteByte(1);
            client.Flush();
        }
        catch
        {
            // 主实例没在监听就忽略
        }
    }

    public void StartListening(Action onWake)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    var buffer = new byte[1];
                    await server.ReadAsync(buffer, token).ConfigureAwait(false);

                    onWake();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    try
                    {
                        await Task.Delay(300, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }
                }
            }
        }, token);
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch
        {
            // ignore
        }

        _cts = null;

        if (_mutex is not null)
        {
            try
            {
                if (_owned)
                {
                    _mutex.ReleaseMutex();
                }
            }
            catch
            {
                // ignore
            }

            _mutex.Dispose();
            _mutex = null;
        }
    }
}
