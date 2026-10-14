using PCOptimizer.Core.History;

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

    /// <summary>Desfaz os ajustes dados. Devolve quantos foram mesmo revertidos (os que já não tinham backup não contam).</summary>
    public static async Task<int> UndoTweaksAsync(IReadOnlyList<ChangeEntry> entries)
    {
        var reverted = 0;
        foreach (var e in entries.Where(e => e.CanUndo))
            if ((await ChangeLog.UndoAsync(e)).StartsWith('✓')) reverted++;
        return reverted;
    }

    static double ChangePct(double before, double after) => before > 0 ? (after - before) * 100 / before : 0;
}
