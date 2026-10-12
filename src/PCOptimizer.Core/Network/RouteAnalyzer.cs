using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Network;

/// <summary>Onde fica cada salto do caminho até ao servidor.</summary>
public enum RouteSegment { Home, Provider, Internet, Server }

public sealed record HopStats(int Ttl, IPAddress? Address, int Sent, int Received, double Avg, double Best, double Worst, double Jitter)
{
    public double LossPercent => Sent == 0 ? 100 : 100.0 * (Sent - Received) / Sent;
    public bool Responds => Address != null && Received > 0;
    public RouteSegment Segment { get; init; }
    public string? HostName { get; init; }
}

public enum LagSource { None, Home, Provider, Internet, Server }

public sealed record RouteVerdict(LagSource Source, string Title, string Detail, int? FromTtl = null);

public sealed record RouteReport(IPAddress Target, bool ReachedTarget, IReadOnlyList<HopStats> Hops, RouteVerdict Verdict);

/// <summary>
/// "Onde está o lag": descobre os saltos até ao servidor (traceroute em paralelo), mede cada um
/// várias vezes e diz em que troço começa o problema — a tua casa, o operador, a internet ou o servidor.
/// </summary>
public static class RouteAnalyzer
{
    public static async Task<RouteReport> RunAsync(IPAddress target, int rounds = 25, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(Loc.T("A descobrir o caminho até ao servidor…"));
        var (path, reached) = await TraceAsync(target, ct: ct);

        var samples = path.Select(_ => new List<double>()).ToArray();
        var sent = new int[path.Count];
        for (var r = 0; r < rounds; r++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(Loc.T($"A medir cada salto… {r + 1}/{rounds}"));
            // como o MTR: pacotes para o servidor com TTL limitado, que "morrem" em cada salto.
            // (Pings diretos aos routers do operador costumam ser bloqueados; estes não.)
            await Task.WhenAll(path.Select(async (addr, i) =>
            {
                if (addr is null) return;
                sent[i]++;
                if (await ProbeAsync(target, i + 1) is double ms) lock (samples[i]) samples[i].Add(ms);
            }));
            await Task.Delay(250, ct);
        }

        var hops = path.Select((addr, i) => Stats(i + 1, addr, sent[i], samples[i])).ToList();
        hops = Classify(hops);
        progress?.Report(Loc.T("A identificar os saltos…"));
        hops = (await Task.WhenAll(hops.Select(async h => h with { HostName = await ReverseAsync(h.Address) }))).ToList();
        return new RouteReport(target, reached, hops, Diagnose(hops));
    }

    /// <summary>Envia todos os TTL de uma vez (bem mais rápido que o tracert, que vai um a um).</summary>
    public static async Task<(List<IPAddress?> Path, bool Reached)> TraceAsync(IPAddress target, int maxHops = 30, CancellationToken ct = default)
    {
        var replies = await Task.WhenAll(Enumerable.Range(1, maxHops).Select(async ttl =>
        {
            using var ping = new Ping();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var r = await ping.SendPingAsync(target, 1500, new byte[32], new PingOptions(ttl, true));
                    if (r.Status is IPStatus.TtlExpired or IPStatus.Success) return (r.Address, r.Status == IPStatus.Success);
                }
                catch { }
            }
            return ((IPAddress?)null, false);
        }));
        ct.ThrowIfCancellationRequested();

        var path = new List<IPAddress?>();
        var reached = false;
        foreach (var (addr, success) in replies)
        {
            path.Add(addr);
            if (success) { reached = true; break; }
        }
        if (!reached) // corta os saltos mudos do fim (o servidor não responde a pings)
            while (path.Count > 0 && path[^1] is null) path.RemoveAt(path.Count - 1);
        return (path, reached);
    }

    /// <summary>Tempo até ao salto <paramref name="ttl"/>. O Windows não dá o tempo nas respostas "TTL expirado", por isso é cronometrado.</summary>
    static async Task<double?> ProbeAsync(IPAddress target, int ttl)
    {
        using var ping = new Ping();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var r = await ping.SendPingAsync(target, 1000, new byte[32], new PingOptions(ttl, true));
            sw.Stop();
            return r.Status switch
            {
                IPStatus.Success => r.RoundtripTime > 0 ? r.RoundtripTime : sw.Elapsed.TotalMilliseconds,
                IPStatus.TtlExpired => sw.Elapsed.TotalMilliseconds,
                _ => null,
            };
        }
        catch { return null; }
    }

    static HopStats Stats(int ttl, IPAddress? addr, int sent, List<double> s)
    {
        if (s.Count == 0) return new HopStats(ttl, addr, sent, 0, 0, 0, 0, 0);
        double jitter = 0;
        for (var i = 1; i < s.Count; i++) jitter += Math.Abs(s[i] - s[i - 1]);
        if (s.Count > 1) jitter /= s.Count - 1;
        return new HopStats(ttl, addr, sent, s.Count, s.Average(), s.Min(), s.Max(), jitter);
    }

    /// <summary>
    /// Casa = o teu router (1.º salto) e um eventual 2.º router de casa (192.168.x / 172.16–31.x);
    /// operador = os 3 saltos seguintes (os 10.x e 100.64.x a seguir ao router são a rede interna
    /// do operador); servidor = os 2 últimos; o resto é a internet pelo meio.
    /// </summary>
    public static List<HopStats> Classify(List<HopStats> hops)
    {
        var homeEnd = hops.Count > 0 ? 1 : 0;
        while (homeEnd < hops.Count && hops[homeEnd].Address is IPAddress a && IsHomeAddress(a)) homeEnd++;
        var serverStart = Math.Max(homeEnd + 3, hops.Count - 2);
        return hops.Select((h, i) => h with
        {
            Segment = i < homeEnd ? RouteSegment.Home
                : i < Math.Min(homeEnd + 3, serverStart) ? RouteSegment.Provider
                : i >= serverStart ? RouteSegment.Server
                : RouteSegment.Internet,
        }).ToList();
    }

    static bool IsHomeAddress(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return a.AddressFamily == AddressFamily.InterNetwork && ((b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31));
    }

    /// <summary>
    /// A regra certa para ler um traceroute: perda ou atraso num salto só contam se continuarem
    /// até ao fim. Se um salto do meio "perde" pacotes mas os seguintes não, é só esse router a
    /// ignorar pings (é normal) — não é lag.
    /// </summary>
    public static RouteVerdict Diagnose(IReadOnlyList<HopStats> hops)
    {
        var resp = hops.Where(h => h.Responds).ToList();
        if (resp.Count == 0)
            return new RouteVerdict(LagSource.None, Loc.T("Sem dados"), Loc.T("Nenhum salto respondeu. A tua ligação pode estar em baixo, ou a rede bloqueia pings."));

        // perda que começa num salto e se mantém até ao último que responde
        for (var i = 0; i < resp.Count; i++)
            if (IsRealLoss(resp, i))
                return Verdict(resp[i], Loc.T($"{resp[i].LossPercent:N0}% dos pacotes perdem-se a partir do salto {resp[i].Ttl}"), loss: true);

        // atraso que salta e não volta a baixar — só na casa e no operador: mais à frente,
        // saltos de dezenas de ms são normalmente distância (outro país), não problema
        for (var i = 1; i < resp.Count; i++)
        {
            var jump = resp[i].Avg - resp[i - 1].Avg;
            if (jump < 35) continue;
            var persists = resp.Skip(i + 1).All(h => h.Avg >= resp[i].Avg - 15);
            if (persists && resp[i].Segment is RouteSegment.Home or RouteSegment.Provider)
                return Verdict(resp[i], Loc.T($"o tempo de resposta sobe {jump:N0} ms no salto {resp[i].Ttl}"), loss: false);
        }

        // router/Wi-Fi lento ou instável (o 1.º salto devia responder em 1–3 ms)
        var first = resp[0];
        if (first.Segment == RouteSegment.Home && (first.Avg > 10 || first.Jitter > 6 || first.Worst > 60))
            return new RouteVerdict(LagSource.Home, Loc.T("Lag na tua rede de casa"),
                Loc.T($"O teu router responde em {first.Avg:N0} ms (picos de {first.Worst:N0} ms); devia ser 1–3 ms. ") +
                Loc.T("É típico do Wi-Fi: usa cabo, aproxima-te do router ou muda o canal do Wi-Fi. Também ajuda reiniciar o router."), first.Ttl);

        return new RouteVerdict(LagSource.None, Loc.T("Caminho limpo ✓"),
            Loc.T("Não há perda nem saltos de atraso no caminho. Se sentes lag, é provável que seja do servidor do jogo em si ou da distância até ele."));
    }

    /// <summary>
    /// Perda a sério = continua (pelo menos metade) em todos os saltos seguintes que respondem.
    /// Se não continua, é só esse router a ignorar alguns testes — normal, não afeta o jogo.
    /// </summary>
    public static bool IsRealLoss(IReadOnlyList<HopStats> hops, int index)
    {
        var h = hops[index];
        if (!h.Responds || h.LossPercent < 4) return false;
        return hops.Skip(index + 1).Where(x => x.Responds).All(x => x.LossPercent >= h.LossPercent * 0.5);
    }

    static RouteVerdict Verdict(HopStats hop, string what, bool loss) => hop.Segment switch
    {
        RouteSegment.Home => new(LagSource.Home, Loc.T("O problema está na tua rede de casa"),
            Loc.T($"Na tua casa: {what}. {(loss ? Loc.T("Perda logo no router") : Loc.T("Atraso no router"))} costuma ser Wi-Fi fraco, cabo estragado ou router sobrecarregado. ") +
            Loc.T("Experimenta com cabo e reinicia o router."), hop.Ttl),
        RouteSegment.Provider => new(LagSource.Provider, Loc.T("O problema está no teu operador"),
            Loc.T($"Na rede do teu operador: {what}. Não é do teu PC nem da tua casa. Liga para a linha de apoio e mostra este resultado ") +
            Loc.T("(podes exportá-lo). Se acontecer à mesma hora todos os dias, é congestionamento da rede deles."), hop.Ttl),
        RouteSegment.Internet => new(LagSource.Internet, Loc.T("O problema está na internet pelo caminho"),
            Loc.T($"Entre o teu operador e o servidor: {what}. Não depende de ti nem do operador diretamente. Costuma passar sozinho; ") +
            Loc.T("uma VPN para outra rota pode ajudar enquanto dura."), hop.Ttl),
        _ => new(LagSource.Server, Loc.T("O problema está no servidor do jogo"),
            Loc.T($"Perto do servidor: {what}. É a rede do jogo. Experimenta outro servidor/região no jogo ou espera que resolvam."), hop.Ttl),
    };

    static async Task<string?> ReverseAsync(IPAddress? a)
    {
        if (a is null) return null;
        try
        {
            var t = Dns.GetHostEntryAsync(a);
            return await Task.WhenAny(t, Task.Delay(1500)) == t && t.Result.HostName != a.ToString() ? t.Result.HostName : null;
        }
        catch { return null; }
    }
}
