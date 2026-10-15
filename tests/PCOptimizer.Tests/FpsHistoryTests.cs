using PCOptimizer.Core.Benchmark;
using PCOptimizer.Core.History;
using Xunit;

namespace PCOptimizer.Tests;

[CollectionDefinition("fps", DisableParallelization = true)]
public class FpsCollection { }

[Collection("fps")] // usa PCOPT_FPS_FILE: não corre em paralelo com outros testes do histórico
public class FpsHistoryTests : IDisposable
{
    readonly string _file = Path.Combine(Path.GetTempPath(), $"fps-{Guid.NewGuid():N}.json");
    public FpsHistoryTests() => Environment.SetEnvironmentVariable("PCOPT_FPS_FILE", _file);
    public void Dispose() { Environment.SetEnvironmentVariable("PCOPT_FPS_FILE", null); File.Delete(_file); File.Delete(_file + ".bad"); }

    static FpsRun Run(FpsRole role, double avg, double low, DateTime when, string exe = "cs2.exe") =>
        new("Counter-Strike 2", exe, when, role, 60, avg, low, low * 0.8, 0);
    static ChangeEntry Tweak(string id, DateTime when, bool undone = false) =>
        new() { Title = id, When = when, Undo = UndoKind.Tweak, Data = new() { ["id"] = id }, Undone = undone };

    static readonly DateTime T0 = new(2026, 10, 14, 20, 0, 0);

    [Theory]
    [InlineData(100, 50, 111, 57.5, FpsOutcome.Better)]   // +11%, +15%
    [InlineData(100, 50, 102, 49, FpsOutcome.Same)]       // dentro de ±3%
    [InlineData(100, 50, 93, 50, FpsOutcome.Worse)]       // −7%
    [InlineData(100, 50, 106, 46, FpsOutcome.Mixed)]      // +6% mas 1% low −8%
    [InlineData(100, 50, 103, 50, FpsOutcome.Better)]     // exatamente +3% conta como subir
    public void Veredito(double avgB, double lowB, double avgA, double lowA, FpsOutcome expected) =>
        Assert.Equal(expected, FpsVerdict.Compare(Run(FpsRole.Before, avgB, lowB, T0), Run(FpsRole.After, avgA, lowA, T0.AddHours(1))).Outcome);

    [Theory]
    [InlineData(FpsOutcome.Worse, 3, null, true)]
    [InlineData(FpsOutcome.Same, 1, null, true)]
    [InlineData(FpsOutcome.Mixed, 2, null, true)]
    [InlineData(FpsOutcome.Worse, 3, 0, false)]    // carregou em Manter neste veredito
    [InlineData(FpsOutcome.Worse, 3, -1, true)]    // Manter foi noutro veredito, mais antigo
    [InlineData(FpsOutcome.Worse, 0, null, false)] // tudo já desfeito
    [InlineData(FpsOutcome.Better, 3, null, false)]
    public void Veredito_volta_a_aparecer_so_com_ajustes_por_desfazer(FpsOutcome outcome, int tweaks, int? keptDaysFromAfter, bool expected)
    {
        var after = T0.AddHours(1);
        Assert.Equal(expected, FpsVerdict.ShouldShow(outcome, tweaks, keptDaysFromAfter is int d ? after.AddDays(d) : null, after));
    }

    static FpsComparison Cmp(double avgB, double lowB, double avgA, double lowA) =>
        FpsVerdict.Compare(Run(FpsRole.Before, avgB, lowB, T0), Run(FpsRole.After, avgA, lowA, T0.AddHours(1)));

    [Fact]
    public void Texto_do_veredito_diz_o_que_mudou_mesmo()
    {
        Assert.Equal("Resultado misto: +6% FPS médios, mas 1% low −8% (mais engasgos).", FpsVerdict.Text(Cmp(100, 50, 106, 46), 2, T0));
        Assert.Equal("Resultado misto: −6% FPS médios, mas 1% low +8% (mais estável).", FpsVerdict.Text(Cmp(100, 50, 94, 54), 2, T0));
        Assert.Equal("Piorou: −7% FPS médios, 1% low +0%.", FpsVerdict.Text(Cmp(100, 50, 93, 50), 2, T0));
        var one = FpsVerdict.Text(Cmp(100, 50, 101, 50), 1, T0);
        Assert.StartsWith("Sem diferença (±3% é a variação normal entre testes). O ajuste aplicado desde ", one);
        Assert.EndsWith(" não deu FPS neste jogo.", one);
        Assert.Contains("Os 3 ajustes aplicados desde ", FpsVerdict.Text(Cmp(100, 50, 101, 50), 3, T0));
    }

    [Fact]
    public void FPS_limitados_nao_culpam_os_ajustes_nem_oferecem_desfazer()
    {
        var capped = Cmp(59.8, 50, 60.1, 50);
        Assert.Equal(FpsOutcome.Same, capped.Outcome);
        Assert.Equal("Sem diferença (±3% é a variação normal entre testes). Os FPS parecem limitados a 60 (V-Sync ou limite do jogo): desliga o limite para o teste mostrar diferenças.",
            FpsVerdict.Text(capped, 3, T0));
        Assert.False(FpsVerdict.OffersUndo(capped, 3));
        Assert.True(FpsVerdict.OffersUndo(Cmp(100, 50, 93, 50), 3));    // pior, sem limite
        Assert.True(FpsVerdict.OffersUndo(Cmp(100, 50, 101, 50), 1));   // sem diferença, sem limite
        Assert.False(FpsVerdict.OffersUndo(Cmp(100, 50, 111, 57.5), 3)); // melhorou
        Assert.False(FpsVerdict.OffersUndo(Cmp(100, 50, 93, 50), 0));   // nada para desfazer
    }

    [Fact]
    public void Depois_espera_pelo_reinicio_so_com_ajustes_que_o_pedem_feitos_depois_do_arranque()
    {
        var boot = T0.AddMinutes(30);
        Func<string, bool> reboot = id => id == "game.hags";
        Assert.True(FpsVerdict.RebootPending(new[] { Tweak("game.dvr_off", T0.AddMinutes(40)), Tweak("game.hags", T0.AddMinutes(40)) }, reboot, boot));
        Assert.False(FpsVerdict.RebootPending(new[] { Tweak("game.hags", T0.AddMinutes(10)) }, reboot, boot));   // já reiniciou depois
        Assert.False(FpsVerdict.RebootPending(new[] { Tweak("game.dvr_off", T0.AddMinutes(40)) }, reboot, boot)); // não pede reinício
        Assert.False(FpsVerdict.RebootPending(Array.Empty<ChangeEntry>(), reboot, boot));
    }

    [Fact]
    public void FPS_limitados_sao_avisados()
    {
        Assert.Equal(60, FpsVerdict.LikelyCap(59.8, 60.1));
        Assert.Equal(144, FpsVerdict.Compare(Run(FpsRole.Before, 143.6, 120, T0), Run(FpsRole.After, 144.1, 121, T0.AddHours(1))).Cap);
        Assert.Null(FpsVerdict.LikelyCap(97, 98));
    }

    [Fact]
    public void Desfazer_so_os_ajustes_entre_os_testes_e_cada_um_uma_vez()
    {
        var history = new[]
        {
            Tweak("game.hags", T0.AddMinutes(-5)),                 // antes do teste Antes
            Tweak("game.dvr_off", T0.AddMinutes(10)),
            Tweak("game.dvr_off", T0.AddMinutes(20)),              // o mesmo ajuste outra vez
            Tweak("power.ultimate", T0.AddMinutes(30), undone: true),
            Tweak("game.gamemode", T0.AddHours(2)),                // depois do teste Depois
        };
        var between = FpsVerdict.TweaksBetween(history, T0, T0.AddHours(1));
        Assert.Equal(new[] { "game.dvr_off" }, between.Select(e => e.Data["id"]));
        Assert.Equal(T0.AddMinutes(20), between[0].When);           // fica a entrada mais recente
    }

    [Fact]
    public void Proximo_teste_e_Depois_so_com_Antes_pendente_e_ajustes_novos()
    {
        var before = Run(FpsRole.Before, 100, 50, T0);
        Assert.Equal(FpsRole.Before, FpsVerdict.NextTestRole(null, new[] { Tweak("game.hags", T0.AddMinutes(5)) }, T0.AddHours(1)));
        Assert.Equal(FpsRole.Before, FpsVerdict.NextTestRole(before, Array.Empty<ChangeEntry>(), T0.AddHours(1)));
        Assert.Equal(FpsRole.After, FpsVerdict.NextTestRole(before, new[] { Tweak("game.hags", T0.AddMinutes(5)) }, T0.AddHours(1)));
    }

    [Fact]
    public void Historico_grava_e_le_e_guarda_um_Antes_pendente_por_jogo()
    {
        FpsHistory.Add(Run(FpsRole.Session, 90, 40, T0.AddMinutes(-30)));
        FpsHistory.Add(Run(FpsRole.Before, 100, 50, T0));
        Assert.Equal(100, FpsHistory.PendingBefore("CS2.EXE")!.AvgFps);   // sem diferenças de maiúsculas
        FpsHistory.Add(Run(FpsRole.After, 111, 57, T0.AddHours(1)));
        Assert.Null(FpsHistory.PendingBefore("cs2.exe"));
        Assert.Equal(111, FpsHistory.LatestPair()!.Value.After.AvgFps);
        var sum = Assert.Single(FpsHistory.Summaries());
        Assert.Equal(90, sum.Last!.AvgFps);
    }

    [Fact]
    public void Ficheiro_estragado_vai_para_bad_e_o_historico_continua()
    {
        File.WriteAllText(_file, "{nao e json");
        FpsHistory.Add(Run(FpsRole.Session, 90, 40, T0));
        Assert.Equal(90, Assert.Single(FpsHistory.All).AvgFps);
        Assert.Equal("{nao e json", File.ReadAllText(_file + ".bad"));
    }

    [Fact]
    public void Ficheiro_bloqueado_nao_e_apagado()
    {
        FpsHistory.Add(Run(FpsRole.Session, 90, 40, T0));
        using (new FileStream(_file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            FpsHistory.Add(Run(FpsRole.Session, 80, 30, T0.AddMinutes(1)));   // não pode lançar nem apagar o que lá está
        Assert.Equal(90, Assert.Single(FpsHistory.All).AvgFps);
    }
}
