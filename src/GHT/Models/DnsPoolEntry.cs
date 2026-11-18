namespace GHT.Models;

/// <summary>加速池中的一条公共 DNS 记录。</summary>
public sealed class DnsPoolEntry
{
    /// <summary>DNS 服务器 IP。</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>服务器反向域名（扫描时从内置清单带出，可为空）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>解析目标域名时的往返延迟（毫秒）。</summary>
    public int LatencyMs { get; set; }

    public override string ToString() => $"{Ip} ({LatencyMs}ms)";
}
