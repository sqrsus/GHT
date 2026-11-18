namespace GHT.Services.Http;

/// <summary>HTTP/1.1 请求头（或响应头）的结构化表示。只保留转发所需的信息。</summary>
internal sealed class HttpHead
{
    public string Method { get; init; } = string.Empty;

    public string Target { get; init; } = string.Empty;

    public string Version { get; init; } = "HTTP/1.1";

    public List<KeyValuePair<string, string>> Headers { get; } = new();

    public string? GetHeader(string name)
    {
        foreach (var header in Headers)
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

    public long? ContentLength
    {
        get
        {
            var value = GetHeader("Content-Length");
            if (value is null)
            {
                return null;
            }

            return long.TryParse(value.Trim(), out var parsed) && parsed >= 0 ? parsed : null;
        }
    }

    public static HttpHead? Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var lines = text.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0)
        {
            return null;
        }

        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length < 2)
        {
            return null;
        }

        var head = new HttpHead
        {
            Method = requestLine[0],
            Target = requestLine[1],
            Version = requestLine.Length > 2 ? requestLine[2] : "HTTP/1.1"
        };

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (name.Length > 0)
            {
                head.Headers.Add(new KeyValuePair<string, string>(name, value));
            }
        }

        return head;
    }
}
