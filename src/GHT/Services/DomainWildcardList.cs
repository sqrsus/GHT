using System.Text;
using System.Text.RegularExpressions;

namespace GHT.Services;

/// <summary>
/// 域名白名单匹配。
/// 输入按逗号 / 分号 / 换行切分，忽略空项；支持 <c>*</c> 通配（<c>*.github.com</c> 命中任意层级子域）。
/// 不做强制合并——用户清空即为空，表示「全部走系统 DNS」。
/// </summary>
public sealed class DomainWildcardList
{
    private readonly Regex? _pattern;

    private DomainWildcardList(string[] entries, Regex? pattern)
    {
        Entries = entries;
        _pattern = pattern;
    }

    public IReadOnlyList<string> Entries { get; }

    public bool IsEmpty => Entries.Count == 0;

    public static DomainWildcardList Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new DomainWildcardList(Array.Empty<string>(), null);
        }

        var entries = text
            .Split(new[] { ',', ';', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (entries.Length == 0)
        {
            return new DomainWildcardList(Array.Empty<string>(), null);
        }

        var sb = new StringBuilder("^(?:");
        for (var i = 0; i < entries.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('|');
            }

            var escaped = Regex.Escape(entries[i]).Replace("\\*", ".*", StringComparison.Ordinal);
            sb.Append(escaped);
        }

        sb.Append(")$");

        var regex = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return new DomainWildcardList(entries, regex);
    }

    public bool IsMatch(string? host)
    {
        if (_pattern is null || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalized = host.Trim().TrimEnd('.');

        var colon = normalized.IndexOf(':');
        if (colon > 0)
        {
            normalized = normalized[..colon];
        }

        return _pattern.IsMatch(normalized);
    }
}
