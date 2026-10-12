using System.Text;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Storage;

/// <summary>
/// Quando foi usado pela última vez cada programa. Fontes (nenhuma é perfeita, por isso juntamos):
///  - UserAssist: o Windows regista os programas abertos pelo menu Iniciar / Explorador (por utilizador).
///  - Prefetch: o Windows guarda um ficheiro por cada .exe executado (só legível como administrador).
/// </summary>
public sealed class UsageHistory
{
    readonly Dictionary<string, DateTime> _byPath = new(StringComparer.OrdinalIgnoreCase);   // caminho completo do .exe
    readonly Dictionary<string, DateTime> _byExeName = new(StringComparer.OrdinalIgnoreCase); // "discord.exe"
    readonly Dictionary<string, DateTime> _byAppId = new(StringComparer.OrdinalIgnoreCase);   // apps da Loja (AUMID)

    public bool HasPrefetch { get; private set; }

    static readonly Dictionary<string, string> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.GetFolderPath(Environment.SpecialFolder.System),
        ["{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"] = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
        ["{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ["{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    };

    public static UsageHistory Load()
    {
        var h = new UsageHistory();
        h.LoadUserAssist();
        h.LoadPrefetch();
        return h;
    }

    void LoadUserAssist()
    {
        const string root = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
        var (hive, sub) = Reg.Split(root);
        using var baseKey = Reg.OpenRoot(hive);
        using var ua = baseKey.OpenSubKey(sub);
        if (ua is null) return;
        foreach (var guid in ua.GetSubKeyNames())
        {
            using var count = ua.OpenSubKey(guid + @"\Count");
            if (count is null) continue;
            foreach (var valueName in count.GetValueNames())
            {
                if (count.GetValue(valueName) is not byte[] { Length: >= 68 } data) continue;
                var ft = BitConverter.ToInt64(data, 60);
                if (ft <= 0) continue;
                DateTime when;
                try { when = DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch { continue; }
                if (when.Year < 2005) continue;

                var name = Rot13(valueName);
                if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.Contains('\\') && name.Contains('!'))
                {
                    Keep(_byAppId, name.Split('!')[0], when); // app da Loja: "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"
                    continue;
                }
                foreach (var (id, path) in KnownFolders)
                    if (name.StartsWith(id, StringComparison.OrdinalIgnoreCase)) name = path + name[id.Length..];
                if (!name.Contains(":\\")) continue;
                Keep(_byPath, name, when);
                Keep(_byExeName, Path.GetFileName(name), when);
            }
        }
    }

    void LoadPrefetch()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.pf"))
            {
                var dash = f.Name.LastIndexOf('-');
                if (dash <= 0) continue;
                Keep(_byExeName, f.Name[..dash], f.LastWriteTime);
                HasPrefetch = true;
            }
        }
        catch
        {
            // sem administrador não dá para ler
        }
    }

    static void Keep(Dictionary<string, DateTime> d, string key, DateTime when)
    {
        if (!d.TryGetValue(key, out var cur) || when > cur) d[key] = when;
    }

    /// <summary>Executáveis com nomes genéricos que não identificam uma app.</summary>
    static readonly HashSet<string> GenericExe = new(StringComparer.OrdinalIgnoreCase)
    {
        "setup.exe", "uninstall.exe", "uninst.exe", "unins000.exe", "unins001.exe", "update.exe", "updater.exe",
        "crashhandler.exe", "crashpad_handler.exe", "helper.exe", "installer.exe", "vc_redist.x64.exe", "vc_redist.x86.exe",
        "dxsetup.exe", "launcher.exe", "elevate.exe", "notification_helper.exe", "squirrel.exe",
    };

    /// <summary>Última utilização de uma app instalada nesta pasta (procura os .exe lá dentro).</summary>
    public DateTime? LastUsedIn(string? folder, string? mainExe)
    {
        DateTime? best = null;
        void Consider(DateTime? t) { if (t is DateTime v && (best is null || v > best)) best = v; }

        if (!string.IsNullOrEmpty(mainExe))
        {
            if (_byPath.TryGetValue(mainExe, out var t1)) Consider(t1);
            var exeName = Path.GetFileName(mainExe);
            if (!GenericExe.Contains(exeName) && _byExeName.TryGetValue(exeName, out var t2)) Consider(t2);
        }

        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            var prefix = folder.TrimEnd('\\') + "\\";
            foreach (var (path, t) in _byPath)
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) Consider(t);

            try
            {
                var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
                foreach (var exe in Directory.EnumerateFiles(folder, "*.exe", opts).Take(300))
                {
                    var name = Path.GetFileName(exe);
                    if (GenericExe.Contains(name) || name.StartsWith("unins", StringComparison.OrdinalIgnoreCase)) continue;
                    if (_byExeName.TryGetValue(name, out var t)) Consider(t);
                }
            }
            catch { }
        }
        return best;
    }

    public DateTime? LastUsedStoreApp(string packageFamilyName) =>
        _byAppId.TryGetValue(packageFamilyName, out var t) ? t : null;

    static string Rot13(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
                >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
                _ => c,
            });
        }
        return sb.ToString();
    }
}
