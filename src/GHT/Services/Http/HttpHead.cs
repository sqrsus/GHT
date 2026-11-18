using System.Text;

namespace GHT.Services.Http;

/// <summary>
/// HTTP/1.x 请求头。解析是<b>严格</b>的：任何畸形（非法字符、折行头、重复且冲突的 Content-Length、
/// 非法分块编码）都会抛 <see cref="ProxyProtocolException"/>，由上层回 400 而不是静默忽略。
///
/// 静默忽略头是有害的：一条被丢掉的 Host / Transfer-Encoding 会让后续转发走到与浏览器预期不同的地址或帧格式上。
/// </summary>
internal sealed class HttpHead
{
    /// <summary>单个头的名字与值长度上限（Apache/nginx 同量级）。</summary>
    private const int MaxNameLength = 64;

    private const int MaxValueLength = 8 * 1024;
    private const int MaxHeaderCount = 200;

    private readonly List<KeyValuePair<string, string>> _headers = new();

    private HttpHead()
    {
    }

    public string Method { get; private set; } = string.Empty;

    public string Target { get; private set; } = string.Empty;

    public string Version { get; private set; } = "HTTP/1.1";

    public IReadOnlyList<KeyValuePair<string, string>> Headers => _headers;

    /// <summary>已校验的 Content-Length（重复出现且值一致才接受，冲突则解析即失败）。</summary>
    public long? ContentLength { get; private set; }

    /// <summary>请求体是否为 chunked 分块传输（已确认 chunked 是最后一个编码）。</summary>
    public bool IsChunked { get; private set; }

    public string? GetHeader(string name)
    {
        foreach (var header in _headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        return null;
    }

    /// <summary>判断某个头是否包含指定 token（如 Transfer-Encoding: chunked）。</summary>
    public bool HasToken(string name, string token)
    {
        var value = GetHeader(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(part.Trim(), token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>严格解析请求头；畸形即抛异常。</summary>
    public static HttpHead Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new ProxyProtocolException("请求行为空");
        }

        var lines = text.Split("\r\n", StringSplitOptions.None);
        var head = new HttpHead();

        ParseRequestLine(head, lines[0]);

        var hostCount = 0;
        long? contentLength = null;
        var hasContentLength = false;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.Length == 0)
            {
                continue;
            }

            // 裸 LF / CR 说明头不是标准 CRLF 分帧，继续解析只会把两行粘成一行。
            if (line.IndexOf('\n') >= 0 || line.IndexOf('\r') >= 0 || line.IndexOf('\0') >= 0)
            {
                throw new ProxyProtocolException("请求头包含非法换行或空字符");
            }

            // obs-fold（以空格/制表符开头的续行）在 RFC 7230 已废弃，且极易被用于绕过 Host 校验。
            if (line[0] is ' ' or '\t')
            {
                throw new ProxyProtocolException("不支持折行请求头");
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new ProxyProtocolException("请求头缺少冒号分隔");
            }

            var name = line[..separator];
            if (name.Length > MaxNameLength || !IsToken(name))
            {
                throw new ProxyProtocolException("请求头名称非法");
            }

            var value = line[(separator + 1)..].Trim(' ', '\t');

            if (value.Length > MaxValueLength || !IsValidValue(value))
            {
                throw new ProxyProtocolException($"请求头 {name} 的值非法");
            }

            if (head._headers.Count >= MaxHeaderCount)
            {
                throw new ProxyProtocolException("请求头数量过多");
            }

            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                hostCount++;
                if (hostCount > 1)
                {
                    throw new ProxyProtocolException("Host 头重复");
                }
            }

            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseContentLength(value, out var parsed))
                {
                    throw new ProxyProtocolException("Content-Length 非法");
                }

                if (hasContentLength && contentLength != parsed)
                {
                    throw new ProxyProtocolException("Content-Length 重复且值冲突");
                }

                hasContentLength = true;
                contentLength = parsed;
                head._headers.Add(new KeyValuePair<string, string>(name, value));
                continue;
            }

            head._headers.Add(new KeyValuePair<string, string>(name, value));
        }

        head.ContentLength = contentLength;

        ValidateTransferEncoding(head);
        return head;
    }

    private static void ParseRequestLine(HttpHead head, string line)
    {
        if (line.Length == 0)
        {
            throw new ProxyProtocolException("请求行为空");
        }

        var parts = line.Split(' ');
        if (parts.Length != 3)
        {
            throw new ProxyProtocolException("请求行格式非法");
        }

        if (parts[0].Length is 0 or > 32 || !IsToken(parts[0]))
        {
            throw new ProxyProtocolException("请求方法非法");
        }

        if (parts[1].Length == 0 || parts[1].Length > 8 * 1024)
        {
            throw new ProxyProtocolException("请求目标非法");
        }

        if (!parts[2].Equals("HTTP/1.1", StringComparison.OrdinalIgnoreCase)
            && !parts[2].Equals("HTTP/1.0", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProxyProtocolException($"不支持的协议版本 {parts[2]}");
        }

        head.Method = parts[0];
        head.Target = parts[1];
        head.Version = parts[2].ToUpperInvariant();
    }

    /// <summary>
    /// 校验 Transfer-Encoding：只支持最后一跳为 chunked 的编码链。
    /// 其他编码（gzip/compress 等）无法在不理解语义的情况下保证帧边界，按 RFC 7230 §3.3.3 直接拒绝。
    /// </summary>
    private static void ValidateTransferEncoding(HttpHead head)
    {
        var raw = head.GetHeader("Transfer-Encoding");
        if (raw is null)
        {
            return;
        }

        var codings = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToArray();

        if (codings.Length == 0 || !string.Equals(codings[^1], "chunked", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProxyProtocolException("不支持的 Transfer-Encoding");
        }

        head.IsChunked = true;
    }

    private static bool TryParseContentLength(string value, out long length)
    {
        length = 0;

        if (value.Length is 0 or > 18)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return long.TryParse(value, out length) && length >= 0;
    }

    /// <summary>RFC 7230 token：header 名与 method 都只能用这组字符。</summary>
    private static bool IsToken(string text)
    {
        foreach (var c in text)
        {
            var ok = c is >= 'a' and <= 'z'
                     or >= 'A' and <= 'Z'
                     or >= '0' and <= '9'
                     or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>头值允许可见 ASCII、空格、HTAB 与 obs-text(0x80-0xFF)，不允许其他控制字符。</summary>
    private static bool IsValidValue(string value)
    {
        foreach (var c in value)
        {
            if (c == '\t')
            {
                continue;
            }

            if (c < 0x20 || c == 0x7F)
            {
                return false;
            }
        }

        return true;
    }
}
