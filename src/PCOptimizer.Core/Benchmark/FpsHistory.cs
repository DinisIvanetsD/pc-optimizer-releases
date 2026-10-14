using System.Text.Json;
using PCOptimizer.Core.Settings;

namespace PCOptimizer.Core.Benchmark;

/// <summary>Session = jogo normal medido ao vivo; Before/After = os dois testes guiados de 60 s.</summary>
public enum FpsRole { Session, Before, After }

/// <summary>Uma medição de FPS de um jogo.</summary>
public sealed record FpsRun(string Game, string Exe, DateTime When, FpsRole Role, double Seconds, double AvgFps, double Low1Fps, double Low01Fps, int Stutters)
{
    public static FpsRun From(string game, string exe, FpsRole role, FrameStats s, DateTime when) =>
        new(game, exe, when, role, s.Seconds, s.AvgFps, s.Low1Fps, s.Low01Fps, s.Stutters);
}

/// <summary>O que a lista de jogos mostra por jogo: última sessão, média das últimas 5 e o último par Antes/Depois.</summary>
public sealed record FpsGameSummary(string Game, string Exe, FpsRun? Last, double? Avg5, FpsRun? Before, FpsRun? After);

/// <summary>Histórico de FPS por jogo, guardado em %LOCALAPPDATA%\PCOptimizer\fps.json.</summary>
public static class FpsHistory
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly object Gate = new();

    /// <summary>PCOPT_FPS_FILE permite testar com um ficheiro à parte. Lido a cada acesso (sem cache).</summary>
    public static string FilePath => Environment.GetEnvironmentVariable("PCOPT_FPS_FILE") ?? Path.Combine(AppSettings.DataFolder, "fps.json");

    public static IReadOnlyList<FpsRun> All
    {
        get { lock (Gate) return Read(); }
    }

    /// <summary>Guarda as últimas 200 sessões e os últimos 100 testes (Antes/Depois).</summary>
    public static void Add(FpsRun run)
    {
        lock (Gate)
        {
            var list = Read();
            list.Add(run);
            var keep = list.Where(r => r.Role == FpsRole.Session).OrderBy(r => r.When).TakeLast(200)
                .Concat(list.Where(r => r.Role != FpsRole.Session).OrderBy(r => r.When).TakeLast(100))
                .OrderBy(r => r.When).ToList();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(keep, Json));
            }
            catch { }
        }
    }

    /// <summary>O Antes mais recente deste jogo, se ainda não tem um Depois a seguir.</summary>
    public static FpsRun? PendingBefore(string exe)
    {
        var mine = All.Where(r => Same(r.Exe, exe)).ToList();
        var before = mine.Where(r => r.Role == FpsRole.Before).MaxBy(r => r.When);
        return before != null && !mine.Any(r => r.Role == FpsRole.After && r.When > before.When) ? before : null;
    }

    /// <summary>O Depois mais recente (de qualquer jogo), com o Antes mais recente desse jogo anterior a ele.</summary>
    public static (FpsRun Before, FpsRun After)? LatestPair()
    {
        var all = All;
        var after = all.Where(r => r.Role == FpsRole.After).MaxBy(r => r.When);
        return after is null ? null : PairOf(all, after);
    }

    /// <summary>Um por jogo (pelo exe), o que jogou há menos tempo primeiro.</summary>
    public static List<FpsGameSummary> Summaries()
    {
        var all = All;
        return all.GroupBy(r => r.Exe, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Max(r => r.When))
            .Select(g =>
            {
                var latest = g.MaxBy(r => r.When)!;
                var sessions = g.Where(r => r.Role == FpsRole.Session).OrderByDescending(r => r.When).ToList();
                var after = g.Where(r => r.Role == FpsRole.After).MaxBy(r => r.When);
                var pair = after is null ? null : PairOf(all, after);
                return new FpsGameSummary(latest.Game, latest.Exe, sessions.FirstOrDefault(),
                    sessions.Count > 0 ? sessions.Take(5).Average(r => r.AvgFps) : null, pair?.Before, pair?.After);
            }).ToList();
    }

    static (FpsRun Before, FpsRun After)? PairOf(IReadOnlyList<FpsRun> all, FpsRun after)
    {
        var before = all.Where(r => r.Role == FpsRole.Before && Same(r.Exe, after.Exe) && r.When < after.When).MaxBy(r => r.When);
        return before is null ? null : (before, after);
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static List<FpsRun> Read()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<FpsRun>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }
}
