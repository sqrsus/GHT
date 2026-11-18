using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GHT.Models;
using GHT.Services.Http;

namespace GHT.Services;

/// <summary>
/// 本地 HTTP 代理（仅监听回环地址）。
///
/// 处理两类请求：
/// 1) <c>CONNECT host:port</c>：选路后建立 TCP 隧道，两端字节原样转发，TLS 端到端，代理不参与加解密。
/// 2) absolute-form 明文 HTTP（<c>GET http://host/path</c>）：重写为 origin-form 后转发，响应按 EOF 回灌。
///
/// 选路（见 <see cref="ConnectForHostAsync"/>）：命中白名单 → 加速池解析 + 多 IP 竞速；否则系统 DNS 直连。
/// 预算只包住「选路」，绝不包住数据转发，否则长下载/长连接会被预算掐断。
///
/// 生命周期用信号量 + 状态机串行化：UI 连点、托盘菜单与退出清理可能同时触发启动/停止，
/// 只靠 <c>IsRunning</c> 判断会出现两个监听器（其中一个无人持有、也无人停止）。
/// </summary>
public sealed class ProxyServer : IAsyncDisposable
{
    private const int MaxHeadBytes = 32 * 1024;
    private const int RelayBufferSize = 32 * 1024;
    private const int TunnelIdleMs = 60_000;
    private const int ResponseIdleMs = 120_000;
    private const int DirectConnectTimeoutMs = 8_000;

    /// <summary>单块上限。远超正常分块大小，但能挡住「分块长度写成 2GB」这类攻击。</summary>
    private const int MaxChunkBytes = 64 * 1024 * 1024;

    private const int MaxTrailerBytes = 8 * 1024;
    private const int MaxChunkLineLength = 8 * 1024;

    private enum LifecycleState
    {
        Stopped,
        Running,
        Stopping
    }

    private readonly LogService _log;
    private readonly ResolverHub _resolver;
    private readonly UpstreamRacer _racer;
    private readonly ProxyStatistics _statistics = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private volatile AppSettings _settings;
    private volatile DomainWildcardList _whitelist;
    private volatile LifecycleState _state = LifecycleState.Stopped;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _port;

    public ProxyServer(AppSettings settings, LogService log)
    {
        _settings = settings;
        _log = log;
        _resolver = new ResolverHub(settings, log);
        _racer = new UpstreamRacer(log);
        _whitelist = DomainWildcardList.Parse(settings.GlobalDnsWhitelist);
    }

    public ProxyStatistics Statistics => _statistics;

    public ResolverHub Resolver => _resolver;

    public bool IsRunning => _state == LifecycleState.Running;

    public int Port => _port;

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        _whitelist = DomainWildcardList.Parse(settings.GlobalDnsWhitelist);
        _resolver.ApplySettings(settings);
    }

    public void UpdatePool(IReadOnlyList<DnsPoolEntry> pool) => _resolver.UpdatePool(pool);

    // ── 生命周期 ────────────────────────────────────────────────────────

    /// <summary>启动监听。已在运行时为幂等空操作；正在停止时直接拒绝，避免状态被覆盖。</summary>
    public async Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_state == LifecycleState.Running)
            {
                return;
            }

            if (_state == LifecycleState.Stopping)
            {
                throw new InvalidOperationException("代理正在停止，请稍后重试");
            }

            TcpListener? listener = null;

            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();

                var cts = new CancellationTokenSource();
                var loop = Task.Run(() => AcceptLoopAsync(listener, cts.Token), CancellationToken.None);

                _listener = listener;
                _cts = cts;
                _acceptLoop = loop;
                _port = port;
                _state = LifecycleState.Running;

                _log.Info($"代理已启动：127.0.0.1:{port}");
            }
            catch
            {
                try
                {
                    listener?.Stop();
                }
                catch
                {
                    // ignore
                }

                _state = LifecycleState.Stopped;
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>停止监听并等待接入循环退出。整个动作持锁，保证与 <see cref="StartAsync"/> 串行。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_state == LifecycleState.Stopped)
            {
                return;
            }

            _state = LifecycleState.Stopping;

            var listener = _listener;
            var cts = _cts;
            var loop = _acceptLoop;

            _listener = null;
            _cts = null;
            _acceptLoop = null;

            try
            {
                cts?.Cancel();
            }
            catch
            {
                // ignore
            }

            try
            {
                listener?.Stop();
            }
            catch
            {
                // ignore
            }

            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
            }

            cts?.Dispose();
            _state = LifecycleState.Stopped;
            _log.Info("代理已停止");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // ── 接入 ────────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _log.Warn($"接受连接失败：{ex.SocketErrorCode}");
                try
                {
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, token), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        _statistics.AddRequest();

        try
        {
            using (client)
            {
                client.NoDelay = true;

                var stream = client.GetStream();
                HeadReadResult? headRead;

                try
                {
                    headRead = await HttpHeadReader.ReadAsync(stream, MaxHeadBytes, token).ConfigureAwait(false);
                }
                catch (ProxyProtocolException ex)
                {
                    await WriteErrorAsync(stream, 400, "Bad Request", ex.Message, token).ConfigureAwait(false);
                    return;
                }

                if (headRead is null)
                {
                    return;
                }

                HttpHead head;

                try
                {
                    head = HttpHead.Parse(headRead.HeadText);
                }
                catch (ProxyProtocolException ex)
                {
                    await WriteErrorAsync(stream, 400, "Bad Request", ex.Message, token).ConfigureAwait(false);
                    return;
                }

                var body = new BufferedReadStream(stream, headRead.Leftover);

                if (string.Equals(head.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleConnectAsync(stream, body, head, token).ConfigureAwait(false);
                }
                else
                {
                    await HandlePlainAsync(stream, body, head, token).ConfigureAwait(false);
                }
            }
        }
        catch (UpstreamUnavailableException ex)
        {
            _statistics.AddError();
            _log.Warn($"上游不可用：{ex.Message}");
        }
        catch (OperationCanceledException)
        {
            // 客户端断开或代理停止
        }
        catch (IOException)
        {
            // 对端提前关闭，属正常现象
        }
        catch (Exception ex)
        {
            _statistics.AddError();
            _log.Error("处理连接时出错", ex);
        }
    }

    // ── CONNECT 隧道 ────────────────────────────────────────────────────

    private async Task HandleConnectAsync(Stream clientStream, Stream clientInput, HttpHead head, CancellationToken token)
    {
        var (host, port) = SplitHostPort(head.Target, 443);
        if (string.IsNullOrWhiteSpace(host))
        {
            await WriteErrorAsync(clientStream, 400, "Bad Request", "CONNECT 目标非法", token).ConfigureAwait(false);
            return;
        }

        TcpClient upstream;

        try
        {
            upstream = await ConnectForHostAsync(host, port, token).ConfigureAwait(false);
        }
        catch (UpstreamUnavailableException ex)
        {
            _statistics.AddError();
            await WriteErrorAsync(clientStream, 502, "Bad Gateway", ex.Message, token).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException)
        {
            _statistics.AddError();
            await WriteErrorAsync(clientStream, 502, "Bad Gateway", $"{host}:{port} 建连失败：{ex.Message}", token)
                .ConfigureAwait(false);
            return;
        }

        using (upstream)
        {
            await clientStream.WriteAsync(ProxyProtocol.ConnectEstablished, token).ConfigureAwait(false);
            await clientStream.FlushAsync(token).ConfigureAwait(false);

            var upstreamStream = upstream.GetStream();
            await RelayAsync(clientInput, clientStream, upstreamStream, token).ConfigureAwait(false);
            _statistics.AddTunnel();
        }
    }

    // ── 明文 HTTP ───────────────────────────────────────────────────────

    private async Task HandlePlainAsync(Stream clientStream, Stream clientInput, HttpHead head, CancellationToken token)
    {
        if (!head.Target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(clientStream, 400, "Bad Request", "仅支持 http:// 绝对地址或无体 CONNECT", token)
                .ConfigureAwait(false);
            return;
        }

        if (!Uri.TryCreate(head.Target, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            await WriteErrorAsync(clientStream, 400, "Bad Request", "请求地址非法", token).ConfigureAwait(false);
            return;
        }

        TcpClient upstream;

        try
        {
            upstream = await ConnectForHostAsync(uri.Host, uri.Port, token).ConfigureAwait(false);
        }
        catch (UpstreamUnavailableException ex)
        {
            _statistics.AddError();
            await WriteErrorAsync(clientStream, 502, "Bad Gateway", ex.Message, token).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException)
        {
            _statistics.AddError();
            await WriteErrorAsync(clientStream, 502, "Bad Gateway", $"{uri.Host}:{uri.Port} 建连失败：{ex.Message}", token)
                .ConfigureAwait(false);
            return;
        }

        using (upstream)
        {
            var upstreamStream = upstream.GetStream();

            var request = new StringBuilder(512);
            request.Append(head.Method).Append(' ');

            var path = uri.PathAndQuery;
            request.Append(string.IsNullOrEmpty(path) ? "/" : path);
            request.Append(" HTTP/1.1\r\n");

            var hasHost = false;

            foreach (var header in head.Headers)
            {
                if (ProxyProtocol.IsHopByHopHeader(header.Key))
                {
                    continue;
                }

                // 同时出现 Transfer-Encoding: chunked 与 Content-Length 时，
                // 保留 CL 会让上游按长度读，而我们按分块写 —— 帧必然错位，必须以 TE 为准丢掉 CL。
                if (head.IsChunked && header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    hasHost = true;
                }

                request.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }

            if (!hasHost && !string.IsNullOrEmpty(uri.Authority))
            {
                request.Append("Host: ").Append(uri.Authority).Append("\r\n");
            }

            // 强制上游单次连接：响应体以 EOF 结束，回灌逻辑最简且不会错帧。
            request.Append("Connection: close\r\n\r\n");

            await ProxyProtocol.WriteAsciiAsync(upstreamStream, request.ToString(), token).ConfigureAwait(false);

            if (head.IsChunked)
            {
                await CopyChunkedAsync(clientInput, upstreamStream, token).ConfigureAwait(false);
            }
            else if (head.ContentLength is { } contentLength && contentLength > 0)
            {
                await CopyExactlyAsync(clientInput, upstreamStream, contentLength, token).ConfigureAwait(false);
            }

            await upstreamStream.FlushAsync(token).ConfigureAwait(false);

            // 响应按原始 framing 直接回灌（chunked 保持 chunked），并带空闲超时，
            // 避免上游无视 Connection: close 时把连接吊死。
            await PumpAsync(upstreamStream, clientStream, ResponseIdleMs, token).ConfigureAwait(false);
            await clientStream.FlushAsync(token).ConfigureAwait(false);
        }
    }

    // ── 选路 ────────────────────────────────────────────────────────────

    private async Task<TcpClient> ConnectForHostAsync(string host, int port, CancellationToken token)
    {
        var settings = _settings;
        var whitelist = _whitelist;

        var accelerate = settings.DnsAccelEnabled
                         && whitelist is { IsEmpty: false }
                         && (port == 80 || port == 443)
                         && whitelist.IsMatch(host);

        if (!accelerate)
        {
            return await ConnectDirectAsync(host, port, token).ConfigureAwait(false);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (settings.ConnectBudgetMs > 0)
        {
            budget.CancelAfter(settings.ConnectBudgetMs);
        }

        TcpClient? client;
        var exhausted = false;

        try
        {
            (client, exhausted) = await ConnectViaPoolAsync(host, port, budget.Token, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // 预算到点：不是失败，回落系统 DNS，后台扫描继续
            client = null;
            exhausted = false;
        }

        if (client is not null)
        {
            return client;
        }

        if (exhausted)
        {
            throw new UpstreamUnavailableException($"{host}:{port} 的全部候选 IP 均连不上");
        }

        _statistics.AddBudgetFallback();
        _log.Debug($"{host}:{port} 首连预算用尽，回落系统 DNS（后台扫描继续）");
        return await ConnectDirectAsync(host, port, token).ConfigureAwait(false);
    }

    private async Task<(TcpClient? Client, bool Exhausted)> ConnectViaPoolAsync(
        string host,
        int port,
        CancellationToken budgetToken,
        CancellationToken clientToken)
    {
        var candidates = await _resolver.ResolveAsync(host, budgetToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return (null, false);
        }

        var settings = _settings;
        var remaining = new List<IPAddress>(candidates);
        var rounds = Math.Clamp(settings.RaceRetryRounds, 0, 5);
        var maxIps = Math.Clamp(settings.RaceMaxIps, 1, 64);

        for (var round = 0; round <= rounds; round++)
        {
            while (remaining.Count > 0)
            {
                var batch = new List<IPAddress>(maxIps);
                for (var i = 0; i < maxIps && i < remaining.Count; i++)
                {
                    batch.Add(remaining[i]);
                }

                remaining.RemoveRange(0, batch.Count);

                var win = await _racer
                    .RaceAsync(batch, port, settings.RaceConnectTimeoutMs, settings.RaceBatchBudgetMs, budgetToken)
                    .ConfigureAwait(false);

                if (win is { } winner)
                {
                    _statistics.AddRace();
                    _statistics.AddPoolWin();
                    _statistics.SetWinner(host, winner.Ip.ToString());
                    return (winner.Client, false);
                }

                budgetToken.ThrowIfCancellationRequested();
            }

            if (round >= rounds)
            {
                break;
            }

            await Task.Delay(Math.Clamp(settings.RaceRetryDelayMs, 100, 60_000), budgetToken).ConfigureAwait(false);

            // 后台扫描可能刚好补齐了新 IP，重赛前再取一次缓存
            var refreshed = _resolver.GetCached(host);
            if (refreshed is { Count: > 0 })
            {
                foreach (var address in refreshed)
                {
                    if (!remaining.Contains(address))
                    {
                        remaining.Add(address);
                    }
                }
            }
        }

        if (clientToken.IsCancellationRequested)
        {
            return (null, false);
        }

        return (null, true);
    }

    private async Task<TcpClient> ConnectDirectAsync(string host, int port, CancellationToken token)
    {
        var client = new TcpClient();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(DirectConnectTimeoutMs);

        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"连接 {host}:{port} 超时");
        }
        catch
        {
            client.Dispose();
            throw;
        }

        client.NoDelay = true;
        _statistics.AddDirect();
        return client;
    }

    // ── 转发 ────────────────────────────────────────────────────────────

    private static async Task RelayAsync(Stream clientIn, Stream clientOut, Stream upstream, CancellationToken token)
    {
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(token);

        static async Task Guard(Func<Task> body, CancellationTokenSource scope)
        {
            try
            {
                await body().ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    scope.Cancel();
                }
                catch
                {
                    // ignore
                }
            }
        }

        await Task.WhenAll(
                Guard(() => PumpAsync(clientIn, upstream, TunnelIdleMs, scope.Token), scope),
                Guard(() => PumpAsync(upstream, clientOut, TunnelIdleMs, scope.Token), scope))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 单向泵：任意方向先 EOF 时只结束本方向（半关闭），另一方向继续，直到两边都没数据或空闲超时。
    /// </summary>
    private static async Task PumpAsync(Stream source, Stream target, int idleMs, CancellationToken token)
    {
        var buffer = new byte[RelayBufferSize];

        while (true)
        {
            int read;

            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                idle.CancelAfter(idleMs);
                read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), idle.Token).ConfigureAwait(false);
            }

            if (read <= 0)
            {
                return;
            }

            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            await target.FlushAsync(token).ConfigureAwait(false);
        }
    }

    internal static async Task CopyExactlyAsync(Stream source, Stream target, long count, CancellationToken token)
    {
        var buffer = new byte[RelayBufferSize];
        var remaining = count;

        while (remaining > 0)
        {
            var want = (int)Math.Min(buffer.Length, remaining);
            var read = await source.ReadAsync(buffer.AsMemory(0, want), token).ConfigureAwait(false);

            if (read <= 0)
            {
                throw new ProxyProtocolException("请求体长度不足");
            }

            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            remaining -= read;
        }
    }

    /// <summary>
    /// 逐块转发 chunked 请求体。校验点（缺一不可）：
    /// 长度行必须是合法十六进制且不超上限、每块数据后必须紧跟 CRLF、末块后必须有结束空行、尾部头长度受限。
    /// 任何一处不满足都立刻 400，绝不把半个帧写进上游。
    /// </summary>
    internal static async Task CopyChunkedAsync(Stream source, Stream target, CancellationToken token)
    {
        while (true)
        {
            var line = await ReadCrlfLineAsync(source, token).ConfigureAwait(false)
                       ?? throw new ProxyProtocolException("分块请求体不完整");

            var size = ParseChunkSize(line);
            await WriteLineAsync(target, line, token).ConfigureAwait(false);

            if (size == 0)
            {
                await CopyTrailersAsync(source, target, token).ConfigureAwait(false);
                return;
            }

            await CopyExactlyAsync(source, target, size, token).ConfigureAwait(false);
            await CopyCrlfAsync(source, target, token).ConfigureAwait(false);
        }
    }

    private static async Task CopyTrailersAsync(Stream source, Stream target, CancellationToken token)
    {
        var budget = MaxTrailerBytes;

        while (true)
        {
            var trailer = await ReadCrlfLineAsync(source, token).ConfigureAwait(false)
                          ?? throw new ProxyProtocolException("分块尾部不完整");

            budget -= trailer.Length + 2;
            if (budget < 0)
            {
                throw new ProxyProtocolException("分块尾部过大");
            }

            await WriteLineAsync(target, trailer, token).ConfigureAwait(false);

            if (trailer.Length == 0)
            {
                return;
            }
        }
    }

    private static int ParseChunkSize(string line)
    {
        var text = line;
        var semicolon = text.IndexOf(';');
        if (semicolon >= 0)
        {
            text = text[..semicolon];
        }

        text = text.Trim();

        if (text.Length is 0 or > 8)
        {
            throw new ProxyProtocolException("分块长度非法");
        }

        foreach (var c in text)
        {
            var hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex)
            {
                throw new ProxyProtocolException("分块长度非法");
            }
        }

        if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
        {
            throw new ProxyProtocolException("分块长度非法");
        }

        if (size > MaxChunkBytes)
        {
            throw new ProxyProtocolException("分块过大");
        }

        return size;
    }

    /// <summary>读出并校验「数据块之后必须紧跟的 CRLF」，校验通过后原样转发。</summary>
    private static async Task CopyCrlfAsync(Stream source, Stream target, CancellationToken token)
    {
        var pair = new byte[2];
        await ReadExactAsync(source, pair, token).ConfigureAwait(false);

        if (pair[0] != (byte)'\r' || pair[1] != (byte)'\n')
        {
            throw new ProxyProtocolException("分块数据后缺少 CRLF");
        }

        await target.WriteAsync(pair.AsMemory(0, 2), token).ConfigureAwait(false);
    }

    private static async Task ReadExactAsync(Stream source, byte[] buffer, CancellationToken token)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new ProxyProtocolException("请求体意外结束");
            }

            offset += read;
        }
    }

    /// <summary>只接受 CRLF 结尾的行。裸 LF 会让上游把两行当成一行，属于必须拒绝的帧错误。</summary>
    private static async Task<string?> ReadCrlfLineAsync(Stream stream, CancellationToken token)
    {
        var builder = new StringBuilder(16);
        var one = new byte[1];

        while (true)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), token).ConfigureAwait(false);
            if (read <= 0)
            {
                return null;
            }

            var value = one[0];

            if (value == (byte)'\n')
            {
                var text = builder.ToString();
                if (text.Length == 0 || text[^1] != '\r')
                {
                    throw new ProxyProtocolException("分块行必须以 CRLF 结束");
                }

                return text[..^1];
            }

            builder.Append((char)value);

            if (builder.Length > MaxChunkLineLength)
            {
                throw new ProxyProtocolException("分块行过长");
            }
        }
    }

    private static ValueTask WriteLineAsync(Stream stream, string line, CancellationToken token) =>
        ProxyProtocol.WriteAsciiAsync(stream, line + "\r\n", token);

    private static async Task WriteErrorAsync(
        Stream stream,
        int statusCode,
        string reason,
        string message,
        CancellationToken token)
    {
        try
        {
            var payload = ProxyProtocol.BuildErrorResponse(statusCode, reason, message);
            await stream.WriteAsync(payload.AsMemory(), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        catch
        {
            // 客户端已断开，无需处理
        }
    }

    private static (string Host, int Port) SplitHostPort(string target, int defaultPort)
    {
        var value = target.Trim();

        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close > 0)
            {
                var host = value[1..close];
                var rest = value[(close + 1)..];

                if (rest.StartsWith(':') && int.TryParse(rest[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ipv6Port))
                {
                    return (host, ipv6Port);
                }

                return (host, defaultPort);
            }
        }

        var colon = value.LastIndexOf(':');
        if (colon > 0 && int.TryParse(value[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
        {
            return (value[..colon], port);
        }

        return (value, defaultPort);
    }
}
