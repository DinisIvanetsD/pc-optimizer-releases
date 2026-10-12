using System.Text.RegularExpressions;
using PCOptimizer.Core.History;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Apps;

/// <summary>
/// Apps que vêm com o Windows e se podem remover sem estragar nada (como no Optimizer/O&amp;O AppBuster).
/// Fora da lista: Loja, Calculadora, Fotografias, Bloco de Notas, Terminal, Xbox Game Bar e tudo o que o sistema usa.
/// Remover é só para o utilizador atual; para voltar a ter uma, instala-se da Microsoft Store.
/// </summary>
public static class WindowsBloat
{
    const string Windows = "Windows";

    public static IReadOnlyList<AppEntry> All { get; } = new AppEntry[]
    {
        new("Microsoft.BingNews", Loc.T("Notícias"), Windows),
        new("Microsoft.BingWeather", Loc.T("Meteorologia"), Windows),
        new("Microsoft.BingSearch", "Bing Search", Windows),
        new("Microsoft.GetHelp", Loc.T("Obter Ajuda"), Windows),
        new("Microsoft.Getstarted", Loc.T("Dicas"), Windows),
        new("Microsoft.WindowsFeedbackHub", "Feedback Hub", Windows),
        new("Microsoft.MicrosoftSolitaireCollection", "Solitaire", Windows),
        new("Clipchamp.Clipchamp", "Clipchamp", Windows),
        new("Microsoft.ZuneVideo", Loc.T("Filmes e TV"), Windows),
        new("Microsoft.GamingApp", "Xbox", Windows),
        new("Microsoft.YourPhone", Loc.T("Ligação Telemóvel"), Windows),
        new("Microsoft.MicrosoftOfficeHub", "Microsoft 365 (Office)", Windows),
        new("Microsoft.OutlookForWindows", Loc.T("Outlook (novo)"), Windows),
        new("MSTeams", Loc.T("Teams (pessoal)"), Windows),
        new("Microsoft.Todos", "Microsoft To Do", Windows),
        new("Microsoft.PowerAutomateDesktop", "Power Automate", Windows),
        new("Microsoft.WindowsMaps", Loc.T("Mapas"), Windows),
        new("Microsoft.People", Loc.T("Pessoas"), Windows),
        new("Microsoft.549981C3F5F10", "Cortana", Windows),
        new("Microsoft.Copilot", "Copilot", Windows),
        new("MicrosoftCorporationII.MicrosoftFamily", Loc.T("Família Microsoft"), Windows),
        new("Microsoft.MixedReality.Portal", Loc.T("Realidade Mista"), Windows),
        new("Microsoft.SkypeApp", "Skype", Windows),
    };

    /// <summary>Nomes dos pacotes da lista que estão instalados para este utilizador.</summary>
    public static async Task<HashSet<string>> InstalledAsync()
    {
        var names = string.Join(",", All.Select(a => Shell.Quote(a.Id)));
        var r = await Shell.PowerShellAsync($"Get-AppxPackage -Name @({names}) -ErrorAction SilentlyContinue | ForEach-Object Name", 60_000);
        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static async Task RemoveAsync(AppEntry app)
    {
        Simulation.Guard(Loc.T($"remover {app.Name}"));
        // só nomes da lista acima; a verificação protege a linha de comandos
        if (!All.Any(a => a.Id == app.Id) || !Regex.IsMatch(app.Id, @"^[\w.]+$")) throw new InvalidOperationException(Loc.T("App inválida."));
        var r = await Shell.PowerShellAsync($"Get-AppxPackage -Name {Shell.Quote(app.Id)} | Remove-AppxPackage -ErrorAction Stop", 120_000);
        if (!r.Ok) throw new InvalidOperationException(Shell.CleanError(r));
        ChangeLog.Record(ChangeKind.Uninstall, Loc.T($"Removida: {app.Name}"), Loc.T("App do Windows. Para a voltar a ter, instala-a da Microsoft Store."));
    }
}
