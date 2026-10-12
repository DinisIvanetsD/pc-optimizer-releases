using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Monitoring;

public sealed record AppTrend(string Name, int Count, double MemoryMb, double Growth10MinMb, double CpuPercent, bool LeakSuspected)
{
    public string DisplayName => Count > 1 ? $"{Name} (×{Count})" : Name;
}

public sealed record MonitorSnapshot(DateTime Time, double CpuPercent, MemoryStatus Memory, IReadOnlyList<AppTrend> Apps);

public sealed record LeakAnalysis(bool Suspected, double SlopeMbPerMin, double R2, double GrowthMb);

/// <summary>
/// Corre em segundo plano: a cada X segundos mede a memória e a CPU de cada app e guarda
/// até 1 hora de histórico. Deteta apps cuja memória sobe de forma contínua (memory leak),
/// RAM quase cheia e CPU presa no máximo.
/// </summary>
public sealed class ResourceMonitor : IDisposable
{
    static readonly TimeSpan Window = TimeSpan.FromHours(1);

    readonly TimeSpan _interval;
    readonly Dictionary<string, List<(DateTime T, double Mb)>> _history = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _lastAlert = new(StringComparer.OrdinalIgnoreCase);
    readonly CpuSampler _cpu = new();
    ProcessSampler.RawSample? _prev;
    CancellationTokenSource? _cts;
    TimeSpan _highCpuFor;

    public ResourceMonitor(TimeSpan? interval = null) => _interval = interval ?? TimeSpan.FromSeconds(10);

    public event Action<MonitorSnapshot>? Updated;
    public event Action<Finding>? Alert;

    public bool IsRunning => _cts != null;

    /// <summary>Durante o teste de RAM a memória fica cheia de propósito: não alertar.</summary>
    public bool SuppressRamAlerts { get; set; }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_interval);
            try
            {
                do
                {
                    try { Tick(); } catch { /* nunca deixar o monitor morrer */ }
                }
                while (await timer.WaitForNextTickAsync(ct));
            }
            catch (OperationCanceledException) { }
        }, ct);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    void Tick()
    {
        var now = DateTime.UtcNow;
        var sample = ProcessSampler.Take();
        var mem = SystemInfo.GetMemory();
        var cpu = _cpu.Sample();
        var usage = _prev is null
            ? sample.Apps.Select(kv => new AppUsage(kv.Key, kv.Value.Count, kv.Value.PrivateBytes, 0)).ToList()
            : ProcessSampler.Diff(_prev, sample);
        _prev = sample;
        usage.RemoveAll(u => ProcessSampler.Ignored.Contains(u.Name));

        var trends = new List<AppTrend>(usage.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in usage)
        {
            seen.Add(u.Name);
            if (!_history.TryGetValue(u.Name, out var h)) _history[u.Name] = h = new();
            var mb = u.PrivateBytes / 1048576.0;
            h.Add((now, mb));
            h.RemoveAll(x => now - x.T > Window);

            var tenMinAgo = h.FirstOrDefault(x => x.T >= now.AddMinutes(-10));
            var growth = mb - tenMinAgo.Mb;
            var leak = Analyze(h);
            trends.Add(new AppTrend(u.Name, u.Count, mb, growth, u.CpuPercent, leak.Suspected));

            if (leak.Suspected && Cooldown(u.Name, TimeSpan.FromMinutes(30)))
            {
                var minutes = (h[^1].T - h[0].T).TotalMinutes;
                var f = new Finding
                {
                    Category = "Monitor", Severity = Severity.Warning,
                    Title = Loc.T($"Possível memory leak: {u.Name}"),
                    Description = Loc.T($"A memória do {u.Name} subiu de {h[0].Mb:N0} MB para {mb:N0} MB nos últimos {minutes:N0} minutos ") +
                                  Loc.T($"(+{leak.SlopeMbPerMin:N0} MB/min) e não está a descer. Isto costuma ser um bug da app: ") +
                                  Loc.T("reiniciá-la liberta a memória."),
                };
                if (!ProcessSampler.Protected.Contains(u.Name)) f.Actions.Add(ProcessSampler.KillAction(u.Name));
                Alert?.Invoke(f);
            }
        }
        foreach (var gone in _history.Keys.Where(k => !seen.Contains(k)).ToList()) _history.Remove(gone);

        var byRam = trends.OrderByDescending(t => t.MemoryMb).ToList();
        if (!SuppressRamAlerts && mem.LoadPercent >= 90 && Cooldown("__ram", TimeSpan.FromMinutes(10)))
        {
            Alert?.Invoke(new Finding
            {
                Category = "Monitor", Severity = Severity.Critical,
                Title = Loc.T($"RAM quase cheia ({mem.LoadPercent}%)"),
                Description = Loc.T("O PC vai começar a ficar lento. O que está a gastar mais: ") +
                              string.Join(", ", byRam.Take(3).Select(t => $"{t.DisplayName} ({t.MemoryMb:N0} MB)")) + ".",
                Actions = byRam.Take(2).Where(t => !ProcessSampler.Protected.Contains(t.Name))
                    .Select(t => ProcessSampler.KillAction(t.Name)).ToList(),
            });
        }

        _highCpuFor = cpu >= 95 ? _highCpuFor + _interval : TimeSpan.Zero;
        if (_highCpuFor >= TimeSpan.FromMinutes(1) && Cooldown("__cpu", TimeSpan.FromMinutes(10)))
        {
            var top = trends.OrderByDescending(t => t.CpuPercent).First();
            Alert?.Invoke(new Finding
            {
                Category = "Monitor", Severity = Severity.Warning,
                Title = Loc.T("CPU no máximo há mais de 1 minuto"),
                Description = Loc.T($"O que está a gastar mais: {top.DisplayName} ({top.CpuPercent:N0}%)."),
                Actions = ProcessSampler.Protected.Contains(top.Name) ? new() : new() { ProcessSampler.KillAction(top.Name) },
            });
        }

        Updated?.Invoke(new MonitorSnapshot(DateTime.Now, cpu, mem, byRam.Take(15).ToList()));
    }

    bool Cooldown(string key, TimeSpan period)
    {
        var now = DateTime.UtcNow;
        if (_lastAlert.TryGetValue(key, out var last) && now - last < period) return false;
        _lastAlert[key] = now;
        return true;
    }

    /// <summary>
    /// Regressão linear sobre o histórico de memória. É um leak se, durante pelo menos 5 minutos,
    /// a memória sobe de forma consistente (R² alto), cresce bastante, e o fim está sempre acima do início.
    /// </summary>
    public static LeakAnalysis Analyze(IReadOnlyList<(DateTime T, double Mb)> s, TimeSpan? minSpan = null)
    {
        var none = new LeakAnalysis(false, 0, 0, 0);
        if (s.Count < 12) return none;
        var span = s[^1].T - s[0].T;
        if (span < (minSpan ?? TimeSpan.FromMinutes(5))) return none;

        double n = s.Count, sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
        var t0 = s[0].T;
        foreach (var (t, y) in s)
        {
            var x = (t - t0).TotalMinutes;
            sx += x; sy += y; sxx += x * x; sxy += x * y; syy += y * y;
        }
        var denom = n * sxx - sx * sx;
        if (denom <= 0) return none;
        var slope = (n * sxy - sx * sy) / denom;
        var intercept = (sy - slope * sx) / n;

        double ssRes = 0;
        foreach (var (t, y) in s)
        {
            var e = y - (intercept + slope * (t - t0).TotalMinutes);
            ssRes += e * e;
        }
        var ssTot = syy - sy * sy / n;
        var r2 = ssTot <= 1e-9 ? 0 : 1 - ssRes / ssTot;

        var growth = slope * span.TotalMinutes;
        var third = s.Count / 3;
        var maxStart = s.Take(third).Max(x => x.Mb);
        var minEnd = s.Skip(s.Count - third).Min(x => x.Mb);

        var suspected = slope > 0 && r2 >= 0.8 && growth >= Math.Max(300, s[0].Mb * 0.3) && s[^1].Mb >= 500 && minEnd > maxStart;
        return new LeakAnalysis(suspected, slope, r2, growth);
    }
}
