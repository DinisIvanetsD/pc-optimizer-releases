using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PCOptimizer.Core.Util;
using PCOptimizer.Core.History;

namespace PCOptimizer.Core.Storage;

public enum AppKind { Program, Game, Store, Essential }

public enum AppVerdict { Essential, InUse, Occasional, Unused, Useless }

public sealed class InstalledApp
{
    public required string Name { get; init; }
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public AppKind Kind { get; set; }
    public required string Source { get; init; } // "Programa", "Steam", "Epic Games", "Microsoft Store"
    public string? InstallLocation { get; init; }
    public string? MainExe { get; init; }
    public string Drive { get; init; } = "?";
    public long? SizeBytes { get; set; }
    public bool SizeMeasured { get; set; }
    public DateTime? InstallDate { get; init; }
    public DateTime? LastUsed { get; set; }
    public string? UninstallCommand { get; init; }
    public string? QuietUninstallCommand { get; init; }
    /// <summary>Chave do registo do programa (para confirmar que foi mesmo desinstalado).</summary>
    public string? RegistryKeyPath { get; init; }
    public string? SteamAppId { get; init; }
    public string? PackageFullName { get; init; }
    public string? PackageFamilyName { get; init; }

    public int Utility { get; set; }
    public AppVerdict Verdict { get; set; }
    public string Reason { get; set; } = "";
    public bool CanUninstall => Kind != AppKind.Essential && (UninstallCommand != null || SteamAppId != null || PackageFullName != null || Source == "Epic Games");
}

/// <summary>
/// Inventário de tudo o que está instalado, com tamanho, disco e uma pontuação de "utilidade"
/// (0 = provavelmente inútil, 100 = essencial ou usado todos os dias).
/// </summary>
public static class AppInventory
{
    public static async Task<List<InstalledApp>> ScanAsync(IProgress<string>? progress = null)
    {
        progress?.Report(Loc.T("A ler os programas instalados…"));
        var apps = new List<InstalledApp>();
        apps.AddRange(await Task.Run(ReadSteam));
        apps.AddRange(await Task.Run(ReadEpic));
        var gameFolders = apps.Where(a => a.InstallLocation != null).Select(a => a.InstallLocation!.TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        apps.AddRange(await Task.Run(() => ReadRegistry(gameFolders)));
        progress?.Report(Loc.T("A ler as apps da Microsoft Store…"));
        apps.AddRange(await ReadStoreAsync());

        progress?.Report(Loc.T("A ver o que usas e o que não usas…"));
        var usage = await Task.Run(UsageHistory.Load);
        await Task.Run(() =>
        {
            foreach (var a in apps)
            {
                if (a.Source == "Microsoft Store" && a.PackageFamilyName != null)
                    a.LastUsed ??= usage.LastUsedStoreApp(a.PackageFamilyName);
                else
                    a.LastUsed = Max(a.LastUsed, usage.LastUsedIn(a.InstallLocation, a.MainExe));
                Classify(a);
            }
        });
        return apps;
    }

    static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    // ---------------- Programas (registo do Windows) ----------------

    static IEnumerable<InstalledApp> ReadRegistry(HashSet<string> skipFolders)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var (hive, path) in roots)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            if (key is null) continue;
            foreach (var subName in key.GetSubKeyNames())
            {
                using var k = key.OpenSubKey(subName);
                if (k is null) continue;
                var name = (k.GetValue("DisplayName") as string)?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                if (k.GetValue("ParentKeyName") != null) continue; // atualizações de outro programa
                if (k.GetValue("ReleaseType") is string rt && rt.Contains("Update", StringComparison.OrdinalIgnoreCase)) continue;
                if (Regex.IsMatch(name, @"^(Update for|Security Update|Hotfix)|\(KB\d+\)")) continue;

                var uninstall = k.GetValue("UninstallString") as string;
                if (uninstall != null && uninstall.Contains("steam://uninstall", StringComparison.OrdinalIgnoreCase)) continue;

                var version = k.GetValue("DisplayVersion") as string ?? "";
                if (!seen.Add(name + "|" + version)) continue;

                var icon = (k.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"');
                var location = (k.GetValue("InstallLocation") as string)?.Trim().Trim('"');
                if (string.IsNullOrEmpty(location) || !Directory.Exists(location))
                    location = FolderFromExe(icon) ?? FolderFromExe(ExeFromCommand(uninstall));
                if (location != null && skipFolders.Contains(location.TrimEnd('\\'))) continue; // já veio do Steam/Epic

                long? size = k.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : null;
                DateTime? installed = DateTime.TryParseExact(k.GetValue("InstallDate") as string, "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

                yield return new InstalledApp
                {
                    Name = name,
                    Publisher = (k.GetValue("Publisher") as string ?? "").Trim(),
                    Version = version,
                    Source = "Programa",
                    Kind = AppKind.Program,
                    InstallLocation = location,
                    MainExe = icon != null && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? icon : null,
                    Drive = DriveOf(location ?? icon),
                    SizeBytes = size,
                    InstallDate = installed,
                    UninstallCommand = uninstall,
                    QuietUninstallCommand = k.GetValue("QuietUninstallString") as string,
                    RegistryKeyPath = (hive == RegistryHive.LocalMachine ? @"HKLM\" : @"HKCU\") + path + "\\" + subName,
                };
            }
        }
    }

    static string? ExeFromCommand(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        cmd = cmd.Trim();
        if (cmd.StartsWith('"')) return cmd[1..].Split('"')[0];
        var i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i > 0 ? cmd[..(i + 4)] : null;
    }

    /// <summary>Pasta de um .exe, exceto pastas genéricas (Windows, caches de instaladores).</summary>
    static string? FolderFromExe(string? exe)
    {
        if (string.IsNullOrEmpty(exe) || !exe.Contains(":\\")) return null;
        var dir = Path.GetDirectoryName(exe);
        if (dir is null || !Directory.Exists(dir)) return null;
        var bad = new[] { @"\Windows\", @"\Package Cache\", @"\Installer\", @"\System32", @"\ProgramData\{" };
        if (bad.Any(b => (dir + "\\").Contains(b, StringComparison.OrdinalIgnoreCase))) return null;
        var root = Path.GetPathRoot(dir)?.TrimEnd('\\');
        var pf = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        if (string.Equals(dir.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase) || pf.Any(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase))) return null;
        return dir;
    }

    static string DriveOf(string? path) =>
        !string.IsNullOrEmpty(path) && path.Length >= 2 && path[1] == ':' ? char.ToUpperInvariant(path[0]) + ":" : "?";

    // ---------------- Steam ----------------

    static IEnumerable<InstalledApp> ReadSteam()
    {
        var steam = Reg.Get(@"HKCU\Software\Valve\Steam", "SteamPath") as string;
        if (string.IsNullOrEmpty(steam)) yield break;
        steam = steam.Replace('/', '\\');
        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string text;
                try { text = File.ReadAllText(acf); } catch { continue; }
                string? V(string key) { var m = Regex.Match(text, $"\"{key}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase); return m.Success ? m.Groups[1].Value : null; }

                var appId = V("appid");
                var name = V("name");
                if (appId is null || name is null) continue;
                var dir = Path.Combine(apps, "common", V("installdir") ?? name);
                long.TryParse(V("SizeOnDisk"), out var size);
                long.TryParse(V("LastPlayed"), out var lastPlayed);
                var isRedist = appId == "228980" || name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase);

                yield return new InstalledApp
                {
                    Name = name,
                    Publisher = "Steam",
                    Source = "Steam",
                    Kind = isRedist ? AppKind.Essential : AppKind.Game,
                    InstallLocation = Directory.Exists(dir) ? dir : null,
                    Drive = DriveOf(lib),
                    SizeBytes = size > 0 ? size : null,
                    SizeMeasured = size > 0,
                    LastUsed = lastPlayed > 0 ? DateTimeOffset.FromUnixTimeSeconds(lastPlayed).LocalDateTime : null,
                    SteamAppId = appId,
                };
            }
        }
    }

    // ---------------- Epic Games ----------------

    static IEnumerable<InstalledApp> ReadEpic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            JsonElement root;
            try { root = JsonDocument.Parse(File.ReadAllText(file)).RootElement; } catch { continue; }
            string? S(string p) => root.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var name = S("DisplayName");
            var location = S("InstallLocation");
            if (name is null) continue;
            long size = root.TryGetProperty("InstallSize", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
            var exe = S("LaunchExecutable");
            yield return new InstalledApp
            {
                Name = name,
                Publisher = "Epic Games",
                Source = "Epic Games",
                Kind = AppKind.Game,
                InstallLocation = location != null && Directory.Exists(location) ? location : null,
                MainExe = location != null && exe != null ? Path.Combine(location, exe) : null,
                Drive = DriveOf(location),
                SizeBytes = size > 0 ? size : null,
                SizeMeasured = size > 0,
            };
        }
    }

    // ---------------- Microsoft Store ----------------

    static async Task<List<InstalledApp>> ReadStoreAsync()
    {
        const string script = """
            Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -eq 'Store' } | ForEach-Object {
              $n = $_.Name
              try { $m = Get-AppxPackageManifest $_; $d = $m.Package.Properties.DisplayName; if ($d -and $d -notlike 'ms-resource*') { $n = $d } } catch {}
              "$n|$($_.Publisher -replace '^CN=([^,]+).*','$1')|$($_.Version)|$($_.PackageFullName)|$($_.PackageFamilyName)|$($_.InstallLocation)"
            }
            """;
        var list = new List<InstalledApp>();
        var r = await Shell.PowerShellAsync(script, 120_000);
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split('|');
            if (p.Length < 6) continue;
            list.Add(new InstalledApp
            {
                Name = PrettyStoreName(p[0]),
                Publisher = p[1],
                Version = p[2],
                Source = "Microsoft Store",
                Kind = AppKind.Store,
                PackageFullName = p[3],
                PackageFamilyName = p[4],
                Drive = DriveOf(p[5]),
            });
        }
        return list;
    }

    static readonly Dictionary<string, string> KnownStoreNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.ScreenSketch"] = Loc.T("Ferramenta de Recorte"),
        ["Microsoft.WindowsCalculator"] = Loc.T("Calculadora"),
        ["Microsoft.ZuneMusic"] = Loc.T("Leitor de Multimédia"),
        ["Microsoft.ZuneVideo"] = Loc.T("Filmes e TV"),
        ["Microsoft.WindowsNotepad"] = Loc.T("Bloco de Notas"),
        ["Microsoft.Paint"] = Loc.T("Paint"),
        ["Microsoft.Windows.Photos"] = Loc.T("Fotografias"),
        ["Microsoft.WindowsCamera"] = Loc.T("Câmara"),
        ["Microsoft.WindowsAlarms"] = Loc.T("Relógio"),
        ["Microsoft.WindowsSoundRecorder"] = Loc.T("Gravador de Som"),
        ["Microsoft.BingWeather"] = Loc.T("Meteorologia"),
        ["Microsoft.BingNews"] = Loc.T("Microsoft News"),
        ["Microsoft.GetHelp"] = Loc.T("Obter Ajuda"),
        ["Microsoft.Getstarted"] = Loc.T("Microsoft Tips"),
        ["Microsoft.WindowsFeedbackHub"] = Loc.T("Feedback Hub"),
        ["Microsoft.MicrosoftSolitaireCollection"] = Loc.T("Microsoft Solitaire Collection"),
        ["Microsoft.MicrosoftStickyNotes"] = Loc.T("Notas Autoadesivas"),
        ["Microsoft.Todos"] = Loc.T("Microsoft To Do"),
        ["Microsoft.PowerAutomateDesktop"] = Loc.T("Power Automate"),
        ["Microsoft.XboxGamingOverlay"] = Loc.T("Xbox Game Bar"),
        ["Microsoft.GamingApp"] = Loc.T("Xbox"),
        ["Microsoft.YourPhone"] = Loc.T("Ligação Telemóvel"),
        ["Microsoft.OutlookForWindows"] = Loc.T("Outlook"),
        ["Microsoft.MicrosoftOfficeHub"] = Loc.T("Microsoft 365 (Office)"),
        ["MicrosoftCorporationII.QuickAssist"] = Loc.T("Assistência Rápida"),
        ["Microsoft.WindowsTerminal"] = Loc.T("Terminal"),
        ["AppleInc.AppleMusicWin"] = Loc.T("Apple Music"),
        ["AppleInc.iCloud"] = Loc.T("iCloud"),
        ["SpotifyAB.SpotifyMusic"] = Loc.T("Spotify"),
        ["5319275A.WhatsAppDesktop"] = Loc.T("WhatsApp"),
        ["Clipchamp.Clipchamp"] = Loc.T("Clipchamp"),
    };

    /// <summary>As apps da Loja muitas vezes só têm o nome técnico do pacote ("Microsoft.ScreenSketch").</summary>
    static string PrettyStoreName(string name)
    {
        if (KnownStoreNames.TryGetValue(name, out var known)) return known;
        if (name.Contains(' ') || !name.Contains('.')) return name; // já é um nome legível (ex.: "WhatsApp", "ChatGPT")
        var last = name.Split('.').Last();
        return Regex.Replace(last, "(?<=[a-z])(?=[A-Z])", " ");
    }

    // ---------------- Classificação ----------------

    static readonly Regex EssentialRx = new(
        @"Visual C\+\+|\.NET|Runtime|Redistributable|Driver|Chipset|PhysX|NVIDIA (Graphics|HD Audio|FrameView|USBC)|AMD (Software|Chipset)|Realtek|Intel\(R\)|" +
        @"Microsoft Edge|WebView2|DirectX|Windows Software Development|Microsoft Update Health|GameInput|Windows PC Health|Microsoft Store|Xbox Identity|" +
        @"App Installer|Windows Terminal|Desktop App Installer|VCLibs|UI\.Xaml|Windows Security|Bonjour Service$|^Store Experience Host$|^Xbox (TCUI|Speech)", RegexOptions.IgnoreCase);

    static readonly Regex UselessRx = new(
        @"McAfee|Norton|WildTangent|Candy ?Crush|Booking\.com|Secure Browser|Driver ?Booster|Driver ?Easy|Advanced ?SystemCare|IObit|Wise Care|" +
        @"PC App Store|Toolbar|Segurazo|ByteFence|Reimage|Restoro|Outbyte|PC ?Accelerate|Bloatware|Microsoft Solitaire|Clipchamp|Microsoft News|" +
        @"MSN Weather|Get Help|Microsoft Tips|Feedback Hub|Disney\+|Spotify Music Trial|LinkedIn|TikTok|Instagram|Facebook|Amazon Prime Video|" +
        @"HP (Support Assistant|JumpStart|Sure|Documentation)|Dell (SupportAssist|Digital Delivery)|Lenovo Vantage|Opera GX Browser Assistant|WinZip|Avast Cleanup|AVG TuneUp",
        RegexOptions.IgnoreCase);

    static void Classify(InstalledApp a)
    {
        var size = a.SizeBytes ?? 0;
        var bigText = size >= 1L << 30 ? $" Ocupa {SystemInfo.FormatBytes(size)}." : "";

        if (a.Kind == AppKind.Essential || EssentialRx.IsMatch(a.Name))
        {
            a.Kind = AppKind.Essential;
            a.Verdict = AppVerdict.Essential;
            a.Utility = 100;
            a.Reason = Loc.T("Componente do sistema, driver ou runtime de que outros programas precisam. Não removas.");
            return;
        }
        if (UselessRx.IsMatch(a.Name))
        {
            a.Verdict = AppVerdict.Useless;
            a.Utility = 5;
            a.Reason = Loc.T("Normalmente é software pré-instalado, publicidade ou \"otimizador\" que não faz falta.") + bigText;
            return;
        }

        if (a.LastUsed is DateTime used)
        {
            var days = (DateTime.Now - used).TotalDays;
            (a.Verdict, a.Utility, a.Reason) = days switch
            {
                <= 7 => (AppVerdict.InUse, 95, Loc.T("Usaste esta semana.")),
                <= 30 => (AppVerdict.InUse, 80, Loc.T("Usaste este mês.")),
                <= 90 => (AppVerdict.Occasional, 55, days < 60 ? Loc.T("Não usas há 1 mês.") : Loc.T($"Não usas há {days / 30:N0} meses.")),
                _ => (AppVerdict.Unused, 25, Loc.T($"Não usas há {Months(days)}.") + bigText),
            };
        }
        else if (a.Kind == AppKind.Game && a.Source == "Steam")
        {
            (a.Verdict, a.Utility, a.Reason) = (AppVerdict.Unused, 15, Loc.T("Nunca jogaste (segundo o Steam).") + bigText);
        }
        else
        {
            var old = a.InstallDate is DateTime inst && (DateTime.Now - inst).TotalDays > 180;
            (a.Verdict, a.Utility, a.Reason) = old
                ? (AppVerdict.Unused, 35, Loc.T($"Instalado há {Months((DateTime.Now - a.InstallDate!.Value).TotalDays)} e sem registo de uso.") + bigText +
                                          Loc.T(" (Pode ser uma app que corre em segundo plano: confirma antes de remover.)"))
                : (AppVerdict.Occasional, 45, Loc.T("Sem registo de uso. Pode ser uma app que corre em segundo plano ou que abres de outra forma."));
        }

        // jogos grandes que não usas descem um pouco mais na lista
        if (a.Verdict == AppVerdict.Unused && size >= 20L << 30) a.Utility = Math.Max(5, a.Utility - 10);
    }

    static string Months(double days) => days >= 365 ? $"{days / 365:N1} ano(s)" : $"{days / 30:N0} meses";

    /// <summary>Atualiza a classificação depois de saber o tamanho real.</summary>
    public static void Reclassify(InstalledApp a) => Classify(a);

    // ---------------- Desinstalar ----------------

    /// <summary>Done = confirmado que saiu do PC; Pending = abriu outra app (Steam/Epic) onde tens de confirmar.</summary>
    public sealed record UninstallResult(bool Done, bool Pending, string Message);

    /// <summary>
    /// Desinstala dentro da app. Tenta o modo silencioso (sem janelas) quando o tipo de instalador o
    /// permite (MSI, Inno Setup, NSIS, Squirrel, apps da Loja). Se não der, abre o desinstalador do
    /// próprio programa e espera que termine. No fim confirma no registo que o programa saiu mesmo.
    /// </summary>
    public static async Task<UninstallResult> UninstallAsync(InstalledApp a, CancellationToken ct = default)
    {
        Simulation.Guard($"desinstalar {a.Name}");
        var r = await UninstallCoreAsync(a, ct);
        if (r.Done)
            ChangeLog.Record(ChangeKind.Uninstall, $"Desinstalado: {a.Name}",
                string.Join(" · ", new[] { a.Publisher, a.Version, a.SizeBytes is long s ? Util.SystemInfo.FormatBytes(s) : null }
                    .Where(x => !string.IsNullOrWhiteSpace(x))));
        return r;
    }

    static async Task<UninstallResult> UninstallCoreAsync(InstalledApp a, CancellationToken ct)
    {
        if (a.Kind == AppKind.Essential)
            return new(false, false, Loc.T("Bloqueado: é um componente essencial do sistema."));
        if (a.SteamAppId != null)
        {
            Shell.Open($"steam://uninstall/{a.SteamAppId}");
            return new(false, true, Loc.T("O Steam abriu a confirmação: carrega em \"Desinstalar\" lá."));
        }
        if (a.Source == "Epic Games")
        {
            Shell.Open("com.epicgames.launcher://store/library");
            return new(false, true, Loc.T("A Epic não permite desinstalar por outras apps: abri a biblioteca, clica nos 3 pontos do jogo › Desinstalar."));
        }
        if (a.PackageFullName != null)
        {
            var r = await Shell.PowerShellAsync($"Remove-AppxPackage -Package {Shell.Quote(a.PackageFullName)} -ErrorAction Stop");
            return r.Ok ? new(true, false, "✓ Desinstalado.") : new(false, false, Loc.T("Erro: ") + Shell.CleanError(r));
        }
        if (a.UninstallCommand is null && a.QuietUninstallCommand is null)
            return new(false, false, Loc.T("Este programa não tem desinstalador. Usa Definições › Aplicações."));

        var (file, args, silent) = SilentCommand(a);
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args) { UseShellExecute = true });
            if (p != null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(15));
                try { await p.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new(false, false, Loc.T("Cancelado (o pedido de administrador foi recusado)."));
        }

        // muitos desinstaladores (NSIS, Inno) copiam-se para uma pasta temporária e terminam logo:
        // esperamos até 90 s que a entrada do programa desapareça do registo
        for (var i = 0; i < 45 && StillInstalled(a); i++) await Task.Delay(2000, ct);
        return StillInstalled(a)
            ? new(false, false, silent
                ? Loc.T("O desinstalador terminou, mas o programa ainda aparece instalado. Pode estar à espera de uma confirmação numa janela, ou precisar de reiniciar.")
                : Loc.T("Abri o desinstalador do programa: segue os passos na janela dele."))
            : new(true, false, "✓ Desinstalado.");
    }

    static bool StillInstalled(InstalledApp a) => a.RegistryKeyPath is null || Reg.KeyExists(a.RegistryKeyPath);

    /// <summary>Escolhe o comando: o "silencioso" oficial, ou os parâmetros silenciosos do tipo de instalador.</summary>
    static (string File, string Args, bool Silent) SilentCommand(InstalledApp a)
    {
        if (!string.IsNullOrWhiteSpace(a.QuietUninstallCommand))
            return ("cmd.exe", $"/c \"{a.QuietUninstallCommand}\"", true);

        var cmd = a.UninstallCommand!.Trim();
        if (cmd.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase))
        {
            var guid = Regex.Match(cmd, @"\{[0-9A-F\-]+\}", RegexOptions.IgnoreCase).Value;
            return ("msiexec.exe", $"/x {guid} /qn /norestart", true);
        }

        var exe = ExeFromCommand(cmd);
        if (exe is null) return ("cmd.exe", "/c " + cmd, false);
        var args = cmd.StartsWith('"') ? cmd[Math.Min(cmd.Length, exe.Length + 2)..].Trim() : cmd[(cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4)..].Trim();
        var name = Path.GetFileName(exe);

        if (Regex.IsMatch(name, @"^unins\d+\.exe$", RegexOptions.IgnoreCase))            // Inno Setup
            return (exe, $"{args} /VERYSILENT /SUPPRESSMSGBOXES /NORESTART".Trim(), true);
        if (name.Equals("Update.exe", StringComparison.OrdinalIgnoreCase) && args.Contains("--uninstall")) // Squirrel (Discord, etc.)
            return (exe, args + " -s", true);
        if (Regex.IsMatch(name, @"^(uninst|uninstall|uninstaller)[\w\- ]*\.exe$", RegexOptions.IgnoreCase) && !args.Contains("/S")) // NSIS
            return (exe, $"{args} /S".Trim(), true);
        return (exe, args, false);                                                         // desconhecido: abre normalmente
    }
}