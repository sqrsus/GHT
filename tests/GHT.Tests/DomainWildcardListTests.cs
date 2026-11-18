using GHT.Services;
using Xunit;

namespace GHT.Tests;

public sealed class DomainWildcardListTests
{
    [Fact]
    public void Empty_whitelist_matches_nothing()
    {
        var list = DomainWildcardList.Parse("   ");

        Assert.True(list.IsEmpty);
        Assert.False(list.IsMatch("github.com"));
    }

    [Fact]
    public void Exact_entry_matches_itself_only()
    {
        var list = DomainWildcardList.Parse("github.com");

        Assert.True(list.IsMatch("github.com"));
        Assert.False(list.IsMatch("api.github.com"));
        Assert.False(list.IsMatch("xgithub.com"));
    }

    [Fact]
    public void Wildcard_matches_subdomains_but_not_the_apex()
    {
        var list = DomainWildcardList.Parse("*.github.com");

        Assert.True(list.IsMatch("api.github.com"));
        Assert.True(list.IsMatch("a.b.github.com"));
        Assert.False(list.IsMatch("github.com"));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var list = DomainWildcardList.Parse("GitHub.com");
        Assert.True(list.IsMatch("GITHUB.COM"));
    }

    [Fact]
    public void Strips_port_and_trailing_dot()
    {
        var list = DomainWildcardList.Parse("github.com");

        Assert.True(list.IsMatch("github.com:443"));
        Assert.True(list.IsMatch("github.com."));
    }

    [Fact]
    public void Supports_comma_semicolon_and_newline_separators()
    {
        var list = DomainWildcardList.Parse("github.com, *.githubusercontent.com;\r\ngithub.io");

        Assert.Equal(3, list.Entries.Count);
        Assert.True(list.IsMatch("raw.githubusercontent.com"));
        Assert.True(list.IsMatch("github.io"));
    }

    [Fact]
    public void Duplicate_entries_are_collapsed()
    {
        var list = DomainWildcardList.Parse("github.com, github.com, GitHub.com");
        Assert.Single(list.Entries);
    }

    [Fact]
    public void Null_host_never_matches()
    {
        var list = DomainWildcardList.Parse("github.com");
        Assert.False(list.IsMatch(null));
    }
}
