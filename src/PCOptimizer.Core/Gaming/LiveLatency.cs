using System.Net;
using System.Net.NetworkInformation;
using PCOptimizer.Core.Network;

namespace PCOptimizer.Core.Gaming;

public enum LinkQuality { Good, Fair, Bad }

/// <summary>Estado da ligação nos últimos ~30 segundos.</summary>
public sealed record LiveSample(double? PingMs, double AvgMs, double LossPercent, double JitterMs, bool NearServer)
{
    public LinkQuality Quality => Classify(AvgMs, LossPercent, JitterMs);

    public static LinkQuality Classify(double avg, double loss, double jitter) =>
        avg < 60 && loss < 1 && jitter < 8 ? LinkQuality.Good
        : avg < 120 && loss < 3 && jitter < 20 ? LinkQuality.Fair
        : LinkQuality.Bad;
}

/// <summary>
/// Mede a ligação ao servidor do jogo uma vez por segundo, enquanto se joga.
/// Muitos servidores de jogos não respondem a pings: nesse caso mede o último salto
/// do caminho que responde (fica junto ao servidor, por isso o valor é quase o mesmo).
/// </summary>
public sealed class LiveLatency : IDisposable
{
    readonly IPAddress _server;
    readonly Queue<double?> _window = new();
    readonly CancellationTokenSource _cts = new();
    const int WindowSize = 30;

    public LiveLatency(IPAddress server) => _server = server;

    public event Action<LiveSample>? Updated;

    /// <summary>O endereço que está a ser medido (o servidor, ou o salto junto a ele).</summary>
    public IPAddress? Target { get; private set; }
    public bool NearServer { get; private set; }

    public void Start() => _ = RunAsync(_cts.Token);

    async Task RunAsync(CancellationToken ct)
    {
        try
        {
            Target = await PickTargetAsync(ct);
            if (Target is null) return;
            using var ping = new Ping();
            while (!ct.IsCancellationRequested)
            {
                double? rtt = null;
                try
                {
                    var r = await ping.SendPingAsync(Target, 1000);
                    if (r.Status == IPStatus.Success) rtt = r.RoundtripTime;
                }
                catch { }
                Updated?.Invoke(Add(rtt));
                await Task.Delay(1000, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    async Task<IPAddress?> PickTargetAsync(CancellationToken ct)
    {
        using var ping = new Ping();
        for (var i = 0; i < 3; i++)
            try
            {
                if ((await ping.SendPingAsync(_server, 1000)).Status == IPStatus.Success) return _server;
            }
            catch { }

        var (path, _) = await RouteAnalyzer.TraceAsync(_server, ct: ct);
        NearServer = true;
        return path.LastOrDefault(a => a != null);
    }

    LiveSample Add(double? rtt)
    {
        lock (_window)
        {
            _window.Enqueue(rtt);
            while (_window.Count > WindowSize) _window.Dequeue();
            return Summarize(_window.ToList(), NearServer);
        }
    }

    /// <summary>Média, perda e jitter de uma janela de medições (null = sem resposta).</summary>
    public static LiveSample Summarize(IReadOnlyList<double?> window, bool nearServer)
    {
        var ok = window.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var loss = window.Count == 0 ? 0 : 100.0 * (window.Count - ok.Count) / window.Count;
        double jitter = 0;
        for (var i = 1; i < ok.Count; i++) jitter += Math.Abs(ok[i] - ok[i - 1]);
        if (ok.Count > 1) jitter /= ok.Count - 1;
        return new LiveSample(window.Count > 0 ? window[^1] : null, ok.Count > 0 ? ok.Average() : 0, loss, jitter, nearServer);
    }

    public void Dispose() => _cts.Cancel();
}
