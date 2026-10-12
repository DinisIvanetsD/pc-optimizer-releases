using PCOptimizer.Core.Gaming;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Diagnostics;

/// <summary>Jogos (Steam e Epic) que crasharam nos últimos 30 dias, com a causa provável e como resolver.</summary>
public sealed class GameCheck : IDiagnosticCheck
{
    public string Name => Loc.T("Crashes dos jogos");

    public Task<IEnumerable<Finding>> RunAsync(CancellationToken ct) => Task.Run(() =>
    {
        var findings = new List<Finding>();
        foreach (var (game, crashes) in GameCrashes.Find(GameCrashes.Installed()).OrderByDescending(kv => kv.Value.Count))
        {
            var today = crashes.Count(c => c.When.Date == DateTime.Today);
            var week = crashes.Count(c => c.When >= DateTime.Now.AddDays(-7));
            var last = crashes[0];
            var (cause, text) = GameCrashes.Verdict(crashes);
            var lastText = Loc.T($"Último: {last.When:dd/MM HH:mm}") +
                (last.Module != null ? Loc.T($", em {last.Module}") : "") +
                (last.Code is uint code ? $" ({GameCrashes.CodeName(code)})" : "") + ".";

            var f = new Finding
            {
                Category = Loc.T("Jogos"),
                Severity = week >= 5 ? Severity.Critical : Severity.Warning,
                Title = Loc.T($"{game.Name} crashou {crashes.Count} vez(es) nos últimos 30 dias") + (today > 0 ? Loc.T($" ({today} hoje)") : ""),
                Description = text + "\n" + lastText,
            };
            if (game.VerifyUri is string uri)
                f.Actions.Add(new FixAction(Loc.T($"Verificar ficheiros ({game.Store})"), () =>
                {
                    Shell.Open(uri);
                    return Task.FromResult(Loc.T($"A {game.Store} vai verificar os ficheiros do jogo e descarregar outra vez os que estiverem estragados."));
                }));
            if (cause == CrashCause.Memory)
                f.Actions.Insert(0, new FixAction(Loc.T("Testar a RAM"), () => { UiHooks.Navigate?.Invoke("ramtest"); return Task.FromResult(""); }));
            if (cause == CrashCause.GraphicsDriver)
                f.Actions.Insert(0, new FixAction(Loc.T("Ver drivers"), () => { UiHooks.Navigate?.Invoke("drivers"); return Task.FromResult(""); }));
            findings.Add(f);
        }
        return (IEnumerable<Finding>)findings;
    }, ct);
}
