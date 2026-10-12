using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCOptimizer.Core.Util;

public sealed record MemoryStatus(ulong TotalBytes, ulong AvailableBytes, uint LoadPercent, ulong CommitUsedBytes, ulong CommitLimitBytes)
{
    public double TotalGb => TotalBytes / 1073741824.0;
    public double UsedGb => (TotalBytes - AvailableBytes) / 1073741824.0;
    public double CommitPercent => CommitLimitBytes == 0 ? 0 : 100.0 * CommitUsedBytes / CommitLimitBytes;
}

public static class SystemInfo
{
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [DllImport("kernel32.dll")]
    internal static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    public static MemoryStatus GetMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref m);
        return new MemoryStatus(m.ullTotalPhys, m.ullAvailPhys, m.dwMemoryLoad,
            m.ullTotalPageFile - m.ullAvailPageFile, m.ullTotalPageFile);
    }

    public static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static string FormatBytes(double bytes) =>
        bytes >= 1073741824 ? $"{bytes / 1073741824:N1} GB" : $"{bytes / 1048576:N0} MB";
}

/// <summary>Mede a utilização total da CPU entre duas chamadas.</summary>
public sealed class CpuSampler
{
    long _idle, _total;

    public CpuSampler() => Sample();

    public double Sample()
    {
        SystemInfo.GetSystemTimes(out var idle, out var kernel, out var user);
        var total = kernel + user; // o tempo "kernel" já inclui o tempo idle
        var dIdle = idle - _idle;
        var dTotal = total - _total;
        _idle = idle;
        _total = total;
        return dTotal <= 0 ? 0 : Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
    }
}

/// <summary>Uso de uma app (todos os processos com o mesmo nome somados, ex.: chrome ×25).</summary>
public sealed record AppUsage(string Name, int Count, long PrivateBytes, double CpuPercent)
{
    public string DisplayName => Count > 1 ? $"{Name} (×{Count})" : Name;
}

public static class ProcessSampler
{
    /// <summary>Processos do sistema que não faz sentido reportar como "apps".</summary>
    public static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "Memory Compression", "Secure System",
    };

    /// <summary>Processos que nunca devem ser fechados pela app.</summary>
    public static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "wininit", "winlogon", "lsass", "services", "smss", "svchost", "dwm", "fontdrvhost",
        "LsaIso", "MsMpEng", "System", "Registry", "Memory Compression", "Secure System", "Idle",
        "audiodg", "sihost", "ctfmon", "RuntimeBroker", "SecurityHealthService", "spoolsv", "WUDFHost",
        "NisSrv", "MpDefenderCoreService",
    };

    public readonly record struct AppRaw(int Count, long PrivateBytes, double CpuMs);

    public sealed record RawSample(DateTime Time, Dictionary<string, AppRaw> Apps);

    public static RawSample Take()
    {
        var apps = new Dictionary<string, AppRaw>(StringComparer.OrdinalIgnoreCase);
        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == 0 || p.Id == self) continue;
                    var name = p.ProcessName;
                    var priv = p.PrivateMemorySize64;
                    double cpu = 0;
                    try { cpu = p.TotalProcessorTime.TotalMilliseconds; } catch { /* sem acesso */ }
                    apps.TryGetValue(name, out var cur);
                    apps[name] = new AppRaw(cur.Count + 1, cur.PrivateBytes + priv, cur.CpuMs + cpu);
                }
                catch
                {
                    // o processo terminou entretanto
                }
            }
        }
        return new RawSample(DateTime.UtcNow, apps);
    }

    public static List<AppUsage> Diff(RawSample before, RawSample after)
    {
        var capacityMs = (after.Time - before.Time).TotalMilliseconds * Environment.ProcessorCount;
        var list = new List<AppUsage>(after.Apps.Count);
        foreach (var (name, v) in after.Apps)
        {
            double cpu = 0;
            if (capacityMs > 0 && before.Apps.TryGetValue(name, out var prev))
                cpu = Math.Clamp((v.CpuMs - prev.CpuMs) / capacityMs * 100, 0, 100);
            list.Add(new AppUsage(name, v.Count, v.PrivateBytes, cpu));
        }
        return list;
    }

    public static async Task<List<AppUsage>> SampleAsync(TimeSpan interval, CancellationToken ct = default)
    {
        var before = Take();
        await Task.Delay(interval, ct);
        return Diff(before, Take());
    }

    /// <summary>Fecha todos os processos com este nome. Devolve quantos foram fechados.</summary>
    public static int Kill(string name)
    {
        Simulation.Guard(Loc.T($"fechar {name}"));
        if (Protected.Contains(name)) return 0;
        var n = 0;
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                try
                {
                    p.Kill();
                    n++;
                }
                catch
                {
                    // sem permissões ou já terminou
                }
            }
        }
        return n;
    }

    public static FixAction KillAction(string name) => new(Loc.T($"Fechar {name}"), () =>
    {
        var n = Kill(name);
        return Task.FromResult(n > 0
            ? Loc.T($"Fechado(s) {n} processo(s) de {name}.")
            : Loc.T("Não foi possível fechar (pode precisar de administrador)."));
    })
    {
        ConfirmText = Loc.T($"Fechar todos os processos \"{name}\"?\n\nO trabalho não guardado nessa app pode perder-se."),
    };
}
