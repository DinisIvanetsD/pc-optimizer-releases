using System.Diagnostics.Eventing.Reader;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCOptimizer.Core.Diagnostics;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Gaming;

/// <summary>Um jogo instalado. VerifyUri abre a verificação de ficheiros da loja (Steam/Epic).</summary>
public sealed record InstalledGame(string Name, string Store, string Folder, string? VerifyUri);

/// <summary>Um crash de um jogo: quando, o executável e (se houver ficheiro .dmp) o módulo e o código do erro.</summary>
public sealed record GameCrash(DateTime When, string Exe, string? Module, uint? Code, string Source);

public enum CrashCause { GraphicsDriver, Memory, Overlay, GameFiles, Unknown }

/// <summary>
/// Encontra os jogos instalados (Steam e Epic) e os crashes de cada um: os .dmp que o Steam guarda
/// (Steam\dumps\crash_jogo.exe_data.dmp) e os registos "Application Error" do Windows.
/// </summary>
public static class GameCrashes
{
    // ---------------------------------------------------------------- jogos instalados

    public static string? SteamPath => (Reg.Get(@"HKCU\Software\Valve\Steam", "SteamPath") as string)?.Replace('/', '\\');

    public static List<InstalledGame> Installed()
    {
        var games = new List<InstalledGame>();
        if (SteamPath is string steam)
            foreach (var lib in SteamLibraries(steam))
                foreach (var acf in SafeFiles(Path.Combine(lib, "steamapps"), "appmanifest_*.acf"))
                    try
                    {
                        var t = File.ReadAllText(acf);
                        string? V(string key) => Regex.Match(t, $"\"{key}\"\\s+\"([^\"]*)\"") is { Success: true } m ? m.Groups[1].Value : null;
                        if (V("appid") is not string id || V("name") is not string name || V("installdir") is not string dir) continue;
                        if (Regex.IsMatch(name, "Steamworks|Redistributable|Proton|Steam Linux", RegexOptions.IgnoreCase)) continue;
                        games.Add(new(name, "Steam", Path.Combine(lib, "steamapps", "common", dir), $"steam://validate/{id}"));
                    }
                    catch { }

        var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        foreach (var item in SafeFiles(epic, "*.item"))
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var r = doc.RootElement;
                string S(string k) => r.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
                if (S("InstallLocation") is not { Length: > 0 } loc) continue;
                var uri = $"com.epicgames.launcher://apps/{S("CatalogNamespace")}%3A{S("CatalogItemId")}%3A{S("AppName")}?action=verify&silent=false";
                games.Add(new(S("DisplayName"), "Epic Games", loc, uri));
            }
            catch { }
        return games.Where(g => Directory.Exists(g.Folder)).ToList();
    }

    /// <summary>libraryfolders.vdf: "path" "D:\\SteamLibrary".</summary>
    static IEnumerable<string> SteamLibraries(string steam)
    {
        var list = new List<string> { steam };
        try
        {
            var vdf = File.ReadAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"));
            list.AddRange(Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
        }
        catch { }
        return list.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Os executáveis do jogo (nomes, sem pasta).</summary>
    static HashSet<string> Exes(InstalledGame g)
    {
        try
        {
            return Directory.EnumerateFiles(g.Folder, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true })
                .Select(Path.GetFileName).Where(n => n != null && !Regex.IsMatch(n, "unins|crash|setup|redist|vc_redist|dxsetup|launcher", RegexOptions.IgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    // ---------------------------------------------------------------- crashes

    /// <summary>Crashes dos últimos <paramref name="days"/> dias, por jogo (só os jogos com crashes).</summary>
    public static Dictionary<InstalledGame, List<GameCrash>> Find(IReadOnlyList<InstalledGame> games, int days = 30)
    {
        var since = DateTime.Now.AddDays(-days);
        var result = new Dictionary<InstalledGame, List<GameCrash>>();
        var dumps = SteamPath is string s ? SafeFiles(Path.Combine(s, "dumps"), "crash_*.dmp").Select(f => new FileInfo(f)).Where(f => f.LastWriteTime >= since).ToList() : new();
        var events = AppErrorEvents(days);

        foreach (var g in games)
        {
            var exes = Exes(g);
            if (exes.Count == 0) continue;
            var crashes = new List<GameCrash>();
            foreach (var d in dumps)
            {
                var m = Regex.Match(d.Name, @"^crash_(.+?\.exe)_", RegexOptions.IgnoreCase);
                if (!m.Success || !exes.Contains(m.Groups[1].Value)) continue;
                var info = ReadDump(d.FullName);
                crashes.Add(new(d.LastWriteTime, m.Groups[1].Value, info?.Module, info?.Code, "Steam"));
            }
            foreach (var e in events.Where(e => e.Path.StartsWith(g.Folder, StringComparison.OrdinalIgnoreCase)))
                crashes.Add(new(e.When, e.Exe, e.Module, e.Code, Loc.T("Registos do Windows")));
            if (crashes.Count > 0) result[g] = crashes.OrderByDescending(c => c.When).ToList();
        }
        return result;
    }

    sealed record AppError(DateTime When, string Exe, string Path, string Module, uint? Code);

    static List<AppError> AppErrorEvents(int days)
    {
        var list = new List<AppError>();
        try
        {
            foreach (var r in EventLogCheck.Query("Application", "Provider[@Name='Application Error'] and (EventID=1000)", TimeSpan.FromDays(days)))
                using (r)
                {
                    string P(int i) => r.Properties.Count > i ? r.Properties[i].Value?.ToString() ?? "" : "";
                    uint? code = uint.TryParse(P(6).Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var c) ? c : null;
                    list.Add(new(r.TimeCreated ?? DateTime.MinValue, P(0), P(10), P(3), code));
                }
        }
        catch { }
        return list;
    }

    // ---------------------------------------------------------------- ficheiros .dmp (minidump de aplicação, formato MDMP)

    /// <summary>Lê do .dmp o código do erro e o módulo (dll/exe) onde aconteceu. Null se não der.</summary>
    public static (string? Module, uint Code)? ReadDump(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            using var r = new BinaryReader(f);
            if (r.ReadUInt32() != 0x504D444D) return null; // "MDMP"
            r.ReadUInt32(); // versão
            var streams = r.ReadUInt32();
            var dirRva = r.ReadUInt32();
            uint exRva = 0, modRva = 0;
            for (var i = 0; i < streams; i++)
            {
                f.Position = dirRva + i * 12;
                var type = r.ReadUInt32();
                r.ReadUInt32(); // tamanho
                var rva = r.ReadUInt32();
                if (type == 6) exRva = rva;      // ExceptionStream
                else if (type == 4) modRva = rva; // ModuleListStream
            }
            if (exRva == 0) return null;
            f.Position = exRva + 8; // ThreadId + alinhamento
            var code = r.ReadUInt32();
            r.ReadUInt32(); r.ReadUInt64(); // flags, registo encadeado
            var address = r.ReadUInt64();

            string? module = null;
            if (modRva != 0)
            {
                f.Position = modRva;
                var count = r.ReadUInt32();
                for (var i = 0; i < count && module == null; i++)
                {
                    f.Position = modRva + 4 + i * 108L; // MINIDUMP_MODULE tem 108 bytes
                    var start = r.ReadUInt64();
                    var size = r.ReadUInt32();
                    r.ReadUInt32(); r.ReadUInt32(); // checksum, data
                    var nameRva = r.ReadUInt32();
                    if (address < start || address >= start + size) continue;
                    f.Position = nameRva;
                    var len = r.ReadInt32();
                    module = Path.GetFileName(Encoding.Unicode.GetString(r.ReadBytes(len)));
                }
            }
            return (module, code);
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- explicação

    public static string CodeName(uint code) => code switch
    {
        0xC0000005 => Loc.T("acesso inválido à memória"),
        0xC0000374 => Loc.T("memória corrompida"),
        0xC0000409 => Loc.T("o jogo detetou dados estragados e fechou-se"),
        0x80000003 => Loc.T("erro fatal do próprio jogo"),
        0xC000001D => Loc.T("instrução inválida"),
        0xC00000FD => Loc.T("falta de memória na pilha"),
        _ => $"0x{code:X8}",
    };

    /// <summary>
    /// Conclusão a partir dos últimos crashes. Se cada crash acontece num sítio diferente, o problema não é um ficheiro:
    /// é a memória (RAM instável, overclock/XMP). Senão, o módulo que mais se repete diz a causa.
    /// </summary>
    public static (CrashCause Cause, string Text) Verdict(IReadOnlyList<GameCrash> crashes)
    {
        var recent = crashes.Where(c => c.Module != null).Take(10).ToList();
        var top = recent.GroupBy(c => c.Module!, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
        var distinct = recent.Select(c => c.Module!).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (recent.Count >= 3 && distinct >= 3 && top!.Count() * 10 < recent.Count * 6)
            return (CrashCause.Memory, Loc.T($"Os últimos crashes aconteceram em {distinct} sítios diferentes do jogo e do sistema. ") +
                Loc.T("Isto quase nunca é um ficheiro estragado: costuma ser memória instável (RAM com XMP/EXPO ou overclock). Testa a RAM; se der erros, baixa a velocidade na BIOS."));
        var cause = top is null ? CrashCause.Unknown : Cause(top.Key, top.First().Code);
        return (cause, cause switch
        {
            CrashCause.GraphicsDriver => Loc.T($"O crash acontece quase sempre no driver da placa gráfica ({top!.Key}). Reinstala ou atualiza o driver gráfico."),
            CrashCause.Overlay => Loc.T($"O crash acontece num overlay ({top!.Key}). Desliga os overlays (Steam, Discord, MSI Afterburner) e testa."),
            CrashCause.Memory => Loc.T("O crash é de memória corrompida. Pode ser RAM instável ou um programa que se mete no jogo (overlays). Testa a RAM."),
            CrashCause.GameFiles => Loc.T($"O crash acontece quase sempre em {top!.Key}, um ficheiro do jogo. Verifica os ficheiros: a loja repara o que estiver estragado."),
            _ => Loc.T("Verifica os ficheiros do jogo; se continuar, atualiza o driver gráfico e testa a RAM."),
        });
    }

    /// <summary>A causa mais provável a partir do módulo e do código (do crash mais frequente).</summary>
    public static CrashCause Cause(string? module, uint? code)
    {
        var m = module?.ToLowerInvariant() ?? "";
        if (Regex.IsMatch(m, @"^(nvwgf2um|nvoglv|nvd3dum|nvlddmkm|nvcuda|nvgpucomp|amdxx|atiumd|atio6|amdvlk|igd\w+|ig\d+icd)")) return CrashCause.GraphicsDriver;
        if (Regex.IsMatch(m, @"gameoverlayrenderer|discordhook|rtsshooks|overlay|obs-vulkan|graphics-hook|mshook")) return CrashCause.Overlay;
        if (code == 0xC0000374 || (m is "ntdll.dll" or "kernelbase.dll" && code == 0xC0000005)) return CrashCause.Memory;
        if (m.Length > 0 && !m.StartsWith("ntdll") && !m.StartsWith("kernel") && !m.StartsWith("d3d") && !m.StartsWith("dxgi")) return CrashCause.GameFiles;
        return CrashCause.Unknown;
    }
}
