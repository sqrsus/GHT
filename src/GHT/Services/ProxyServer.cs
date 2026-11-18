using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GHT.Models;
using GHT.Services.Github;
using GHT.Services.Http;

namespace GHT.Services;

/// <summary>
/// 加速池选路的结果。<b>必须</b>和「回落原因」分开表达：
/// 前者描述池子这一侧发生了什么，后者描述上层因此做了什么（回落到系统 DNS，或直接 502）。
/// 混成一个布尔值的话，「池子没答案」与「预算到期」在统计上就再也分不开了。
/// </summary>
public enum PoolOutcome
{
    /// <summary>竞速胜出，已拿到连接。</summary>
    Won,

    /// <summary>首连预算用尽，候选还没凑齐或还没连上。</summary>
    BudgetExpired,

    /// <summary>首批窗口内加速池一个 IP 都没解析出来。</summary>
    NoCandidates,

    /// <summary>解析出了候选，但全部建连失败。</summary>
    CandidatesFailed
}

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
///
/// 转发层的三条性能纪律（都是这里最容易退化的地方）：
/// 1) 转发缓冲用 <see cref="ArrayPool{T}"/>，不每条连接新分配 32KB；
/// 2) 空闲超时用「可重置的 CancelAfter」，而不是每次 Read 都 CreateLinkedTokenSource（旧实现每次读都新建一个 CTS）；
/// 3) 分块体的尺寸行/尾部头用 <see cref="LineBufferedStream"/> 批量读，不再逐字节进内核。
/// </summary>
public sealed class ProxyServer : IAsyncDisposable
{
    private const int MaxHeadBytes = 32 * 1024;
    private const int RelayBufferSize = 32 * 1024;
    private const int TunnelIdleMs = 60_000;
    private const int ResponseIdleMs = 120_000;
    private const int DirectConnectTimeoutMs = 8_000;

    /// <summary>套接字缓冲：默认 64KB 在长肥管道上会限制单流吞吐。</summary>
    private const int SocketBufferBytes = 256 * 1024;

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
    private readonly UpstreamValidator _validator;
    private readonly GithubMetaProvider _githubMeta;
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

        // TLS 判坏必须进竞速的健康度，否则「建连快但用不了」的地址会被反复选中。
        _validator = new UpstreamValidator(log, _racer.ReportReject);
        _githubMeta = new GithubMetaProvider(log);
        _whitelist = DomainWildcardList.Parse(settings.GlobalDnsWhitelist);

        // 先读磁盘缓存（几十 KB，同步读），再按需在后台刷新：
        // 首条连接不应该因为「还没拿到官方地址」而少一路候选。
        _githubMeta.BeginLoad();
    }

    public ProxyStatistics Statistics => _statistics;

    public ResolverHub Resolver => _resolver;

    /// <summary>官方地址快照，UI 用来展示加载状态。</summary>
    public GithubMetaProvider GithubMeta => _githubMeta;

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

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _githubMeta.Dispose();
    }

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
                TuneSocket(client.Client);

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
                    await HandleConnectAsync(client, stream, body, head, token).ConfigureAwait(false);
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

    private async Task HandleConnectAsync(
        TcpClient client,
        Stream clientStream,
        Stream clientInput,
        HttpHead head,
        CancellationToken token)
    {
        var (host, port) = SplitHostPort(head.Target, 443);
        if (string.IsNullOrWhiteSpace(host))
        {
            await WriteErrorAsync(clientStream, 400, "Bad Request", "CONNECT 目标非法", token).ConfigureAwait(false);
            return;
        }

        var connectWatch = Stopwatch.StartNew();
        TcpClient upstream;

        try
        {
            upstream = await ConnectForHostAsync(host, port, token).ConfigureAwait(false);
        }
        catch (UpstreamUnavailableException ex)
        {
            _statistics.AddError();
            _statistics.NoteFailure(host);
            await WriteErrorAsync(clientStream, 502, "Bad Gateway", ex.Message, token).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException)
        {
            _statistics.AddError();
            _statistics.NoteFailure(host);
            await WriteErrorAsync(clientStream, 502, "Bad Gateway", $"{host}:{port} 建连失败：{ex.Message}", token)
                .ConfigureAwait(false);
            return;
        }

        _statistics.AddConnectSample((int)connectWatch.ElapsedMilliseconds);

        using (upstream)
        {
            TuneSocket(upstream.Client);

            await clientStream.WriteAsync(ProxyProtocol.ConnectEstablished, token).ConfigureAwait(false);
            await clientStream.FlushAsync(token).ConfigureAwait(false);

            var upstreamStream = upstream.GetStream();

            _statistics.TunnelOpened();

            try
            {
                await RelayAsync(client, clientStream, upstream, upstreamStream, token).ConfigureAwait(false);
            }
            finally
            {
                _statistics.TunnelClosed();
                _statistics.AddTunnel();
            }
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

        var connectWatch = Stopwatch.StartNew();
        TcpClient upstream;

        try
        {
            upstream = await ConnectForHostAsync(uri.Host, uri.Port, token).ConfigureAwait(false);
        }
        catch (UpstreamUnavailableException ex)
        {
            _statistics.AddError();
            _statistics.NoteFailure(uri.Host);
            await WriteErrorAsync(clientStream, 502, "Bad Gateway", ex.Message, token).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException)
        {
            _statistics.AddError();
            _statistics.NoteFailure(uri.Host);
            await WriteErrorAsync(clientStream, 502, "Bad Gateway", $"{uri.Host}:{uri.Port} 建连失败：{ex.Message}", token)
                .ConfigureAwait(false);
            return;
        }

        _statistics.AddConnectSample((int)connectWatch.ElapsedMilliseconds);

        using (upstream)
        {
            TuneSocket(upstream.Client);

            var upstreamStream = upstream.GetStream();

            var request = new StringBuilder(512);
            request.Append(head.Method).Append(' ');

            var path = uri.PathAndQuery;
            request.Append(string.IsNullOrEmpty(path) ? "/" : path);
            request.Append(" HTTP/1.1\r\n");

            var hasHost = false;

            // 客户端带 Expect: 100-continue 时会停在这里等我们点头再发请求体。
            // 代理不跟上游协商，直接自己应答并丢掉这个头，否则双方互等（curl 上传大文件必踩）。
            var expectContinue = head.HasToken("Expect", "100-continue");

            foreach (var header in head.Headers)
            {
                if (ProxyProtocol.IsHopByHopHeader(header.Key))
                {
                    continue;
                }

                if (expectContinue && header.Key.Equals("Expect", StringComparison.OrdinalIgnoreCase))
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

            if (expectContinue)
            {
                await clientStream.WriteAsync(ProxyProtocol.ContinueResponse, token).ConfigureAwait(false);
                await clientStream.FlushAsync(token).ConfigureAwait(false);
            }

            long uploaded = 0;

            if (head.IsChunked)
            {
                uploaded = await CopyChunkedAsync(clientInput, upstreamStream, token).ConfigureAwait(false);
            }
            else if (head.ContentLength is { } contentLength && contentLength > 0)
            {
                uploaded = await CopyExactlyAsync(clientInput, upstreamStream, contentLength, token).ConfigureAwait(false);
            }

            _statistics.AddBytesUploaded(uploaded);

            await upstreamStream.FlushAsync(token).ConfigureAwait(false);

            // 响应按原始 framing 直接回灌（chunked 保持 chunked），并带空闲超时，
            // 避免上游无视 Connection: close 时把连接吊死。
            await PumpAsync(upstreamStream, clientStream, ResponseIdleMs, upload: false, token).ConfigureAwait(false);
            await clientStream.FlushAsync(token).ConfigureAwait(false);
        }
    }

    // ── 选路 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 选路：命中白名单 → 加速池解析 + 多 IP 竞速；否则系统 DNS 直连。
    ///
    /// 落不到加速池时会明确区分<b>为什么</b>回落（池为空 / 预算到期 / 无解析结果 / 候选全失败），
    /// 因为这四种情况的修法完全不同，而它们在 UI 上都表现为「回落」。
    /// </summary>
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
            return await ConnectDirectAsync(host, port, token, FallbackReason.None).ConfigureAwait(false);
        }

        // 池为空是「注定要用系统 DNS」的情形：直接连，不必先空转一轮解析再等预算。
        if (_resolver.PoolCount == 0)
        {
            _log.Debug($"加速池为空，{host}:{port} 直接走系统 DNS");
            return await ConnectDirectAsync(host, port, token, FallbackReason.PoolEmpty).ConfigureAwait(false);
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (settings.ConnectBudgetMs > 0)
        {
            budget.CancelAfter(settings.ConnectBudgetMs);
        }

        var (client, outcome) = await ConnectViaPoolAsync(host, port, budget.Token, token).ConfigureAwait(false);

        if (client is not null)
        {
            return client;
        }

        switch (outcome)
        {
            case PoolOutcome.CandidatesFailed:
                _statistics.AddPoolLoss();

                if (!settings.FallbackOnCandidatesFailed)
                {
                    // 严格语义：候选试完就认失败，不再用系统 DNS 兜底
                    throw new UpstreamUnavailableException($"{host}:{port} 的全部候选 IP 均连不上");
                }

                _log.Debug($"{host}:{port} 加速池候选全部连不上，改用系统 DNS 兜底");
                return await ConnectDirectAsync(host, port, token, FallbackReason.CandidatesFailed)
                    .ConfigureAwait(false);

            case PoolOutcome.NoCandidates:
                _log.Debug($"{host}:{port} 加速池暂无解析结果，回落系统 DNS（后台扫描继续）");
                return await ConnectDirectAsync(host, port, token, FallbackReason.NoDnsAnswer).ConfigureAwait(false);

            default:
                _log.Debug($"{host}:{port} 首连预算用尽，回落系统 DNS（后台扫描继续）");
                return await ConnectDirectAsync(host, port, token, FallbackReason.BudgetExpired).ConfigureAwait(false);
        }
    }

    private async Task<(TcpClient? Client, PoolOutcome Outcome)> ConnectViaPoolAsync(
        string host,
        int port,
        CancellationToken budgetToken,
        CancellationToken clientToken)
    {
        IReadOnlyList<IPAddress> resolved;

        try
        {
            resolved = await _resolver.ResolveAsync(host, budgetToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!clientToken.IsCancellationRequested)
        {
            return (null, PoolOutcome.BudgetExpired);
        }

        var settings = _settings;

        // 官方地址只是候选的补充来源：DNS 池给出的地址反映当前权威答案，优先；
        // 静态地址用来覆盖「DNS 被污染」和「GeoDNS 分到远端区域」两种 DNS 治不了的病。
        //
        // 注意这里不是「随便取 N 个」：官方池有一百多个地址，必须按地址健康度挑 ——
        // 试出好用的那天起它就稳定留在窗口里，轮换只作用于还没试过的地址。
        var seedLimit = Math.Clamp(settings.GithubMetaSeedCount, 0, 32);

        IReadOnlyList<IPAddress> seeds = Array.Empty<IPAddress>();

        if (settings.GithubMetaEnabled && seedLimit > 0)
        {
            var pool = _githubMeta.GetPool(host);
            if (pool.Count > 0)
            {
                seeds = _racer.RankSeeds(pool, seedLimit);
            }
        }

        var merged = CandidateMerger.Merge(resolved, seeds);

        if (merged.Count == 0)
        {
            // 预算已经在等首批结果时被耗尽 → 算预算问题；否则说明池子对这个域名没有答案
            return (null, budgetToken.IsCancellationRequested ? PoolOutcome.BudgetExpired : PoolOutcome.NoCandidates);
        }

        var ordered = ApplyTlsGate(host, port, merged, settings);

        var remaining = new List<IPAddress>(ordered);
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

                _statistics.AddRace();

                var win = await _racer
                    .RaceAsync(batch, port, settings.RaceConnectTimeoutMs, settings.RaceBatchBudgetMs, budgetToken)
                    .ConfigureAwait(false);

                if (win is { } winner)
                {
                    _statistics.AddPoolWin();
                    _statistics.SetWinner(host, winner.Ip.ToString());

                    if (seeds.Count > 0 && ContainsAddress(seeds, winner.Ip))
                    {
                        _log.Debug($"{host}:{port} 由 GitHub 官方地址胜出：{winner.Ip}");
                    }

                    return (winner.Client, PoolOutcome.Won);
                }

                if (budgetToken.IsCancellationRequested)
                {
                    return (null, PoolOutcome.BudgetExpired);
                }
            }

            if (round >= rounds)
            {
                break;
            }

            try
            {
                await Task.Delay(Math.Clamp(settings.RaceRetryDelayMs, 100, 60_000), budgetToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (null, PoolOutcome.BudgetExpired);
            }

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
            return (null, PoolOutcome.BudgetExpired);
        }

        return (null, PoolOutcome.CandidatesFailed);
    }

    /// <summary>
    /// TLS 校验闸门：已确认可用的排前面，已知不可用的直接剔除，未确认的照常参与竞速并顺手补探测。
    ///
    /// 三条边界条件值得写下来，否则很容易把功能改成「变慢」或「变不可用」：
    /// 1) 冷启动（一条可信记录都没有）时顺序完全不变，首个请求的耗时不受影响；
    /// 2) 全部候选都被判为不可用时，仍然按原样再试一轮 —— 与其直接 502，不如让用户拿到连接；
    /// 3) 只有 443 走这道闸门：明文 HTTP 没有 TLS 可言，探测它没有意义。
    /// </summary>
    private List<IPAddress> ApplyTlsGate(
        string host,
        int port,
        IReadOnlyList<IPAddress> candidates,
        AppSettings settings)
    {
        if (!settings.ValidateCandidates || port != 443 || candidates.Count == 0)
        {
            return new List<IPAddress>(candidates);
        }

        var verdict = _validator.Partition(host, port, candidates);

        var ordered = new List<IPAddress>(candidates.Count);
        ordered.AddRange(verdict.Trusted);
        ordered.AddRange(verdict.Unverified);

        if (ordered.Count == 0)
        {
            _log.Debug($"{host}:{port} 全部候选都被 TLS 校验判为不可用，仍按原样竞速一次");
            ordered.AddRange(candidates);
        }

        if (verdict.Unverified.Count > 0)
        {
            // 用服务生命周期而不是首连预算做取消源：探测结论要留给后续请求用，
            // 不能因为这一次的预算到期就把已经发出的握手废掉。
            _validator.Warm(host, port, verdict.Unverified, _cts?.Token ?? CancellationToken.None);
        }

        return ordered;
    }

    private static bool ContainsAddress(IReadOnlyList<IPAddress> list, IPAddress address)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<TcpClient> ConnectDirectAsync(
        string host,
        int port,
        CancellationToken token,
        FallbackReason reason)
    {
        if (reason != FallbackReason.None)
        {
            _statistics.AddFallback(reason);
        }

        var watch = Stopwatch.StartNew();
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
            NoteFallbackResult(reason, false, watch);
            throw new TimeoutException($"连接 {host}:{port} 超时");
        }
        catch
        {
            client.Dispose();
            NoteFallbackResult(reason, false, watch);
            throw;
        }

        client.NoDelay = true;
        _statistics.AddDirect();
        NoteFallbackResult(reason, true, watch);
        return client;
    }

    private void NoteFallbackResult(FallbackReason reason, bool ok, Stopwatch watch)
    {
        if (reason == FallbackReason.None)
        {
            return;
        }

        _statistics.AddFallbackResult(ok, (int)watch.ElapsedMilliseconds);
    }

    // ── 转发 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 双向转发。任一侧先 EOF 时只做<b>半关闭</b>（Shutdown(Send)），保留反方向继续传输 ——
    /// 否则「客户端上传完就关写端」会把还在返回数据的上游连接一起掐死（大文件下载尾部丢失）。
    /// </summary>
    private async Task RelayAsync(
        TcpClient client,
        Stream clientStream,
        TcpClient upstream,
        Stream upstreamStream,
        CancellationToken token)
    {
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(token);

        async Task HalfAsync(Stream from, Stream to, TcpClient toSocket, bool upload)
        {
            bool eof;

            try
            {
                eof = await PumpAsync(from, to, TunnelIdleMs, upload, scope.Token).ConfigureAwait(false);
            }
            catch
            {
                // 任意方向出错就整体结束，避免一侧已断另一侧还在空转
                try
                {
                    scope.Cancel();
                }
                catch
                {
                    // ignore
                }

                return;
            }

            // 只有「对端真的关了写端」才做半关闭。
            // 空闲超时不能发 FIN：客户端长时间只收不发（大文件下载）时，
            // 提前把上游写端关掉会让服务端直接结束响应。
            if (!eof)
            {
                return;
            }

            try
            {
                toSocket.Client.Shutdown(SocketShutdown.Send);
            }
            catch
            {
                // 对端可能已经彻底关闭，属正常
            }
        }

        await Task.WhenAll(
                HalfAsync(clientStream, upstreamStream, upstream, upload: true),
                HalfAsync(upstreamStream, clientStream, client, upload: false))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 单向泵。返回 true 表示读到 EOF（对端正常关了写端），false 表示空闲超时。
    /// 这个区分很关键：只有真 EOF 才应该对上游做半关闭。
    ///
    /// 空闲超时用「可重置的 CancelAfter」：每读到数据就重置计时，
    /// 不再像旧实现那样每次 Read 都创建一个 linked CTS（大文件传输时这是每秒上万次分配）。
    /// </summary>
    private async Task<bool> PumpAsync(Stream source, Stream target, int idleMs, bool upload, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(RelayBufferSize);

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(idleMs);

        try
        {
            while (true)
            {
                int read;

                try
                {
                    read = await source.ReadAsync(buffer.AsMemory(0, RelayBufferSize), idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // 空闲超时：本方向结束，但不代表对端不再需要另一方向
                    return false;
                }

                if (read <= 0)
                {
                    return true;
                }

                // 重置空闲计时
                idle.CancelAfter(idleMs);

                await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);

                if (upload)
                {
                    _statistics.AddBytesUploaded(read);
                }
                else
                {
                    _statistics.AddBytesDownloaded(read);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static async Task<long> CopyExactlyAsync(Stream source, Stream target, long count, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(RelayBufferSize);
        var remaining = count;
        long total = 0;

        try
        {
            while (remaining > 0)
            {
                var want = (int)Math.Min(RelayBufferSize, remaining);
                var read = await source.ReadAsync(buffer.AsMemory(0, want), token).ConfigureAwait(false);

                if (read <= 0)
                {
                    throw new ProxyProtocolException("请求体长度不足");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                remaining -= read;
                total += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return total;
    }

    /// <summary>
    /// 逐块转发 chunked 请求体。校验点（缺一不可）：
    /// 长度行必须是合法十六进制且不超上限、每块数据后必须紧跟 CRLF、末块后必须有结束空行、尾部头长度受限。
    /// 任何一处不满足都立刻 400，绝不把半个帧写进上游。
    ///
    /// 读取统一走 <see cref="LineBufferedStream"/>：预读到的正文不会丢，尺寸行也不必逐字节系统调用。
    /// </summary>
    internal static async Task<long> CopyChunkedAsync(Stream source, Stream target, CancellationToken token)
    {
        using var buffered = new LineBufferedStream(source, leaveOpen: true);
        long total = 0;

        while (true)
        {
            var line = await ReadCrlfLineAsync(buffered, token).ConfigureAwait(false)
                       ?? throw new ProxyProtocolException("分块请求体不完整");

            var size = ParseChunkSize(line);
            await WriteLineAsync(target, line, token).ConfigureAwait(false);

            if (size == 0)
            {
                await CopyTrailersAsync(buffered, target, token).ConfigureAwait(false);
                return total;
            }

            total += await CopyExactlyAsync(buffered, target, size, token).ConfigureAwait(false);
            await CopyCrlfAsync(buffered, target, token).ConfigureAwait(false);
        }
    }

    private static async Task CopyTrailersAsync(LineBufferedStream source, Stream target, CancellationToken token)
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
    private static async Task<string?> ReadCrlfLineAsync(LineBufferedStream source, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(256);

        try
        {
            var length = 0;

            while (true)
            {
                var value = await source.ReadByteAsync(token).ConfigureAwait(false);

                if (value < 0)
                {
                    return null;
                }

                if (value == '\n')
                {
                    if (length == 0 || buffer[length - 1] != (byte)'\r')
                    {
                        throw new ProxyProtocolException("分块行必须以 CRLF 结束");
                    }

                    return Encoding.ASCII.GetString(buffer, 0, length - 1);
                }

                if (length >= buffer.Length)
                {
                    if (buffer.Length >= MaxChunkLineLength)
                    {
                        throw new ProxyProtocolException("分块行过长");
                    }

                    // 扩容（极少发生：正常尺寸行只有几个字符）
                    var bigger = ArrayPool<byte>.Shared.Rent(Math.Min(MaxChunkLineLength, buffer.Length * 4));
                    Array.Copy(buffer, bigger, length);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                }

                buffer[length++] = (byte)value;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ValueTask WriteLineAsync(Stream stream, string line, CancellationToken token) =>
        ProxyProtocol.WriteAsciiAsync(stream, line + "\r\n", token);

    private static void TuneSocket(Socket socket)
    {
        try
        {
            socket.SendBufferSize = SocketBufferBytes;
            socket.ReceiveBufferSize = SocketBufferBytes;
        }
        catch
        {
            // 部分系统策略下不允许调整，忽略
        }
    }

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
