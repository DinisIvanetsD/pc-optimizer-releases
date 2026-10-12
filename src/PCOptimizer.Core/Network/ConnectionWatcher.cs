using System.Diagnostics.Eventing.Reader;
using System.Net.NetworkInformation;
using System.Text.Json;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Network;

public enum DropCause { Cable, LocalNetwork, Internet, PortExhaustion, Unknown }

public sealed record ConnectionDrop(DateTime Start, TimeSpan Duration, DropCause Cause, string Detail)
{
    public string CauseText => Cause switch
    {
        DropCause.Cable => Loc.T("Cabo / placa de rede"),
        DropCause.LocalNetwork => Loc.T("Rede local (router ou Wi-Fi)"),
        DropCause.Internet => "Internet (operador)",
        DropCause.PortExhaustion => Loc.T("Portas de rede esgotadas"),
        _ => "Desconhecida",
    };

    public string When => Start.ToString("dd/MM HH:mm:ss");
    public string DurationText => Duration.TotalSeconds < 60 ? $"{Duration.TotalSeconds:N0} s" : $"{Duration.TotalMinutes:N1} min";
}

/// <summary>
/// Vigia a ligação em segundo plano: faz ping ao router e à internet a cada segundo. Quando a ligação
/// cai, regista quando, quanto tempo durou e porquê (cabo desligado, router, operador, ou portas de
/// rede esgotadas por alguma app). Também apanha em tempo real os avisos "Tcpip 4266/4231" do Windows
/// e diz qual a app que tinha mais portas abertas nesse momento.
/// </summary>
public sealed class ConnectionWatcher : IDisposable
{
    static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCOptimizer", "network-drops.json");

    readonly List<ConnectionDrop> _drops = new();
    CancellationTokenSource? _cts;
    EventLogWatcher? _eventWatcher;
    PortSnapshot? _lastPorts;
    DateTime _lastPortCheck;

    public event Action<ConnectionDrop>? DropDetected;
    public event Action<string>? StatusChanged;
    public event Action<PortSnapshot>? PortsUpdated;

    public IReadOnlyList<ConnectionDrop> Drops
    {
        get { lock (_drops) return _drops.ToList(); }
    }

    public bool IsRunning => _cts != null;

    public ConnectionWatcher() => Load();

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(() => LoopAsync(ct), ct);
        StartEventWatcher();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        if (_eventWatcher != null)
        {
            _eventWatcher.Enabled = false;
            _eventWatcher.Dispose();
            _eventWatcher = null;
        }
    }

    public void Dispose() => Stop();

    public void ClearHistory()
    {
        lock (_drops) _drops.Clear();
        Save();
    }

    async Task LoopAsync(CancellationToken ct)
    {
        using var ping = new Ping();
        DateTime? downSince = null;
        var downCause = DropCause.Unknown;
        var downDetail = "";
        var fails = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var conn = NetworkDiagnostics.GetActiveConnection();
                var linkUp = conn != null;
                var routerOk = linkUp && await Reachable(ping, conn!.Gateway.ToString());
                var internetOk = linkUp && (await Reachable(ping, "1.1.1.1") || await Reachable(ping, "8.8.8.8"));

                // a cada 5 s (ou quando falha) contar as portas por app
                if (DateTime.UtcNow - _lastPortCheck > TimeSpan.FromSeconds(5) || !internetOk)
                {
                    _lastPorts = PortUsage.Take();
                    _lastPortCheck = DateTime.UtcNow;
                    PortsUpdated?.Invoke(_lastPorts);
                }

                if (!internetOk)
                {
                    fails++;
                    if (fails == 2) // 2 falhas seguidas = quebra (evita falsos alarmes de um ping perdido)
                    {
                        downSince = DateTime.Now.AddSeconds(-1);
                        (downCause, downDetail) = Classify(linkUp, routerOk, _lastPorts);
                        StatusChanged?.Invoke(Loc.T($"⚠ Sem internet desde {downSince:HH:mm:ss} — {downDetail}"));
                    }
                }
                else
                {
                    if (downSince is DateTime start)
                    {
                        var drop = new ConnectionDrop(start, DateTime.Now - start, downCause, downDetail);
                        Add(drop);
                        DropDetected?.Invoke(drop);
                    }
                    downSince = null;
                    fails = 0;
                    StatusChanged?.Invoke(Loc.T($"✓ Ligação OK · última verificação {DateTime.Now:HH:mm:ss}"));
                }
            }
            catch
            {
                // nunca deixar o vigia morrer
            }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    static (DropCause, string) Classify(bool linkUp, bool routerOk, PortSnapshot? ports)
    {
        if (!linkUp) return (DropCause.Cable, Loc.T("a placa de rede ficou sem ligação (cabo, porta do router ou driver)"));
        if (ports != null && (ports.TotalUdp > PortSnapshot.DefaultDynamicPorts * 0.8 || ports.TotalTcp > PortSnapshot.DefaultDynamicPorts * 0.8))
        {
            var top = ports.Top.FirstOrDefault();
            return (DropCause.PortExhaustion, top is null ? Loc.T("portas de rede esgotadas")
                : Loc.T($"portas esgotadas — {top.Name} tinha {top.Total:N0} abertas"));
        }
        if (!routerOk) return (DropCause.LocalNetwork, Loc.T("o router também não respondia"));
        return (DropCause.Internet, Loc.T("o router respondia, mas a internet não"));
    }

    static async Task<bool> Reachable(Ping ping, string host)
    {
        try
        {
            var r = await ping.SendPingAsync(host, 800);
            return r.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Os avisos do Windows "todas as portas estão em uso" (Tcpip 4266 = UDP, 4231 = TCP).</summary>
    void StartEventWatcher()
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Tcpip'] and (EventID=4266 or EventID=4231)]]");
            _eventWatcher = new EventLogWatcher(query);
            _eventWatcher.EventRecordWritten += (_, e) =>
            {
                using var rec = e.EventRecord;
                if (rec is null) return;
                var ports = PortUsage.Take();
                var proto = rec.Id == 4266 ? "UDP" : "TCP";
                var top = ports.Top.FirstOrDefault();
                var detail = top is null
                    ? Loc.T($"o Windows ficou sem portas {proto}")
                    : Loc.T($"o Windows ficou sem portas {proto}; quem tinha mais abertas: {top.Name} ({top.Tcp:N0} TCP, {top.Udp:N0} UDP)");
                var drop = new ConnectionDrop(rec.TimeCreated ?? DateTime.Now, TimeSpan.Zero, DropCause.PortExhaustion, detail);
                Add(drop);
                DropDetected?.Invoke(drop);
            };
            _eventWatcher.Enabled = true;
        }
        catch
        {
            // sem acesso ao registo de eventos: continua só com os pings
        }
    }

    /// <summary>Esgotamentos de portas registados pelo Windows nos últimos N dias (antes de a app estar a vigiar).</summary>
    public static List<DateTime> PastPortExhaustion(int days = 30)
    {
        var list = new List<DateTime>();
        try
        {
            var ms = (long)days * 24 * 3600 * 1000;
            var q = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Tcpip'] and (EventID=4266 or EventID=4231) and TimeCreated[timediff(@SystemTime) <= {ms}]]]")
            { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            for (var r = reader.ReadEvent(); r != null; r = reader.ReadEvent())
            {
                using (r) if (r.TimeCreated is DateTime t) list.Add(t);
            }
        }
        catch { }
        return list;
    }

    void Add(ConnectionDrop d)
    {
        lock (_drops)
        {
            _drops.Insert(0, d);
            if (_drops.Count > 500) _drops.RemoveAt(_drops.Count - 1);
        }
        Save();
    }

    void Load()
    {
        try
        {
            if (File.Exists(LogPath))
                _drops.AddRange(JsonSerializer.Deserialize<List<ConnectionDrop>>(File.ReadAllText(LogPath)) ?? new());
        }
        catch { }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            lock (_drops) File.WriteAllText(LogPath, JsonSerializer.Serialize(_drops));
        }
        catch { }
    }
}
