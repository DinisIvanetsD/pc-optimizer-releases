using System.Net;
using PCOptimizer.Core.Gaming;
using PCOptimizer.Core.Network;
using Xunit;

namespace PCOptimizer.Tests;

public class GamingTests
{
    // ---- deteção do jogo

    [Fact]
    public void Deteta_o_jogo_pelo_processo_e_ignora_o_launcher()
    {
        var procs = new[] { (10, "RiotClientServices", (string?)null), (20, "VALORANT-Win64-Shipping", null), (30, "explorer", null) };
        var g = GameCatalog.Find(procs, GameCatalog.Known);
        Assert.NotNull(g);
        Assert.Equal("VALORANT", g!.Game.Name);
        Assert.Equal(20, g.Pid);
    }

    [Fact]
    public void Jogo_acrescentado_pelo_utilizador_tambem_conta()
    {
        var games = GameCatalog.Known.Append(new GameInfo("O meu jogo", "MeuJogo.exe"));
        Assert.Equal("O meu jogo", GameCatalog.Find(new[] { (5, "meujogo", (string?)null) }, games)!.Game.Name);
        Assert.Null(GameCatalog.Find(new[] { (5, "notepad", (string?)null) }, games));
    }

    [Fact]
    public void Com_varios_jogos_conta_o_de_primeiro_plano_ou_o_mais_recente()
    {
        var procs = new[] { (1, "RobloxPlayerBeta", (string?)null), (2, "cs2", null) };
        var running = GameCatalog.FindAll(procs, GameCatalog.Known);
        var started = new Dictionary<int, DateTime> { [1] = new(2026, 1, 1, 10, 0, 0), [2] = new(2026, 1, 1, 11, 0, 0) };
        Assert.Equal("Counter-Strike 2", GameCatalog.Choose(running, foregroundPid: 999, pid => started[pid])!.Game.Name); // o mais recente
        Assert.Equal("Roblox", GameCatalog.Choose(running, foregroundPid: 1, pid => started[pid])!.Game.Name);             // o que está à frente
    }

    [Theory]
    [InlineData("eldenring", true, true)]
    [InlineData("chrome", true, false)]         // vídeo em ecrã inteiro no browser
    [InlineData("vlc", true, false)]
    [InlineData("explorer", true, false)]
    [InlineData("PCOptimizer", true, false)]
    [InlineData("mstsc", true, false)]          // ambiente de trabalho remoto em ecrã inteiro
    [InlineData("Code", true, false)]
    [InlineData("ms-teams", true, false)]
    [InlineData("NVIDIA Overlay", true, false)]
    [InlineData("eldenring", false, false)]     // em janela: não conta
    public void Qualquer_jogo_em_ecra_inteiro(string process, bool covers, bool expected) =>
        Assert.Equal(expected, GameCatalog.IsCandidate(process, covers));

    [Fact]
    public void Alt_Tab_nao_acaba_a_sessao_de_um_jogo_detetado()
    {
        var detected = new RunningGame(new GameInfo("eldenring", "eldenring.exe"), 7, null, Detected: true);
        var known = new RunningGame(GameCatalog.Known[0], 9, null);
        Assert.Same(detected, GameCatalog.Pick(null, foreground: null, current: detected, alive: _ => true, fullscreen: _ => true));
        Assert.Null(GameCatalog.Pick(null, null, detected, alive: _ => false, _ => true));     // o jogo fechou
        Assert.Same(known, GameCatalog.Pick(known, null, detected, _ => true, _ => true));      // um jogo conhecido ganha
        Assert.Same(detected, GameCatalog.Pick(null, detected, null, _ => true, _ => true));    // novo em primeiro plano
    }

    [Fact]
    public void Um_jogo_novo_em_ecra_inteiro_substitui_o_detetado_antes()
    {
        var detected = new RunningGame(new GameInfo("eldenring", "eldenring.exe"), 7, null, Detected: true);
        var other = new RunningGame(new GameInfo("hades", "hades.exe"), 8, null, Detected: true);
        Assert.Same(other, GameCatalog.Pick(null, other, detected, _ => true, _ => true));
    }

    [Fact]
    public void Detetado_que_deixou_de_ocupar_o_ecra_e_nao_esta_minimizado_sai()
    {
        // falso positivo (ex.: um vídeo em ecrã inteiro numa app desconhecida) que voltou a ser uma janela normal
        var detected = new RunningGame(new GameInfo("someapp", "someapp.exe"), 7, null, Detected: true);
        Assert.Null(GameCatalog.Pick(null, null, detected, alive: _ => true, fullscreen: _ => false));
    }

    [Theory]
    [InlineData(0x90000000, true)]    // WS_POPUP | WS_VISIBLE: ecrã inteiro exclusivo ou sem bordas
    [InlineData(0x01CF0000, false)]   // WS_OVERLAPPEDWINDOW | WS_MAXIMIZE: janela normal maximizada
    [InlineData(0x00800000, true)]    // só WS_BORDER, sem WS_DLGFRAME: não é barra de título
    public void So_janelas_sem_barra_de_titulo_podem_ser_jogos(long style, bool expected) =>
        Assert.Equal(expected, GameCatalog.IsBorderless(style));

    // ---- servidor do jogo

    [Theory]
    [InlineData("192.168.1.1", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("100.70.1.1", false)]   // CGNAT
    [InlineData("239.255.255.250", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("185.40.64.65", true)]
    [InlineData("2a00:1450:4003::1", true)]
    [InlineData("fe80::1", false)]
    public void So_enderecos_da_internet_podem_ser_o_servidor(string ip, bool expected) =>
        Assert.Equal(expected, ServerPicker.IsPublic(IPAddress.Parse(ip)));

    [Fact]
    public void Servidor_e_o_UDP_com_mais_pacotes()
    {
        var traffic = new[]
        {
            new RemoteTraffic(IPAddress.Parse("104.16.1.1"), 443, false, 40, 9000),   // loja/lobby por TCP
            new RemoteTraffic(IPAddress.Parse("162.249.72.10"), 7100, true, 640, 80_000), // o jogo: 64 pacotes/s
            new RemoteTraffic(IPAddress.Parse("162.249.72.99"), 7200, true, 30, 2000),  // voz
            new RemoteTraffic(IPAddress.Parse("192.168.1.20"), 3074, true, 5000, 1),    // rede local: ignorado
        };
        var s = ServerPicker.Pick(traffic, 10);
        Assert.Equal("162.249.72.10", s!.Address.ToString());
        Assert.True(s.Udp);
        Assert.Equal(64, s.PacketsPerSecond);
    }

    [Fact]
    public void Sem_UDP_fica_a_ligacao_TCP()
    {
        var s = ServerPicker.Pick(new[] { new RemoteTraffic(IPAddress.Parse("185.40.64.65"), 2099, false, 1, 0) }, 3);
        Assert.False(s!.Udp);
        Assert.Null(ServerPicker.Pick(Array.Empty<RemoteTraffic>(), 3));
    }

    // ---- onde está o lag

    static HopStats Hop(int ttl, string ip, double avg, double lossPct = 0, double worst = -1, double jitter = 1)
    {
        const int sent = 50;
        var received = (int)Math.Round(sent * (1 - lossPct / 100));
        return new HopStats(ttl, IPAddress.Parse(ip), sent, received, avg, avg, worst < 0 ? avg + 2 : worst, jitter);
    }

    static List<HopStats> Route(params HopStats[] hops) => RouteAnalyzer.Classify(hops.ToList());

    [Fact]
    public void Caminho_bom_da_limpo()
    {
        var v = RouteAnalyzer.Diagnose(Route(
            Hop(1, "192.168.1.1", 1), Hop(2, "100.64.0.1", 6), Hop(3, "84.90.1.1", 7), Hop(4, "84.90.2.1", 8),
            Hop(5, "62.115.1.1", 20), Hop(6, "62.115.2.1", 32), Hop(7, "162.249.72.1", 33), Hop(8, "162.249.72.10", 34)));
        Assert.Equal(LagSource.None, v.Source);
    }

    [Fact]
    public void Perda_num_salto_do_meio_que_nao_continua_nao_e_lag()
    {
        // o salto 5 ignora pings (rate limit), mas os seguintes estão perfeitos
        var v = RouteAnalyzer.Diagnose(Route(
            Hop(1, "192.168.1.1", 1), Hop(2, "100.64.0.1", 6), Hop(3, "84.90.1.1", 7), Hop(4, "84.90.2.1", 8),
            Hop(5, "62.115.1.1", 20, lossPct: 60), Hop(6, "62.115.2.1", 32), Hop(7, "162.249.72.1", 33), Hop(8, "162.249.72.10", 34)));
        Assert.Equal(LagSource.None, v.Source);
    }

    [Fact]
    public void Perda_que_comeca_no_operador_e_continua_culpa_o_operador()
    {
        var v = RouteAnalyzer.Diagnose(Route(
            Hop(1, "192.168.1.1", 1), Hop(2, "100.64.0.1", 6), Hop(3, "84.90.1.1", 7, lossPct: 12), Hop(4, "84.90.2.1", 8, lossPct: 10),
            Hop(5, "62.115.1.1", 20, lossPct: 12), Hop(6, "62.115.2.1", 32, lossPct: 10), Hop(7, "162.249.72.1", 33, lossPct: 12), Hop(8, "162.249.72.10", 34, lossPct: 10)));
        Assert.Equal(LagSource.Provider, v.Source);
        Assert.Equal(3, v.FromTtl);
    }

    [Fact]
    public void Router_lento_culpa_a_rede_de_casa()
    {
        var v = RouteAnalyzer.Diagnose(Route(
            Hop(1, "192.168.1.1", 18, worst: 140, jitter: 15), Hop(2, "100.64.0.1", 24), Hop(3, "84.90.1.1", 25), Hop(4, "84.90.2.1", 26),
            Hop(5, "62.115.1.1", 38), Hop(6, "162.249.72.10", 50)));
        Assert.Equal(LagSource.Home, v.Source);
    }

    [Fact]
    public void Perda_so_no_fim_culpa_o_servidor()
    {
        var v = RouteAnalyzer.Diagnose(Route(
            Hop(1, "192.168.1.1", 1), Hop(2, "100.64.0.1", 6), Hop(3, "84.90.1.1", 7), Hop(4, "84.90.2.1", 8),
            Hop(5, "62.115.1.1", 20), Hop(6, "62.115.2.1", 32), Hop(7, "162.249.72.1", 33, lossPct: 15), Hop(8, "162.249.72.10", 34, lossPct: 14)));
        Assert.Equal(LagSource.Server, v.Source);
    }

    [Fact]
    public void Distancia_longa_no_meio_nao_e_problema()
    {
        // +80 ms ao atravessar o Atlântico é distância, não lag
        var v = RouteAnalyzer.Diagnose(Route(
            Hop(1, "192.168.1.1", 1), Hop(2, "100.64.0.1", 6), Hop(3, "84.90.1.1", 7), Hop(4, "84.90.2.1", 8),
            Hop(5, "62.115.1.1", 20), Hop(6, "62.115.2.1", 100), Hop(7, "162.249.72.1", 101), Hop(8, "162.249.72.10", 102)));
        Assert.Equal(LagSource.None, v.Source);
    }

    [Fact]
    public void Classifica_casa_operador_internet_servidor()
    {
        var r = Route(Hop(1, "192.168.1.1", 1), Hop(2, "100.64.0.1", 6), Hop(3, "84.90.1.1", 7), Hop(4, "84.90.2.1", 8),
            Hop(5, "62.115.1.1", 20), Hop(6, "62.115.2.1", 32), Hop(7, "162.249.72.1", 33), Hop(8, "162.249.72.10", 34));
        Assert.Equal(new[] { RouteSegment.Home, RouteSegment.Provider, RouteSegment.Provider, RouteSegment.Provider,
            RouteSegment.Internet, RouteSegment.Internet, RouteSegment.Server, RouteSegment.Server }, r.Select(h => h.Segment));
    }

    [Fact]
    public void Rede_10_x_depois_do_router_e_do_operador()
    {
        // caminho real (MEO/NOS): router de casa e depois a rede interna do operador em 10.x
        var r = Route(Hop(1, "192.168.1.1", 1), Hop(2, "10.44.63.254", 10), Hop(3, "10.137.206.113", 10), Hop(4, "10.255.51.30", 25));
        Assert.Equal(RouteSegment.Home, r[0].Segment);
        Assert.All(r.Skip(1), h => Assert.NotEqual(RouteSegment.Home, h.Segment));
        // com um 2.º router de casa (192.168.x), esse também é casa
        var dbl = Route(Hop(1, "192.168.0.1", 1), Hop(2, "192.168.1.1", 2), Hop(3, "10.44.63.254", 10));
        Assert.Equal(new[] { RouteSegment.Home, RouteSegment.Home }, dbl.Take(2).Select(h => h.Segment));
    }

    // ---- medição ao vivo

    [Fact]
    public void Resumo_da_janela_conta_perda_e_jitter()
    {
        var s = LiveLatency.Summarize(new double?[] { 20, 22, null, 20, 30 }, nearServer: false);
        Assert.Equal(30, s.PingMs);
        Assert.Equal(23, s.AvgMs);
        Assert.Equal(20, s.LossPercent);
        Assert.Equal((2 + 2 + 10) / 3.0, s.JitterMs, 3);
        Assert.Equal(LinkQuality.Bad, s.Quality); // 20% de perda
    }

    [Theory]
    [InlineData(25, 0, 2, LinkQuality.Good)]
    [InlineData(80, 0, 5, LinkQuality.Fair)]
    [InlineData(25, 2, 2, LinkQuality.Fair)]
    [InlineData(150, 0, 2, LinkQuality.Bad)]
    public void Qualidade_da_ligacao(double avg, double loss, double jitter, LinkQuality q) =>
        Assert.Equal(q, LiveSample.Classify(avg, loss, jitter));

    // ---- placa de rede

    [Fact]
    public void Plano_da_placa_so_muda_o_que_existe_e_esta_mal()
    {
        var current = new Dictionary<string, string>
        {
            ["*InterruptModeration"] = "1",  // ligado → muda
            ["*EEE"] = "0",                  // já desligado → fica
            ["EnableGreenEthernet"] = "1",   // muda
            [NicTuning.NagleKey] = "0",      // Nagle ligado → muda
        };
        var plan = NicTuning.Plan(current);
        Assert.Equal(new[] { "*InterruptModeration", "EnableGreenEthernet", NicTuning.NagleKey }, plan.Select(c => c.Keyword));
        Assert.All(plan, c => Assert.Equal("Desligado", c.To));
        Assert.Empty(NicTuning.Plan(new Dictionary<string, string> { ["*EEE"] = "0" }));
    }
}
