using PCOptimizer.Core.Diagnostics;
using PCOptimizer.Core.Drivers;
using PCOptimizer.Core.Settings;
using PCOptimizer.Core.Storage;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Maintenance;

/// <summary>Algo que a verificação automática encontrou e que merece uma notificação.</summary>
/// <param name="Tab">aba a abrir quando se clica na notificação ("drivers", "diag", "storage")</param>
public sealed record MaintenanceNotice(string Title, string Text, string Tab, bool Important);

/// <summary>
/// Verificação periódica: só lê e avisa — nunca muda nada sozinha.
/// Para não chatear, só avisa de coisas novas (o mesmo driver novo só é avisado uma vez).
/// </summary>
public static class MaintenanceRunner
{
    public static bool IsDue(AppSettings s) =>
        s.AutoMaintenance && (s.LastMaintenance is not DateTime last || DateTime.Now - last >= TimeSpan.FromDays(s.MaintenanceDays));

    public static async Task<List<MaintenanceNotice>> RunAsync(AppSettings s, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var notices = new List<MaintenanceNotice>();
        var seen = s.NotifiedKeys;

        if (s.MaintainDiagnostics)
        {
            progress?.Report(Loc.T("A procurar problemas…"));
            var findings = await DiagnosticsEngine.RunAsync(null, ct);
            var critical = findings.Where(f => f.Severity == Severity.Critical && !f.NotifiedElsewhere).ToList();
            var fresh = critical.Where(f => seen.Add("diag:" + f.Title)).ToList();
            if (fresh.Count > 0)
                notices.Add(new MaintenanceNotice(
                    fresh.Count == 1 ? Loc.T("Problema encontrado no PC") : Loc.T($"{fresh.Count} problemas encontrados no PC"),
                    string.Join("\n", fresh.Take(3).Select(f => "• " + f.Title)), "diag", true));
        }

        if (s.MaintainDrivers)
        {
            progress?.Report(Loc.T("A procurar drivers novos…"));
            var keys = await Task.Run(DriverManager.KeyDrivers, ct);
            var problems = await Task.Run(DriverManager.ProblemDevices, ct);
            var offers = await DriverDownloads.FindOffersAsync(keys, problems, ct);
            var fresh = offers.Where(o => seen.Add($"drv:{o.MatchKey}:{o.Version}")).ToList();
            var missing = problems.Where(p => p.Kind == DeviceProblemKind.Missing && seen.Add("missing:" + p.InstanceId)).ToList();
            if (fresh.Count > 0)
                notices.Add(new MaintenanceNotice(
                    fresh.Count == 1 ? Loc.T("Há um driver novo") : Loc.T($"Há {fresh.Count} drivers novos"),
                    string.Join("\n", fresh.Take(3).Select(o => "• " + o.Title)) + Loc.T("\nAbre a aba Drivers para instalar."), "drivers", false));
            if (missing.Count > 0)
                notices.Add(new MaintenanceNotice(Loc.T($"{missing.Count} dispositivo(s) sem driver"),
                    string.Join("\n", missing.Take(3).Select(p => "• " + p.Name)), "drivers", true));
        }

        if (s.MaintainDisk)
        {
            progress?.Report(Loc.T("A ver o espaço nos discos…"));
            foreach (var d in await StorageDrives.ListAsync())
            {
                if (d.UsedPercent < 90) { seen.Remove("disk:" + d.Letter); continue; }
                if (!seen.Add("disk:" + d.Letter)) continue;
                notices.Add(new MaintenanceNotice(Loc.T($"O disco {d.Letter} está quase cheio"),
                    Loc.T($"{d.UsedPercent:N0}% usado, só {SystemInfo.FormatBytes(d.FreeBytes)} livres. Discos cheios deixam o PC lento."), "storage", true));
            }
        }

        s.LastMaintenance = DateTime.Now;
        s.Save();
        return notices;
    }
}
