using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Storage;

public sealed record DriveSummary(string Letter, string Label, string Media, string Model, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsedPercent => TotalBytes == 0 ? 0 : 100.0 * UsedBytes / TotalBytes;
    public string Root => Letter + "\\";
}

public static class StorageDrives
{
    /// <summary>Os discos fixos com o tipo (SSD/HDD), espaço total e livre. Ignora partições pequenas de sistema.</summary>
    public static async Task<List<DriveSummary>> ListAsync()
    {
        var media = await MediaByLetterAsync();
        var list = new List<DriveSummary>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize < 4L << 30) continue;
                var letter = d.Name.TrimEnd('\\');
                var (type, model) = media.GetValueOrDefault(letter[0], (Loc.T("Disco"), ""));
                list.Add(new DriveSummary(letter, string.IsNullOrWhiteSpace(d.VolumeLabel) ? Loc.T("Disco local") : d.VolumeLabel,
                    type, model, d.TotalSize, d.AvailableFreeSpace));
            }
            catch { }
        }
        return list;
    }

    public static bool IsSsd(DriveSummary d) => d.Media.StartsWith("SSD");
    public static bool IsHdd(DriveSummary d) => d.Media == "HDD";

    /// <summary>TRIM ligado no Windows (fsutil). null = não deu para saber.</summary>
    public static async Task<bool?> TrimEnabledAsync()
    {
        try { return ParseTrim((await Shell.RunAsync("fsutil", "behavior query DisableDeleteNotify", 15_000)).StdOut); }
        catch { return null; }
    }

    /// <summary>"NTFS DisableDeleteNotify = 0 (...)": 0 = TRIM ligado, 1 = desligado.</summary>
    public static bool? ParseTrim(string output)
    {
        var m = System.Text.RegularExpressions.Regex.Match(output, @"NTFS\s+DisableDeleteNotify\s*=\s*(\d)");
        return m.Success ? m.Groups[1].Value == "0" : null;
    }

    /// <summary>
    /// Otimiza como o Windows faz no "Otimizar unidades": TRIM nos SSD, desfragmentação nos HDD.
    /// Não apaga nada. Precisa de administrador.
    /// </summary>
    public static async Task<string> OptimizeAsync(DriveSummary d)
    {
        var ssd = IsSsd(d);
        if (!ssd && !IsHdd(d)) throw new InvalidOperationException(Loc.T("Este disco não é SSD nem HDD: o Windows trata dele sozinho."));
        Simulation.Guard(ssd ? Loc.T($"enviar TRIM para {d.Letter}") : Loc.T($"desfragmentar {d.Letter}"));
        var letter = char.ToUpperInvariant(d.Letter[0]);
        if (!char.IsAsciiLetterUpper(letter)) throw new InvalidOperationException(Loc.T("Disco inválido."));
        var r = await Shell.PowerShellAsync($"Optimize-Volume -DriveLetter {letter} {(ssd ? "-ReTrim" : "-Defrag")} -ErrorAction Stop", 6 * 60 * 60_000);
        if (!r.Ok) throw new InvalidOperationException(Loc.T("O Windows não conseguiu otimizar o disco: ") + r.StdErr.Trim().Split('\n')[0]);
        History.ChangeLog.Record(History.ChangeKind.Cleanup, ssd ? Loc.T($"TRIM enviado para {d.Letter}") : Loc.T($"{d.Letter} desfragmentado"), d.Model);
        return ssd ? Loc.T("✓ TRIM feito: o SSD já sabe que blocos estão livres.") : Loc.T("✓ Desfragmentado.");
    }

    static async Task<Dictionary<char, (string Media, string Model)>> MediaByLetterAsync()
    {
        const string script = """
            $pd = @{}; Get-PhysicalDisk | ForEach-Object { $pd[[string]$_.DeviceId] = $_ }
            Get-Partition | Where-Object DriveLetter | ForEach-Object {
              $p = $pd[[string]$_.DiskNumber]
              if ($p) { "$($_.DriveLetter)|$($p.MediaType)|$($p.BusType)|$($p.FriendlyName)" }
            }
            """;
        var map = new Dictionary<char, (string, string)>();
        var r = await Shell.PowerShellAsync(script, 30_000);
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 4 || parts[0].Length == 0) continue;
            var media = parts[1] switch
            {
                "SSD" or "4" => parts[2] is "NVMe" or "17" ? "SSD NVMe" : "SSD",
                "HDD" or "3" => "HDD",
                _ => parts[2] is "NVMe" or "17" ? "SSD NVMe" : parts[2] is "USB" or "7" ? "USB" : Loc.T("Disco"),
            };
            map[char.ToUpperInvariant(parts[0][0])] = (media, parts[3]);
        }
        return map;
    }

    /// <summary>Tamanho de uma pasta (soma de todos os ficheiros, incluindo ocultos), ignorando o que não dá para ler.</summary>
    public static long FolderSize(string path, CancellationToken ct = default)
    {
        // ficheiros com reparse point (ex.: comprimidos pelo Windows) contam; pastas com reparse point
        // (atalhos/junctions) não são seguidas, para não contar a mesma coisa duas vezes
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var e in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos("*", opts))
                {
                    if (e is FileInfo f)
                    {
                        try { total += f.Length; } catch { }
                    }
                    else if ((e.Attributes & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(e.FullName);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return total;
    }
}
