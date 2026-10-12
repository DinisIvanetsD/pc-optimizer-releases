using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;
using PCOptimizer.Core.Hardware;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Monitoring;

/// <summary>Uma medição por segundo. Null = não deu para medir (ex.: sem placa NVIDIA, ping sem resposta).</summary>
public sealed record RecSample(DateTime Time, double Cpu, double RamFreeGb, double Disk, int? GpuLoad, int? GpuTempC, int? PingMs,
    IReadOnlyList<string> TopApps);

/// <summary>Uma gravação: 30 s antes e 15 s depois de a pessoa carregar em "Aconteceu agora".</summary>
public sealed record Recording(DateTime Mark, string? What, string? PingTarget, IReadOnlyList<RecSample> Samples);

/// <summary>
/// Grava o PC continuamente (CPU, RAM, disco, GPU, ping e as apps que mais gastam) num buffer de 90 s.
/// Quando a pessoa sente o engasgo, <see cref="MarkAsync"/> guarda o que aconteceu à volta desse momento.
/// </summary>
public sealed class ProblemRecorder : IDisposable
{
    public static readonly TimeSpan Before = TimeSpan.FromSeconds(30), After = TimeSpan.FromSeconds(15);

    /// <summary>PCOPT_RECORDINGS_DIR permite testar sem mexer nas gravações verdadeiras.</summary>
    public static string Folder => Environment.GetEnvironmentVariable("PCOPT_RECORDINGS_DIR") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCOptimizer", "recordings");

    readonly List<RecSample> _buffer = new();
    readonly CpuSampler _cpu = new();
    CancellationTokenSource? _cts;
    Process? _smi;
    (int? Load, int? Temp) _gpu;
    IReadOnlyList<string> _top = Array.Empty<string>();
    int? _ping;
    string? _pingTarget, _what;

    public event Action<RecSample>? Sampled;
    public bool Running => _cts != null;

    public void Start(string? what, string? pingTarget)
    {
        if (Running) return;
        _what = what;
        _pingTarget = pingTarget ?? "1.1.1.1";
        _cts = new CancellationTokenSource();
        StartGpu();
        _ = Task.Run(() => LoopAsync(_cts.Token));
        _ = Task.Run(() => TopAppsLoopAsync(_cts.Token));
        _ = Task.Run(() => PingLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try { if (_smi is { HasExited: false }) _smi.Kill(); } catch { }
        _smi?.Dispose();
        _smi = null;
        lock (_buffer) _buffer.Clear();
    }

    public void Dispose() => Stop();

    /// <summary>Espera os 15 s seguintes, guarda a gravação e devolve-a (null se a gravação parou entretanto).</summary>
    public async Task<Recording?> MarkAsync(CancellationToken ct = default)
    {
        var mark = DateTime.Now;
        await Task.Delay(After, ct);
        if (!Running) return null;
        List<RecSample> slice;
        lock (_buffer) slice = _buffer.Where(s => s.Time >= mark - Before && s.Time <= mark + After).ToList();
        var rec = new Recording(mark, _what, _pingTarget, slice);
        Save(rec);
        return rec;
    }

    async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var s = new RecSample(DateTime.Now, _cpu.Sample(), SystemInfo.GetMemory().AvailableBytes / 1073741824.0, DiskActive(),
                _gpu.Load, _gpu.Temp, _ping, _top);
            lock (_buffer)
            {
                _buffer.Add(s);
                _buffer.RemoveAll(x => x.Time < s.Time - TimeSpan.FromSeconds(90));
            }
            Sampled?.Invoke(s);
        }
    }

    /// <summary>O ping corre à parte: se esperasse pela leitura do disco (lenta com o disco cheio de trabalho) dava falsas perdas.</summary>
    async Task PingLoopAsync(CancellationToken ct)
    {
        using var ping = new Ping();
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.Now;
            try
            {
                var r = await ping.SendPingAsync(_pingTarget!, 1500);
                _ping = r.Status == IPStatus.Success ? (int)r.RoundtripTime : null;
            }
            catch { _ping = null; }
            var wait = TimeSpan.FromSeconds(1) - (DateTime.Now - started);
            try { if (wait > TimeSpan.Zero) await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { }
        }
    }

    async Task TopAppsLoopAsync(CancellationToken ct)
    {
        var before = ProcessSampler.Take();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);
                var now = ProcessSampler.Take();
                _top = ProcessSampler.Diff(before, now).Where(a => a.CpuPercent >= 1)
                    .OrderByDescending(a => a.CpuPercent).Take(3).Select(a => $"{a.Name} {a.CpuPercent:N0}%").ToList();
                before = now;
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>O disco mais ocupado (o "_Total" faz a média: um SSD a 90% com um HDD parado dava 45%).</summary>
    static double DiskActive()
    {
        double busy = 0;
        Wmi.Each("SELECT Name, PercentIdleTime FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk WHERE Name<>'_Total'",
            o => busy = Math.Max(busy, 100 - Convert.ToDouble(o["PercentIdleTime"])));
        return Math.Clamp(busy, 0, 100);
    }

    /// <summary>Um só nvidia-smi em modo contínuo (-l 1): lançar um por segundo pesaria mais do que o que mede.</summary>
    void StartGpu()
    {
        try
        {
            _smi = Process.Start(new ProcessStartInfo("nvidia-smi",
                "--query-gpu=name,temperature.gpu,fan.speed,power.draw,utilization.gpu --format=csv,noheader,nounits -l 1")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            if (_smi is null) return;
            _smi.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                var g = Sensors.ParseNvidiaSmi(e.Data).FirstOrDefault();
                if (g != null) _gpu = (g.LoadPercent, g.TempC);
            };
            _smi.BeginOutputReadLine();
        }
        catch { _smi = null; } // sem NVIDIA: a GPU fica de fora
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    static void Save(Recording rec)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, $"{rec.Mark:yyyyMMdd-HHmmss}.json"), JsonSerializer.Serialize(rec, Json));
            foreach (var old in new DirectoryInfo(Folder).GetFiles("*.json").OrderByDescending(f => f.Name).Skip(20)) old.Delete();
        }
        catch { }
    }

    public static List<Recording> LoadAll()
    {
        var list = new List<Recording>();
        if (!Directory.Exists(Folder)) return list;
        foreach (var f in new DirectoryInfo(Folder).GetFiles("*.json").OrderByDescending(f => f.Name))
            try { if (JsonSerializer.Deserialize<Recording>(File.ReadAllText(f.FullName)) is { } r) list.Add(r); }
            catch { }
        return list;
    }
}
