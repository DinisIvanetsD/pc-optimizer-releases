using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Network;

public sealed record ActiveConnection(NetworkInterface Nic, IPAddress Gateway, bool IsWifi, int IfIndex)
{
    public string TypeText => IsWifi ? "Wi-Fi" : "Cabo (Ethernet)";
}

public sealed record WifiStatus(int? SignalPercent, int? Channel)
{
    public string Band => Channel is null ? "?" : Channel <= 14 ? "2,4 GHz" : "5/6 GHz";
}

public sealed record NetworkReport(ActiveConnection? Connection, WifiStatus? Wifi, PingStats? Router, PingStats? Internet, PingStats? Game, List<Finding> Findings);

public sealed record GameServer(string Name, string Host);

public static class GameServers
{
    public static string Custom => Loc.T("Personalizado…");

    /// <summary>
    /// Muitos jogos (Fortnite, PUBG, Rocket League, etc.) correm em servidores AWS. Medir a região
    /// onde jogas dá uma ideia muito boa do ping que vais ter.
    /// </summary>
    public static IReadOnlyList<GameServer> Presets { get; } = new[]
    {
        new GameServer("Europa – Madrid (AWS)", "dynamodb.eu-south-2.amazonaws.com"),
        new GameServer("Europa – Paris (AWS)", "dynamodb.eu-west-3.amazonaws.com"),
        new GameServer("Europa – Frankfurt (AWS)", "dynamodb.eu-central-1.amazonaws.com"),
        new GameServer("Europa – Londres (AWS)", "dynamodb.eu-west-2.amazonaws.com"),
        new GameServer("Europa – Irlanda (AWS)", "dynamodb.eu-west-1.amazonaws.com"),
        new GameServer("Europa – Estocolmo (AWS)", "dynamodb.eu-north-1.amazonaws.com"),
        new GameServer("EUA – Este (AWS)", "dynamodb.us-east-1.amazonaws.com"),
        new GameServer(Loc.T("Brasil – São Paulo (AWS)"), "dynamodb.sa-east-1.amazonaws.com"),
        new GameServer(Custom, ""),
    };
}

public static class NetworkDiagnostics
{
    const string Cat = "Rede";

    public static ActiveConnection? GetActiveConnection()
    {
        var candidates = new List<ActiveConnection>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = nic.GetIPProperties();
            var gw = props.GatewayAddresses.Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
            if (gw is null) continue;
            var index = 0;
            try { index = props.GetIPv4Properties()?.Index ?? 0; } catch { }
            candidates.Add(new ActiveConnection(nic, gw, nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211, index));
        }
        // preferir placas físicas (Ethernet/Wi-Fi) a adaptadores virtuais e VPNs
        return candidates
            .OrderBy(c => c.Nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 ? 0 : 1)
            .ThenBy(c => c.Nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();
    }

    public static async Task<WifiStatus?> GetWifiAsync()
    {
        var r = await Shell.RunAsync("netsh", "wlan show interfaces");
        if (!r.Ok) return null;
        int? signal = null, channel = null;
        foreach (var line in r.StdOut.Split('\n'))
        {
            var s = Regex.Match(line, @"^\s*(Signal|Sinal)\s*:\s*(\d+)\s*%", RegexOptions.IgnoreCase);
            if (s.Success) signal = int.Parse(s.Groups[2].Value);
            var c = Regex.Match(line, @"^\s*(Channel|Canal)\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            if (c.Success) channel = int.Parse(c.Groups[2].Value);
        }
        return signal is null && channel is null ? null : new WifiStatus(signal, channel);
    }

    public static async Task<NetworkReport> RunAsync(string? gameHost, string gameLabel, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var findings = new List<Finding>();
        var conn = GetActiveConnection();
        if (conn is null)
        {
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Critical,
                Title = Loc.T("Sem ligação à rede"),
                Description = Loc.T("Não encontrei nenhuma placa de rede ligada a um router. Verifica o cabo ou o Wi-Fi."),
            });
            return new NetworkReport(null, null, null, null, null, findings);
        }

        var wifi = conn.IsWifi ? await GetWifiAsync() : null;

        progress?.Report(Loc.T("A medir o ping ao router, à internet e ao servidor (≈10 s)…"));
        var routerTask = PingTester.PingAsync("Router", conn.Gateway.ToString(), 40, 100, tcpFallback: false, ct: ct);
        var internetTask = PingTester.PingAsync("Internet", "1.1.1.1", 40, 100, ct: ct);
        var gameTask = string.IsNullOrWhiteSpace(gameHost)
            ? Task.FromResult<PingStats?>(null)
            : PingTester.PingAsync(gameLabel, gameHost, 40, 100, ct: ct).ContinueWith(t => (PingStats?)t.Result, ct);
        await Task.WhenAll(routerTask, internetTask, (Task)gameTask);
        var router = routerTask.Result;
        var internet = internetTask.Result;
        var game = gameTask.Result;

        if (conn.IsWifi)
        {
            if (wifi?.SignalPercent is int sig && sig < 50)
            {
                findings.Add(new Finding
                {
                    Category = Cat, Severity = Severity.Warning,
                    Title = Loc.T($"Sinal Wi-Fi fraco ({sig}%)"),
                    Description = Loc.T("Com sinal fraco há perda de pacotes e picos de ping. Aproxima-te do router, usa um sistema mesh ou, melhor ainda, um cabo de rede."),
                });
            }
            if (wifi?.Channel is <= 14)
            {
                findings.Add(new Finding
                {
                    Category = Cat, Severity = Severity.Info,
                    Title = Loc.T("Estás na banda de 2,4 GHz"),
                    Description = Loc.T("É mais lenta e está mais congestionada (redes dos vizinhos, micro-ondas, Bluetooth). Se o router tiver 5 GHz, liga-te a essa rede."),
                });
            }
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Info,
                Title = Loc.T("Estás ligado por Wi-Fi"),
                Description = Loc.T("Para jogar, o cabo de rede é sempre melhor: ping mais baixo e quase sem jitter."),
            });
        }

        var localBad = false;
        if (router.Reachable)
        {
            if (router.LossPercent >= 2 || router.Jitter >= 10 || router.Avg >= 20)
            {
                localBad = true;
                findings.Add(new Finding
                {
                    Category = Cat, Severity = router.LossPercent >= 5 ? Severity.Critical : Severity.Warning,
                    Title = Loc.T("O problema está na tua rede local"),
                    Description = Loc.T($"Só até ao router já tens {router.Avg:N0} ms, {router.Jitter:N1} ms de jitter e {router.LossPercent:N0}% de perda. ") +
                                  Loc.T("Numa rede boa isto é 1–3 ms e 0%. A culpa é do Wi-Fi, do cabo ou do router, não do ISP nem do jogo."),
                });
            }
        }
        else
        {
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Info,
                Title = Loc.T("O router não responde a ping"),
                Description = Loc.T("Alguns routers bloqueiam pings, e isso é normal. Não foi possível testar a rede local à parte."),
            });
        }

        var ispBad = false;
        if (!internet.Reachable)
        {
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Critical,
                Title = Loc.T("Sem acesso à internet"),
                Description = Loc.T("O router responde mas a internet não. Reinicia o router; se continuar, o problema é do operador."),
            });
        }
        else if (!localBad && (internet.LossPercent >= 2 || internet.Jitter >= 15 || internet.Avg >= 60))
        {
            ispBad = true;
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Warning,
                Title = Loc.T("O problema está na ligação do teu operador (ISP)"),
                Description = Loc.T($"A rede local está bem, mas até à internet tens {internet.Avg:N0} ms, jitter {internet.Jitter:N1} ms e {internet.LossPercent:N0}% de perda. ") +
                              Loc.T("Reinicia o router; se continuar, contacta o operador com estes números."),
            });
        }

        if (game is not null)
        {
            if (!game.Reachable)
            {
                findings.Add(new Finding
                {
                    Category = Cat, Severity = Severity.Warning,
                    Title = Loc.T($"Não foi possível chegar a {gameLabel}"),
                    Description = Loc.T("O servidor pode estar a bloquear os testes, ou há um problema na rota até lá."),
                });
            }
            else if (!localBad && !ispBad)
            {
                if (game.LossPercent >= 2)
                {
                    findings.Add(new Finding
                    {
                        Category = Cat, Severity = Severity.Warning,
                        Title = Loc.T("Perda de pacotes até ao servidor do jogo"),
                        Description = Loc.T($"A tua rede e o operador estão bem, mas perde-se {game.LossPercent:N0}% do tráfego a caminho do servidor. ") +
                                      Loc.T("É exatamente aqui que as rotas otimizadas (estilo ExitLag) ajudam."),
                    });
                }
                var extra = internet.Reachable ? game.Avg - internet.Avg : 0;
                if (extra >= 40)
                {
                    findings.Add(new Finding
                    {
                        Category = Cat, Severity = Severity.Info,
                        Title = Loc.T($"O servidor está a {game.Avg:N0} ms"),
                        Description = Loc.T($"São mais {extra:N0} ms do que até à internet. Ou o servidor está longe, ou a rota do teu operador até lá é má. ") +
                                      Loc.T("Experimenta outra região no jogo. As rotas otimizadas (fase 2) podem baixar este valor."),
                    });
                }
            }
        }

        if (!findings.Any(f => f.Severity is Severity.Critical or Severity.Warning))
        {
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Ok,
                Title = Loc.T("A tua ligação está boa"),
                Description = Loc.T("Router, internet e servidor estão estáveis. Se tens lag no jogo, o problema provavelmente está no servidor do jogo ") +
                              Loc.T("ou no próprio PC (FPS baixos parecem lag). Vê o Diagnóstico e o Monitor."),
            });
        }

        progress?.Report(Loc.T("Teste concluído."));
        return new NetworkReport(conn, wifi, router, internet, game, findings.OrderBy(f => f.Severity).ToList());
    }
}
