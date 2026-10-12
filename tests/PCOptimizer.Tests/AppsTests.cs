using System.Text.RegularExpressions;
using PCOptimizer.Core.Apps;
using PCOptimizer.Core.Optimization;
using Xunit;

namespace PCOptimizer.Tests;

public class AppsTests
{
    [Fact]
    public void ParseExport_reads_package_ids_case_insensitively()
    {
        const string json = """
            {"$schema":"x","Sources":[{"Packages":[{"PackageIdentifier":"Valve.Steam"},{"PackageIdentifier":"Git.Git"}],
              "SourceDetails":{"Name":"winget"}}],"WinGetVersion":"1.9"}
            """;
        var ids = Winget.ParseExport(json);
        Assert.Equal(2, ids.Count);
        Assert.Contains("valve.steam", ids);
    }

    [Fact]
    public void ParseExport_handles_no_sources()
    {
        Assert.Empty(Winget.ParseExport("""{"WinGetVersion":"1.9"}"""));
    }

    // saída real do "winget upgrade --source winget" (com o progresso desenhado por \r antes da tabela)
    const string UpgradeOutput =
        "   - \r   \\ \r\r" +
        "Name                                                         Id                           Version       Available\n" +
        "---------------------------------------------------------------------------------------------------------------------\n" +
        "App Installer                                                Microsoft.AppInstaller       1.29.379.0    1.29.380\n" +
        "Microsoft Visual C++ v14 Redistributable (x64) - 14.50.35719 Microsoft.VCRedist.2015+.x64 14.50.35719.0 14.51.36247.0\n" +
        "Ollama version 0.35.1                                        Ollama.Ollama                0.35.1        0.40.2\n" +
        "3 upgrades available.\n" +
        "1 package(s) have version numbers that cannot be determined. Use --include-unknown to see all results.\n";

    [Fact]
    public void ParseUpgrades_reads_the_table_and_stops_at_the_footer()
    {
        var ups = Winget.ParseUpgrades(UpgradeOutput);
        Assert.Equal(3, ups.Count);
        Assert.Equal(new AppUpdate("Microsoft Visual C++ v14 Redistributable (x64) - 14.50.35719", "Microsoft.VCRedist.2015+.x64", "14.50.35719.0", "14.51.36247.0"), ups[1]);
        Assert.Equal("0.40.2", ups[2].Available);
    }

    [Fact]
    public void ParseUpgrades_works_with_translated_headers_and_a_source_column()
    {
        const string pt =
            "Nome            ID              Versão   Disponível Origem\r\n" +
            "-----------------------------------------------------------\r\n" +
            "Discord         Discord.Discord 1.0.90   1.0.91     winget\r\n" +
            "1 atualizações disponíveis.\r\n";
        var u = Assert.Single(Winget.ParseUpgrades(pt));
        Assert.Equal(("Discord", "Discord.Discord", "1.0.90", "1.0.91"), (u.Name, u.Id, u.Version, u.Available));
    }

    [Fact]
    public void ParseUpgrades_returns_nothing_without_a_table()
    {
        Assert.Empty(Winget.ParseUpgrades("No installed package found matching input criteria.\n"));
    }

    [Fact]
    public void Catalog_ids_are_unique_and_safe_for_the_command_line()
    {
        var ids = AppCatalog.All.Select(a => a.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ids, id => Assert.Matches(new Regex(@"^[\w.+-]+$"), id));
        Assert.True(AppCatalog.All.Select(a => a.Category).Distinct().Count() >= 5);
    }

    [Fact]
    public void Every_profile_tweak_exists()
    {
        Assert.Contains(TweakCatalog.Profiles, p => p.Id == "privacy");
        Assert.All(TweakCatalog.Profiles.SelectMany(p => p.TweakIds), id => Assert.Contains(TweakCatalog.All, t => t.Id == id));
    }

    [Theory]
    [InlineData("Opera GX Stable 136.0.6008.76", "Opera.OperaGX", true)]
    [InlineData("Opera GX Stable 136.0.6008.76", "Opera.Opera", false)]
    [InlineData("Opera Stable 120.0.5543.61", "Opera.Opera", true)]
    [InlineData("Brave", "Brave.Brave", true)]
    [InlineData("Mozilla Firefox (x64 en-US)", "Mozilla.Firefox", true)]
    [InlineData("Google Chrome", "Google.Chrome", true)]
    [InlineData("Steam", "Valve.Steam", true)]
    [InlineData("Steamworks Common Redistributables", "Valve.Steam", false)]
    [InlineData("Discord", "Discord.Discord", true)]
    public void MatchesProgram_recognises_installed_programs(string displayName, string id, bool expected)
    {
        Assert.Equal(expected, Winget.MatchesProgram(displayName, AppCatalog.All.First(a => a.Id == id)));
    }
}