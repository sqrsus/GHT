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
/// </summary>
public sealed class ProxyServer : IAsyncDisposable
{
    private const int MaxHeadBytes = 32 * 1024;
    private const int RelayBufferSize = 32 * 1024;
    private const int TunnelIdleMs = 60_000;
    private const int DirectConnectTimeoutMs = 8_000;

    private readonly LogService _log;
    private readonly ResolverHub _resolver;
    private readonly UpstreamRacer _racer;
    private readonly ProxyStatistics _statistics = new();

    private volatile AppSettings _settings;
    private volatile DomainWildcardList _whitelist;

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

    public bool IsRunning => _listener is not null;

    public int Port => _port;

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        _whitelist = DomainWildcardList.Parse(settings.GlobalDnsWhitelist);
        _resolver.ApplySettings(settings);
    }

    public void UpdatePool(IReadOnlyList<DnsPoolEntry> pool) => _resolver.UpdatePool(pool);

    public Task StartAsync(int port)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        _listener = listener;
        _port = port;
        _cts = new CancellationTokenSource();

        var token = _cts.Token;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, token), CancellationToken.None);

        _log.Info($"代理已启动：127.0.0.1:{port}");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
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
        _log.Info("代理已停止");
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

                var head = HttpHead.Parse(headRead.HeadText);
                if (head is null)
                {
                    await WriteErrorAsync(stream, 400, "Bad Request", "无法解析请求行", token).ConfigureAwait(false);
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

            if (head.HasToken("Transfer-Encoding", "chunked"))
            {
                await CopyChunkedAsync(clientInput, upstreamStream, token).ConfigureAwait(false);
            }
            else if (head.ContentLength is { } contentLength && contentLength > 0)
            {
                await CopyExactlyAsync(clientInput, upstreamStream, contentLength, token).ConfigureAwait(false);
            }

            await upstreamStream.FlushAsync(token).ConfigureAwait(false);
            await upstreamStream.CopyToAsync(clientStream, RelayBufferSize, token).ConfigureAwait(false);
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

    private static async Task CopyExactlyAsync(Stream source, Stream target, long count, CancellationToken token)
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

    private static async Task CopyChunkedAsync(Stream source, Stream target, CancellationToken token)
    {
        while (true)
        {
            var line = await ReadCrlfLineAsync(source, token).ConfigureAwait(false)
                       ?? throw new ProxyProtocolException("分块请求体不完整");

            await WriteLineAsync(target, line, token).ConfigureAwait(false);

            var sizeText = line;
            var semicolon = sizeText.IndexOf(';');
            if (semicolon >= 0)
            {
                sizeText = sizeText[..semicolon];
            }

            if (!int.TryParse(sizeText.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
            {
                throw new ProxyProtocolException("分块长度非法");
            }

            if (size == 0)
            {
                // 尾部 header 区，空行结束
                while (true)
                {
                    var trailer = await ReadCrlfLineAsync(source, token).ConfigureAwait(false);
                    if (trailer is null)
                    {
                        return;
                    }

                    await WriteLineAsync(target, trailer, token).ConfigureAwait(false);

                    if (trailer.Length == 0)
                    {
                        return;
                    }
                }
            }

            // 数据 + 尾随 CRLF
            await CopyExactlyAsync(source, target, size + 2, token).ConfigureAwait(false);
        }
    }

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
                return text.EndsWith("\r", StringComparison.Ordinal) ? text[..^1] : text;
            }

            builder.Append((char)value);

            if (builder.Length > 8192)
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
