using PCOptimizer.Core.History;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Benchmark;

public enum FpsOutcome { Better, Same, Worse, Mixed }

/// <summary>Variação do FPS médio e do 1% low (em %) e o veredito; Cap é o limite provável de FPS (V-Sync, limitador), se houver.</summary>
public sealed record FpsComparison(FpsOutcome Outcome, double AvgChangePct, double LowChangePct, int? Cap);

/// <summary>Compara o teste Antes com o Depois e decide que ajustes se desfazem se o jogo ficou pior.</summary>
public static class FpsVerdict
{
    public const double ThresholdPct = 3;
    public const double MinSeconds = 20;

    static readonly int[] Caps = { 30, 60, 75, 90, 120, 144, 165, 240 };

    public static FpsComparison Compare(FpsRun before, FpsRun after)
    {
        var avg = ChangePct(before.AvgFps, after.AvgFps);
        var low = ChangePct(before.Low1Fps, after.Low1Fps);
        var up = avg >= ThresholdPct || low >= ThresholdPct;
        var down = avg < -ThresholdPct || low < -ThresholdPct;
        var outcome = up && down ? FpsOutcome.Mixed : up ? FpsOutcome.Better : down ? FpsOutcome.Worse : FpsOutcome.Same;
        return new FpsComparison(outcome, avg, low, LikelyCap(before.AvgFps, after.AvgFps));
    }

    /// <summary>Se os dois FPS médios estão colados ao mesmo limite (60, 144...), o PC não consegue mostrar a diferença.</summary>
    public static int? LikelyCap(double avgBefore, double avgAfter)
    {
        foreach (var cap in Caps)
            if (Math.Abs(avgBefore - cap) * 100 / cap < 1.5 && Math.Abs(avgAfter - cap) * 100 / cap < 1.5) return cap;
        return null;
    }

    /// <summary>Os ajustes feitos entre os dois testes, cada um uma vez (a entrada mais recente), do mais recente para o mais antigo.</summary>
    public static List<ChangeEntry> TweaksBetween(IEnumerable<ChangeEntry> history, DateTime from, DateTime to) =>
        history.Where(e => e.Undo == UndoKind.Tweak && !e.Undone && e.When > from && e.When < to)
            .GroupBy(e => e.Data["id"])
            .Select(g => g.MaxBy(e => e.When)!)
            .OrderByDescending(e => e.When).ToList();

    /// <summary>O próximo teste guiado é o Depois só se há um Antes à espera e ajustes feitos desde então.</summary>
    public static FpsRole NextTestRole(FpsRun? pendingBefore, IEnumerable<ChangeEntry> history, DateTime now) =>
        pendingBefore != null && TweaksBetween(history, pendingBefore.When, now).Count > 0 ? FpsRole.After : FpsRole.Before;

    /// <summary>
    /// O cartão volta a mostrar o último veredito (depois de reabrir a app, por exemplo como administrador para desfazer)
    /// só se há ajustes por desfazer, não melhorou (Melhorou não tem botões, nem Manter para o fechar) e não se carregou em Manter.
    /// </summary>
    public static bool ShouldShow(FpsOutcome outcome, int tweakCount, DateTime? keptAt, DateTime afterWhen) =>
        outcome != FpsOutcome.Better && tweakCount > 0 && keptAt != afterWhen;

    /// <summary>Desfaz os ajustes dados. Devolve quantos foram mesmo revertidos (os que já não tinham backup não contam).</summary>
    public static async Task<int> UndoTweaksAsync(IReadOnlyList<ChangeEntry> entries)
    {
        var reverted = 0;
        foreach (var e in entries.Where(e => e.CanUndo))
            if ((await ChangeLog.UndoAsync(e)).StartsWith('✓')) reverted++;
        return reverted;
    }

    /// <summary>
    /// O texto do veredito. Com os FPS presos a um limite o teste não mede os ajustes: avisa do limite em vez de dizer
    /// que os ajustes não deram FPS.
    /// </summary>
    public static string Text(FpsComparison c, int tweakCount, DateTime since)
    {
        string avg = Signed(c.AvgChangePct), low = Signed(c.LowChangePct);
        var text = c.Outcome switch
        {
            FpsOutcome.Better => Loc.T($"Melhorou: {avg}% FPS médios, 1% low {low}%."),
            FpsOutcome.Same => Loc.T("Sem diferença (±3% é a variação normal entre testes).") + (c.Cap is not null ? "" : " " + (tweakCount == 1
                ? Loc.T($"O ajuste aplicado desde {since:dd/MM} não deu FPS neste jogo.")
                : Loc.T($"Os {tweakCount} ajustes aplicados desde {since:dd/MM} não deram FPS neste jogo."))),
            FpsOutcome.Worse => Loc.T($"Piorou: {avg}% FPS médios, 1% low {low}%."),
            // misto: um subiu e o outro desceu
            _ => c.LowChangePct < 0
                ? Loc.T($"Resultado misto: {avg}% FPS médios, mas 1% low {low}% (mais engasgos).")
                : Loc.T($"Resultado misto: {avg}% FPS médios, mas 1% low {low}% (mais estável)."),
        };
        return text + (c.Cap is int cap ? " " + Loc.T($"Os FPS parecem limitados a {cap} (V-Sync ou limite do jogo): desliga o limite para o teste mostrar diferenças.") : "");
    }

    /// <summary>O Desfazer só aparece se os ajustes podem ter culpa: não melhorou e os FPS não estão presos a um limite.</summary>
    public static bool OffersUndo(FpsComparison c, int tweakCount) =>
        tweakCount > 0 && c.Outcome != FpsOutcome.Better && c.Cap is null;

    /// <summary>
    /// Algum dos ajustes desde o Antes só funciona depois de reiniciar e foi feito depois do último arranque: um Depois
    /// agora mediria o PC sem ele.
    /// </summary>
    public static bool RebootPending(IEnumerable<ChangeEntry> tweaks, Func<string, bool> requiresReboot, DateTime bootTime) =>
        tweaks.Any(e => e.When > bootTime && requiresReboot(e.Data["id"]));

    // arredonda para int antes: um double entre −0,5 e 0 sairia "-+0"
    /// <summary>Variação em % com sinal, sem casas (N0), com o "−" tipográfico.</summary>
    public static string Signed(double pct) => ((int)Math.Round(pct, MidpointRounding.AwayFromZero)).ToString("+#,0;−#,0");

    static double ChangePct(double before, double after) => before > 0 ? (after - before) * 100 / before : 0;
}
