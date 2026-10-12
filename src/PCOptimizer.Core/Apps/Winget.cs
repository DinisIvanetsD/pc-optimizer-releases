using System.Text.Json;
using System.Text.RegularExpressions;
using PCOptimizer.Core.History;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Apps;

/// <summary>Uma app instalada com versão nova no winget.</summary>
public sealed record AppUpdate(string Name, string Id, string Version, string Available);

/// <summary>
/// Instala, desinstala e atualiza apps pelo winget (o instalador oficial da Microsoft, que vem no Windows 10/11).
/// Cada app vem da fonte do fabricante; o winget verifica o hash do instalador.
/// </summary>
public static class Winget
{
    const string Agree = "--accept-source-agreements --disable-interactivity";
    const string Quiet = "--silent --accept-package-agreements " + Agree;

    public static async Task<bool> IsAvailableAsync()
    {
        try { return (await Shell.RunAsync("winget", "--version", 15_000)).Ok; }
        catch { return false; }
    }

    /// <summary>IDs do winget das apps instaladas (só as que o winget conhece).</summary>
    public static async Task<HashSet<string>> InstalledIdsAsync()
    {
        var file = Path.Combine(Path.GetTempPath(), $"pcopt-winget-{Guid.NewGuid():N}.json");
        try
        {
            await Shell.RunAsync("winget", $"export -o \"{file}\" --source winget {Agree}", 120_000);
            var ids = File.Exists(file) ? ParseExport(await File.ReadAllTextAsync(file)) : new(StringComparer.OrdinalIgnoreCase);
            // o winget não reconhece algumas apps (ex.: Brave, Opera GX instalados pelo site): vê também os programas do Windows
            var names = InstalledProgramNames();
            foreach (var app in AppCatalog.All)
                if (names.Any(n => MatchesProgram(n, app))) ids.Add(app.Id);
            return ids;
        }
        finally { try { File.Delete(file); } catch { } }
    }

    /// <summary>Nomes em "Apps instaladas" do Windows (registo Uninstall, do PC e do utilizador).</summary>
    static List<string> InstalledProgramNames()
    {
        var names = new List<string>();
        foreach (var (hive, path) in new[]
        {
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
            try
            {
                using var key = hive.OpenSubKey(path);
                foreach (var sub in key?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var k = key!.OpenSubKey(sub);
                    if (k?.GetValue("DisplayName") is string n && n.Length > 0) names.Add(n);
                }
            }
            catch { }
        return names;
    }

    /// <summary>
    /// "Opera GX Stable 136.0" é o Opera GX mas não o Opera; "Mozilla Firefox (x64 en-US)" é o Firefox.
    /// O nome tem de bater no início (sem contar a marca do ID) e só pode ser seguido de versão, "Stable" ou parênteses.
    /// </summary>
    public static bool MatchesProgram(string displayName, AppEntry app)
    {
        var vendor = app.Id.Split('.')[0];
        var dn = Regex.Replace(displayName.Trim(), $@"^{Regex.Escape(vendor)}\s+(?={Regex.Escape(app.Name.Split(' ')[0])})", "", RegexOptions.IgnoreCase);
        return Regex.IsMatch(dn, $@"^{Regex.Escape(app.Name)}(\s*$|\s+(stable\b|v?\d|\(|x64|x86))", RegexOptions.IgnoreCase);
    }

    /// <summary>Lê o JSON do "winget export" (Sources[].Packages[].PackageIdentifier).</summary>
    public static HashSet<string> ParseExport(string json)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("Sources", out var sources)) return ids;
        foreach (var src in sources.EnumerateArray())
            if (src.TryGetProperty("Packages", out var pkgs))
                foreach (var p in pkgs.EnumerateArray())
                    if (p.TryGetProperty("PackageIdentifier", out var id) && id.GetString() is { Length: > 0 } s) ids.Add(s);
        return ids;
    }

    public static async Task InstallAsync(AppEntry app)
    {
        Simulation.Guard(Loc.T($"instalar {app.Name}"));
        Check(app.Id);
        var r = await Shell.RunAsync("winget", $"install --id {app.Id} -e --source winget {Quiet}", 30 * 60_000);
        if (!r.Ok) throw new InvalidOperationException(Error(r));
        ChangeLog.Record(ChangeKind.Install, Loc.T($"Instalada: {app.Name}"), "winget · " + app.Id, UndoKind.AppInstall,
            new() { ["id"] = app.Id, ["name"] = app.Name });
    }

    public static async Task UninstallAsync(string id)
    {
        Check(id);
        var args = $"uninstall --id {id} -e --silent {Agree}";
        var r = await Shell.RunAsync("winget", args, 30 * 60_000);
        // apps instaladas para todo o PC só saem com administrador (1603 = o desinstalador precisa de permissão)
        if (!r.Ok && !Admin.IsElevated)
        {
            var e = await Shell.PowerShellAsync($"(Start-Process winget -ArgumentList {Shell.Quote(args)} -Verb RunAs -Wait -PassThru -WindowStyle Hidden).ExitCode", 30 * 60_000);
            if (e.Ok && e.StdOut.Trim() == "0") return;
        }
        if (!r.Ok) throw new InvalidOperationException(Error(r));
    }

    /// <summary>Apps com versão nova (do repositório do winget).</summary>
    public static async Task<List<AppUpdate>> UpgradesAsync()
    {
        var r = await Shell.RunAsync("winget", $"upgrade --source winget {Agree}", 180_000);
        return ParseUpgrades(r.StdOut);
    }

    /// <summary>
    /// Lê a tabela do "winget upgrade". Os títulos vêm no idioma do Windows, por isso usa só as posições
    /// das colunas (a linha antes dos "-----"): Nome, ID, Versão, Disponível, Origem.
    /// </summary>
    public static List<AppUpdate> ParseUpgrades(string output)
    {
        var list = new List<AppUpdate>();
        // o winget desenha o progresso com \r: fica só o que foi escrito por último em cada linha
        var lines = output.Replace("\r\n", "\n").Split('\n').Select(l => l[(l.LastIndexOf('\r') + 1)..]).ToList();
        var dash = lines.FindIndex(l => l.Trim().Length > 10 && l.Trim().All(c => c == '-'));
        if (dash < 1) return list;
        var starts = Regex.Matches(lines[dash - 1], @"(?<=^|\s)\S").Select(m => m.Index).ToList();
        if (starts.Count < 4) return list;
        string Col(string line, int k)
        {
            if (line.Length <= starts[k]) return "";
            var end = k + 1 < starts.Count ? Math.Min(starts[k + 1], line.Length) : line.Length;
            return line[starts[k]..end].Trim();
        }
        foreach (var line in lines.Skip(dash + 1))
        {
            var id = Col(line, 1);
            var available = Col(line, 3);
            // acaba no rodapé ("3 upgrades available.") ou na segunda tabela
            if (!Regex.IsMatch(id, @"^[\w.+-]+$") || available.Length == 0) break;
            list.Add(new AppUpdate(Col(line, 0), id, Col(line, 2), available));
        }
        return list;
    }

    public static async Task UpgradeAsync(AppUpdate app)
    {
        Simulation.Guard(Loc.T($"atualizar {app.Name}"));
        Check(app.Id);
        var r = await Shell.RunAsync("winget", $"upgrade --id {app.Id} -e --source winget {Quiet}", 30 * 60_000);
        if (!r.Ok) throw new InvalidOperationException(Error(r));
        ChangeLog.Record(ChangeKind.Install, Loc.T($"Atualizada: {app.Name} ({app.Version} → {app.Available})"), "winget · " + app.Id);
    }

    /// <summary>Atualiza todas as apps que o winget conhece. Devolve quantas foram atualizadas (aproximado).</summary>
    public static async Task<string> UpgradeAllAsync()
    {
        Simulation.Guard(Loc.T("atualizar todas as apps"));
        var r = await Shell.RunAsync("winget", $"upgrade --all {Quiet}", 60 * 60_000);
        var done = Regex.Matches(r.StdOut, @"Successfully installed|Instalado com êxito|Instalado com sucesso", RegexOptions.IgnoreCase).Count;
        if (Regex.IsMatch(r.StdOut, @"No (available )?upgrade|No installed package found matching|Não foram encontradas atualizações", RegexOptions.IgnoreCase) && done == 0)
            return Loc.T("✓ As tuas apps já estão na versão mais recente.");
        if (done > 0) ChangeLog.Record(ChangeKind.Install, Loc.T($"Apps atualizadas ({done})"), "winget upgrade --all");
        return r.Ok ? Loc.T($"✓ {done} app(s) atualizada(s).") : Loc.T($"{done} app(s) atualizada(s); algumas falharam: ") + Error(r);
    }

    // os IDs vêm do catálogo ou do histórico; mesmo assim, nada de espaços nem aspas na linha de comandos
    static void Check(string id)
    {
        if (!Regex.IsMatch(id, @"^[\w.+-]+$")) throw new InvalidOperationException(Loc.T("App inválida."));
    }

    static string Error(ShellResult r)
    {
        var last = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.Length > 3 && !l.All(c => c is '-' or '\\' or '|' or '/' or '█' or '▒' or ' ')) ?? "";
        return Loc.T($"o winget devolveu o código 0x{r.ExitCode:X8}") + (last.Length > 0 ? $" ({last})" : "");
    }
}
