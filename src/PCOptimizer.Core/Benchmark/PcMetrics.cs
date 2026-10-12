using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text.Json;
using PCOptimizer.Core.Network;
using PCOptimizer.Core.Optimization;
using PCOptimizer.Core.Settings;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Benchmark;

/// <summary>Uma medição do PC num momento (para comparar antes e depois de otimizar).</summary>
public sealed record MetricSnapshot
{
    public DateTime When { get; init; } = DateTime.Now;
    public string Label { get; init; } = "";
    /// <summary>Tempo de arranque em segundos (exato com administrador; senão, do Windows ligado até ao login).</summary>
    public double? BootSeconds { get; init; }
    public bool BootExact { get; init; }
    public double RamUsedGb { get; init; }
    public double RamPercent { get; init; }
    public int StartupApps { get; init; }
    public int Processes { get; init; }
    public double? PingMs { get; init; }
    public double? JitterMs { get; init; }
    public double SystemDriveFreeGb { get; init; }
    public int? Health { get; init; }
}

public sealed record HealthPoint(DateTime When, int Score);

/// <summary>Uma linha da comparação "antes → agora".</summary>
public sealed record MetricDelta(string Name, string Before, string Now, string Change, bool? Better);

/// <summary>Mede o PC e guarda o histórico (métricas e saúde) em %LOCALAPPDATA%\PCOptimizer.</summary>
public static class PcMetrics
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly object Gate = new();

    static string MetricsFile => Environment.GetEnvironmentVariable("PCOPT_METRICS_FILE") ?? Path.Combine(AppSettings.DataFolder, "metrics.json");
    static string HealthFile => Path.Combine(Path.GetDirectoryName(MetricsFile)!, "health.json");

    // ------------------------------------------------------------------ medir

    public static async Task<MetricSnapshot> MeasureAsync(string label, int? health, CancellationToken ct = default)
    {
        var mem = SystemInfo.GetMemory();
        var boot = await Task.Run(LastBoot, ct);
        var startup = await Task.Run(() => { try { return StartupManager.List().Count(i => i.Enabled); } catch { return 0; } }, ct);
        PingStats? ping = null;
        try { ping = await PingTester.PingAsync("Internet", "1.1.1.1", count: 20, intervalMs: 100, ct: ct); } catch { }
        var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);

        return new MetricSnapshot
        {
            Label = label,
            BootSeconds = boot?.Seconds,
            BootExact = boot?.Exact ?? false,
            RamUsedGb = Math.Round(mem.UsedGb, 1),
            RamPercent = mem.LoadPercent,
            StartupApps = startup,
            Processes = Process.GetProcesses().Length,
            PingMs = ping is { Reachable: true } ? Math.Round(ping.Avg, 1) : null,
            JitterMs = ping is { Reachable: true } ? Math.Round(ping.Jitter, 1) : null,
            SystemDriveFreeGb = Math.Round(sys.AvailableFreeSpace / 1073741824.0, 1),
            Health = health,
        };
    }

    /// <summary>Tempo do último arranque.</summary>
    public static (double Seconds, bool Exact)? LastBoot()
    {
        // exato: Diagnostics-Performance (só com administrador)
        try
        {
            var q = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName, "*[System[EventID=100]]")
                { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            using var e = reader.ReadEvent();
            var m = e is null ? null : System.Text.RegularExpressions.Regex.Match(e.ToXml(), @"Name=['""]BootTime['""]>(\d+)<");
            if (m is { Success: true } && long.TryParse(m.Groups[1].Value, out var ms) && ms > 0)
                return (Math.Round(ms / 1000.0, 1), true);
        }
        catch { }

        // aproximado: do Windows ligado (Kernel-General 12) até ao login (Winlogon 7001)
        try
        {
            var start = Last("Microsoft-Windows-Kernel-General", 12);
            var logon = Last("Microsoft-Windows-Winlogon", 7001);
            if (start is DateTime s && logon is DateTime l && l > s && (l - s).TotalMinutes < 30)
                return (Math.Round((l - s).TotalSeconds, 1), false);
        }
        catch { }
        return null;
    }

    static DateTime? Last(string provider, int id)
    {
        var q = new EventLogQuery("System", PathType.LogName, $"*[System[Provider[@Name='{provider}'] and EventID={id}]]") { ReverseDirection = true };
        using var reader = new EventLogReader(q);
        using var e = reader.ReadEvent();
        return e?.TimeCreated;
    }

    // ------------------------------------------------------------------ guardar

    public static IReadOnlyList<MetricSnapshot> All
    {
        get
        {
            lock (Gate) return Read<List<MetricSnapshot>>(MetricsFile) ?? new();
        }
    }

    public static void Save(MetricSnapshot s)
    {
        lock (Gate)
        {
            var list = Read<List<MetricSnapshot>>(MetricsFile) ?? new();
            list.Add(s);
            Write(MetricsFile, list.OrderBy(x => x.When).TakeLast(200).ToList());
        }
    }

    /// <summary>A medição "antes": a mais antiga das que foram tiradas antes de otimizar, ou a primeira de todas.</summary>
    public static MetricSnapshot? Baseline(IReadOnlyList<MetricSnapshot> all) =>
        all.FirstOrDefault(s => s.Label.StartsWith("Antes", StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();

    /// <summary>Tira a medição "antes" automaticamente, na primeira vez que se otimiza (não volta a tirar).</summary>
    public static async Task EnsureBaselineAsync(string what, int? health)
    {
        if (All.Any(s => s.Label.StartsWith("Antes", StringComparison.OrdinalIgnoreCase))) return;
        Save(await MeasureAsync(Loc.T($"Antes de otimizar ({what})"), health));
    }

    // ------------------------------------------------------------------ saúde ao longo do tempo

    public static IReadOnlyList<HealthPoint> HealthHistory
    {
        get
        {
            lock (Gate) return Read<List<HealthPoint>>(HealthFile) ?? new();
        }
    }

    /// <summary>Guarda a pontuação (no máximo um ponto por hora; guarda 1 ano).</summary>
    public static void RecordHealth(int score)
    {
        lock (Gate)
        {
            var list = Read<List<HealthPoint>>(HealthFile) ?? new();
            if (list.Count > 0 && DateTime.Now - list[^1].When < TimeSpan.FromHours(1))
                list[^1] = new HealthPoint(DateTime.Now, score);
            else
                list.Add(new HealthPoint(DateTime.Now, score));
            Write(HealthFile, list.Where(p => p.When > DateTime.Now.AddDays(-365)).ToList());
        }
    }

    // ------------------------------------------------------------------ comparar

    public static List<MetricDelta> Compare(MetricSnapshot before, MetricSnapshot now)
    {
        var rows = new List<MetricDelta>();

        void Add(string name, double? b, double? n, string unit, bool lowerIsBetter, string fmt = "N1", double tolerance = 0)
        {
            if (b is null && n is null) return;
            string F(double? v) => v is double x ? x.ToString(fmt) + unit : "—";
            bool? better = null;
            var change = "";
            if (b is double bb && n is double nn)
            {
                var diff = nn - bb;
                if (Math.Abs(diff) <= tolerance) change = "igual";
                else
                {
                    better = lowerIsBetter ? diff < 0 : diff > 0;
                    var points = unit == "/100";
                    var pct = bb != 0 && !points ? $" ({Math.Abs(diff / bb) * 100:N0}%)" : "";
                    change = (diff > 0 ? "+" : "−") + Math.Abs(diff).ToString(fmt) + (points ? Loc.T(" pontos") : unit) + pct;
                }
            }
            rows.Add(new MetricDelta(name, F(b), F(n), change, better));
        }

        Add(Loc.T("Saúde do PC"), before.Health, now.Health, "/100", lowerIsBetter: false, "N0");
        Add(now.BootExact ? Loc.T("Tempo de arranque") : Loc.T("Arranque até ao login"), before.BootSeconds, now.BootSeconds, " s", true, "N1", 0.5);
        Add(Loc.T("Programas no arranque"), before.StartupApps, now.StartupApps, "", true, "N0");
        Add(Loc.T("RAM em uso"), before.RamUsedGb, now.RamUsedGb, " GB", true, "N1", 0.2);
        Add("Processos a correr", before.Processes, now.Processes, "", true, "N0", 3);
        Add("Ping (1.1.1.1)", before.PingMs, now.PingMs, " ms", true, "N1", 1);
        Add("Jitter", before.JitterMs, now.JitterMs, " ms", true, "N1", 0.5);
        Add(Loc.T("Espaço livre no disco do sistema"), before.SystemDriveFreeGb, now.SystemDriveFreeGb, " GB", false, "N1", 0.5);
        return rows;
    }

    // ------------------------------------------------------------------

    static T? Read<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default; }
        catch { return default; }
    }

    static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
        }
        catch { }
    }
}
