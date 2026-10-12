using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using PCOptimizer.Core.Optimization;
using PCOptimizer.Core.Util;
using PCOptimizer.Core.History;

namespace PCOptimizer.Core.Memory;

/// <summary>
/// A "lista de memória avariada" do Windows (objeto {badmemory} do arranque). As páginas físicas
/// nesta lista nunca são usadas pelo Windows: é a forma oficial de isolar RAM com defeito sem a
/// trocar. Guardamos o estado original para poder repor.
/// </summary>
public static class BadMemoryList
{
    const string SnapKey = "ram.badmemory";

    public static bool HasBackup => SnapshotStore.Has(SnapKey);

    /// <summary>PFNs (números de página física) atualmente isolados.</summary>
    public static async Task<List<ulong>> ReadAsync()
    {
        Admin.Require();
        var r = await Shell.RunAsync("bcdedit", "/enum {badmemory}");
        var list = new List<ulong>();
        var inList = false;
        foreach (var raw in r.StdOut.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("badmemorylist", StringComparison.OrdinalIgnoreCase))
            {
                inList = true;
                line = line["badmemorylist".Length..];
            }
            else if (inList && line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                break; // começou outro campo
            }
            if (!inList) continue;
            foreach (Match m in Regex.Matches(line, @"0x[0-9a-fA-F]+|\b\d+\b"))
            {
                var v = m.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? ulong.Parse(m.Value[2..], NumberStyles.HexNumber)
                    : ulong.Parse(m.Value);
                list.Add(v);
            }
        }
        return list;
    }

    /// <summary>Junta estas páginas à lista. Tem efeito no próximo arranque.</summary>
    public static async Task<int> AddAsync(IEnumerable<ulong> pfns)
    {
        Simulation.Guard(Loc.T("isolar as páginas de RAM com erros (bcdedit)"));
        Admin.Require();
        var current = await ReadAsync();
        SnapshotStore.PutIfMissing(SnapKey, new Dictionary<string, string?> { ["list"] = string.Join(" ", current) });
        var merged = current.Concat(pfns).Distinct().OrderBy(x => x).ToList();
        await SetAsync(merged);
        ChangeLog.Record(ChangeKind.Memory, Loc.T($"{merged.Count - current.Count} página(s) de RAM isoladas"), Loc.T("Tem efeito no próximo arranque."),
            UndoKind.BadMemory, new() { ["k"] = "badmem" });
        return merged.Count - current.Count;
    }

    /// <summary>Repõe a lista que existia antes de a app mexer nela.</summary>
    public static async Task<bool> RevertAsync()
    {
        Simulation.Guard(Loc.T("repor a lista de RAM isolada"));
        Admin.Require();
        var s = SnapshotStore.Get(SnapKey);
        if (s is null) return false;
        var original = (s.GetValueOrDefault("list") ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(ulong.Parse).ToList();
        if (original.Count == 0)
        {
            var r = await Shell.RunAsync("bcdedit", "/deletevalue {badmemory} badmemorylist");
            if (!r.Ok && !r.StdOut.Contains("not found", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Shell.CleanError(r));
        }
        else
        {
            await SetAsync(original);
        }
        SnapshotStore.Remove(SnapKey);
        return true;
    }

    static async Task SetAsync(List<ulong> pfns)
    {
        var r = await Shell.RunAsync("bcdedit", "/set {badmemory} badmemorylist " + string.Join(" ", pfns));
        if (!r.Ok) throw new InvalidOperationException(Shell.CleanError(r));
    }

    /// <summary>Se o disco do Windows tem BitLocker ativo, mexer no arranque pode pedir a chave de recuperação.</summary>
    public static bool IsBitLockerOn()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
                "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume");
            var system = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
            foreach (ManagementObject o in s.Get())
            {
                using (o)
                {
                    if (string.Equals(o["DriveLetter"]?.ToString(), system, StringComparison.OrdinalIgnoreCase))
                        return Convert.ToUInt32(o["ProtectionStatus"]) == 1;
                }
            }
        }
        catch { }
        return false;
    }
}
