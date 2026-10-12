using PCOptimizer.Core.Network;
using Xunit;

namespace PCOptimizer.Tests;

public class HostsTests
{
    const string Original = "# Copyright Microsoft\r\n127.0.0.1 meu-servidor.local\r\n";

    [Fact]
    public void ParseList_keeps_only_blocking_entries()
    {
        var list = "# title\n127.0.0.1 localhost\n0.0.0.0 0.0.0.0\n0.0.0.0 ads.example.com\n0.0.0.0 Ads.Example.com # dup\n0.0.0.0 tracker.net\n";
        Assert.Equal(new[] { "0.0.0.0 ads.example.com", "0.0.0.0 tracker.net" }, HostsBlocker.ParseList(list));
    }

    [Fact]
    public void ParseList_only_skips_exact_local_names()
    {
        var list = "127.0.0.1 localhost.localdomain\n127.0.0.1 broadcasthost\n127.0.0.1 localhostads.net\n";
        Assert.Equal(new[] { "0.0.0.0 localhostads.net" }, HostsBlocker.ParseList(list));
    }

    [Fact]
    public void ParseList_accepts_127_format_and_skips_localhost()
    {
        // formato das listas AdAway / Peter Lowe
        var list = "127.0.0.1  localhost\n127.0.0.1 localhost.localdomain\n::1  localhost\n127.0.0.1 pixel.spotify.com\n";
        Assert.Equal(new[] { "0.0.0.0 pixel.spotify.com" }, HostsBlocker.ParseList(list));
    }

    [Fact]
    public void AddBlock_then_Strip_gives_back_the_original()
    {
        var withBlock = HostsBlocker.AddBlock(Original, new[] { "0.0.0.0 ads.example.com" });
        Assert.Contains("0.0.0.0 ads.example.com", withBlock);
        Assert.StartsWith(Original.TrimEnd(), withBlock);
        Assert.Equal(Original.TrimEnd(), HostsBlocker.Strip(withBlock).TrimEnd());
    }

    [Fact]
    public void AddBlock_twice_replaces_the_old_block()
    {
        var once = HostsBlocker.AddBlock(Original, new[] { "0.0.0.0 old.example.com" });
        var twice = HostsBlocker.AddBlock(once, new[] { "0.0.0.0 new.example.com" });
        Assert.DoesNotContain("old.example.com", twice);
        Assert.Contains("new.example.com", twice);
        Assert.Contains("meu-servidor.local", twice);
    }

    [Fact]
    public void Strip_without_block_changes_nothing()
    {
        Assert.Equal(Original, HostsBlocker.Strip(Original));
    }

    [Fact]
    public void Turning_off_restores_the_file_byte_for_byte()
    {
        var on = HostsBlocker.AddBlock(Original, new[] { "0.0.0.0 ads.example.com" });
        Assert.Equal(Original, HostsBlocker.Strip(on).TrimEnd('\r', '\n') + "\r\n"); // como o DisableAsync
    }
}
