using System.Net;
using System.Text;
using PCOptimizer.Core.Benchmark;
using PCOptimizer.Core.History;
using PCOptimizer.Core.Monitoring;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Reports;

/// <summary>
/// Relatório do PC num ficheiro HTML (abre no browser, imprime ou guarda em PDF).
/// Útil para mostrar a um técnico ou num fórum. O nome de utilizador é escondido.
/// </summary>
public static class ReportBuilder
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rigwise");

    /// <param name="timeline">Null = não incluir (ler os registos do Windows demora uns segundos).</param>
    public static string Build(IReadOnlyList<Finding> findings, int? health, string appVersion,
        IReadOnlyList<TimelineEvent>? timeline = null, IReadOnlyList<Recording>? recordings = null)
    {
        string E(string s) => WebUtility.HtmlEncode(PcSummary.Redact(s));
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html>
            <html lang="pt-PT"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Relatório do PC — {{DateTime.Now:dd/MM/yyyy}}</title>
            <style>
              :root { --bg:#f6f7f9; --card:#fff; --text:#1b1f27; --muted:#5b6472; --line:#e3e6eb;
                      --crit:#d93636; --warn:#c77700; --ok:#1f9d5c; --info:#3867d6; }
              @media (prefers-color-scheme: dark) { :root { --bg:#0e1014; --card:#1a1e26; --text:#e8eaed; --muted:#9aa3af; --line:#262b35; } }
              body { margin:0; background:var(--bg); color:var(--text); font:15px/1.5 "Segoe UI", system-ui, sans-serif; }
              main { max-width:900px; margin:0 auto; padding:32px 16px 64px; }
              h1 { margin:0 0 4px; font-size:26px; } h2 { font-size:18px; margin:32px 0 12px; }
              .muted { color:var(--muted); } .card { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:16px 18px; margin:10px 0; }
              .score { font-size:48px; font-weight:700; } .tag { display:inline-block; font-size:12px; font-weight:600; padding:2px 8px; border-radius:6px; color:#fff; margin-right:8px; }
              .Critical { background:var(--crit); } .Warning { background:var(--warn); } .Info { background:var(--info); } .Ok { background:var(--ok); }
              .card.f { border-left:5px solid var(--line); } .card.f.Critical { border-left-color:var(--crit); background:var(--card); }
              .card.f.Warning { border-left-color:var(--warn); background:var(--card); } .card.f.Ok { border-left-color:var(--ok); background:var(--card); } .card.f.Info { border-left-color:var(--info); background:var(--card); }
              pre { white-space:pre-wrap; font:inherit; margin:6px 0 0; color:var(--muted); }
              table { width:100%; border-collapse:collapse; } td, th { text-align:left; padding:6px 8px; border-top:1px solid var(--line); } th { color:var(--muted); font-weight:600; font-size:13px; }
              .better { color:var(--ok); font-weight:600; } .worse { color:var(--crit); font-weight:600; }
              @media print { body { background:#fff; } .card { break-inside:avoid; } }
            </style></head><body><main>
            <h1>Relatório do PC</h1>
            <div class="muted">Gerado pelo Rigwise {{appVersion}} a {{DateTime.Now:dd/MM/yyyy 'às' HH:mm}}</div>
            """);

        // resumo
        var crit = findings.Count(f => f.Severity == Severity.Critical);
        var warn = findings.Count(f => f.Severity == Severity.Warning);
        sb.Append($"""
            <div class="card"><div class="muted">Saúde do PC</div><div class="score">{(health?.ToString() ?? "—")}<span class="muted" style="font-size:18px">/100</span></div>
            <div>{crit} problema(s) crítico(s) · {warn} aviso(s)</div></div>
            <h2>Sistema</h2><div class="card"><pre style="color:inherit">{E(PcSummary.Text())}</pre></div>
            <h2>Problemas encontrados</h2>
            """);

        foreach (var f in findings.OrderBy(f => f.Severity))
        {
            var label = f.Severity switch { Severity.Critical => Loc.T("CRÍTICO"), Severity.Warning => Loc.T("AVISO"), Severity.Ok => "OK", _ => "INFO" };
            sb.Append($"""
                <div class="card f {f.Severity}"><span class="tag {f.Severity}">{label}</span><span class="muted">{E(f.Category)}</span>
                <div style="font-weight:600;margin-top:6px">{E(f.Title)}</div><pre>{E(f.Description)}</pre>
                {(f.Actions.Count > 0 ? $"<div class=\"muted\" style=\"margin-top:6px\">Ações sugeridas: {E(string.Join(" · ", f.Actions.Select(a => a.Label)))}</div>" : "")}</div>
                """);
        }

        // antes/depois
        var all = PcMetrics.All;
        if (PcMetrics.Baseline(all) is MetricSnapshot before && all.LastOrDefault() is MetricSnapshot now && before.When != now.When)
        {
            sb.Append(Loc.T($"<h2>Antes e depois</h2><div class=\"card\"><table><tr><th>Medida</th><th>Antes ({before.When:dd/MM})</th><th>Agora ({now.When:dd/MM})</th><th>Diferença</th></tr>"));
            foreach (var d in PcMetrics.Compare(before, now))
                sb.Append($"<tr><td>{E(d.Name)}</td><td>{E(d.Before)}</td><td>{E(d.Now)}</td><td class=\"{(d.Better == true ? "better" : d.Better == false ? "worse" : "")}\">{E(d.Change)}</td></tr>");
            sb.Append("</table></div>");
        }

        // alterações recentes
        var changes = ChangeLog.All.Take(25).ToList();
        if (changes.Count > 0)
        {
            sb.Append(Loc.T("<h2>Alterações feitas pela app</h2><div class=\"card\"><table><tr><th>Quando</th><th>O quê</th><th>Estado</th></tr>"));
            foreach (var c in changes)
                sb.Append($"<tr><td>{c.When:dd/MM HH:mm}</td><td>{E(c.Title)}</td><td class=\"muted\">{(c.Undone ? "desfeito" : c.Undo == UndoKind.None ? "" : Loc.T("pode desfazer"))}</td></tr>");
            sb.Append("</table></div>");
        }

        // problemas gravados (Diagnóstico › Gravar um problema)
        foreach (var r in (recordings ?? Array.Empty<Recording>()).Take(3))
        {
            if (r == recordings![0]) sb.Append(Loc.T("<h2>Problemas gravados</h2>"));
            sb.Append($"<div class=\"card\"><div style=\"font-weight:600\">{r.Mark:dd/MM/yyyy HH:mm:ss}</div>{Sparkline(r)}<ul>");
            foreach (var o in RecordingAnalyzer.Analyze(r)) sb.Append($"<li>{E(o.Text)}</li>");
            sb.Append("</ul></div>");
        }

        // o que mudou (crashes, drivers, atualizações, programas, Rigwise)
        if (timeline is { Count: > 0 })
        {
            var suspects = Timeline.Suspects(timeline).Take(5).ToList();
            if (suspects.Count > 0)
            {
                sb.Append(Loc.T("<h2>Vale a pena investigar</h2><div class=\"card\"><ul>"));
                foreach (var s in suspects) sb.Append($"<li><b>{s.Change.When:dd/MM HH:mm} · {E(s.Change.Title)}</b><br><span class=\"muted\">{E(s.Why)}</span></li>");
                sb.Append("</ul></div>");
            }
            sb.Append(Loc.T("<h2>O que mudou (30 dias)</h2><div class=\"card\"><table><tr><th>Quando</th><th>O quê</th><th>Origem</th></tr>"));
            foreach (var t in timeline.Where(t => t.Kind != TimelineKind.StoreApp).Take(80))
                sb.Append($"<tr><td>{t.When:dd/MM HH:mm}</td><td{(t.Kind == TimelineKind.Crash ? " class=\"worse\"" : "")}>{E(t.Title)}</td><td class=\"muted\">{E(t.Source)}</td></tr>");
            sb.Append("</table></div>");
        }

        sb.Append(Loc.T("<p class=\"muted\" style=\"margin-top:40px\">O nome de utilizador foi substituído por &lt;utilizador&gt;.</p></main></body></html>"));
        return sb.ToString();
    }

    /// <summary>CPU (roxo), disco (laranja) e ping (azul) em SVG, com a marca do momento.</summary>
    static string Sparkline(Recording r)
    {
        var s = r.Samples;
        if (s.Count < 2) return "";
        const double W = 600, H = 80;
        var t0 = s[0].Time;
        var span = Math.Max(1, (s[^1].Time - t0).TotalSeconds);
        var pingMax = Math.Max(100, s.Max(x => x.PingMs ?? 0));
        string X(DateTime t) => ((t - t0).TotalSeconds / span * W).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        string Y(double v) => (H - 2 - (H - 4) * Math.Clamp(v, 0, 100) / 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        string Line(string color, Func<RecSample, double?> v) =>
            $"<polyline fill=\"none\" stroke=\"{color}\" stroke-width=\"1.6\" points=\"{string.Join(" ", s.Where(x => v(x) is not null).Select(x => $"{X(x.Time)},{Y(v(x)!.Value)}"))}\"/>";
        return $"<svg viewBox=\"0 0 {W} {H}\" style=\"width:100%;height:80px;margin:8px 0\">" +
               Line("#8b5cf6", x => x.Cpu) + Line("#ffb020", x => x.Disk) + Line("#4fc3f7", x => x.PingMs is int p ? 100.0 * p / pingMax : null) +
               $"<line x1=\"{X(r.Mark)}\" x2=\"{X(r.Mark)}\" y1=\"0\" y2=\"{H}\" stroke=\"currentColor\" stroke-dasharray=\"3 3\"/></svg>";
    }

    /// <summary>Grava o relatório em Documentos\Rigwise e devolve o caminho.</summary>
    public static string Save(IReadOnlyList<Finding> findings, int? health, string appVersion,
        IReadOnlyList<TimelineEvent>? timeline = null, IReadOnlyList<Recording>? recordings = null)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, Loc.T($"Relatório do PC {DateTime.Now:yyyy-MM-dd HH-mm}.html"));
        File.WriteAllText(path, Build(findings, health, appVersion, timeline, recordings), new UTF8Encoding(true));
        return path;
    }
}
