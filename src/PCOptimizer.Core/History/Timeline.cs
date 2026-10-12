using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PCOptimizer.Core.Diagnostics;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.History;

public enum TimelineKind { Crash, AppCrash, Driver, Update, Program, StoreApp, Rigwise }

/// <summary>Uma coisa que aconteceu no PC. Source diz de onde veio a informação.</summary>
public sealed record TimelineEvent(DateTime When, TimelineKind Kind, string Title, string Detail, string Source);

/// <summary>Uma mudança que aconteceu pouco antes de um tipo de crash começar.</summary>
public sealed record Suspect(TimelineEvent Change, TimelineEvent FirstCrash, string Why);

/// <summary>
/// "O que mudou?": junta numa linha do tempo os crashes, os drivers instalados, as atualizações do Windows,
/// os programas instalados e as alterações do Rigwise, para ver o que aconteceu antes de os problemas começarem.
/// </summary>
public static class Timeline
{
    const int MaxPerSource = 300;

    public static List<TimelineEvent> Build(int days = 30)
    {
        var window = TimeSpan.FromDays(days);
        // as mudanças leem-se com mais 3 dias: o primeiro crash do período pode ter vindo de algo instalado um pouco antes
        var changes = window + TimeSpan.FromDays(3);
        var list = new List<TimelineEvent>();
        var since = DateTime.Now - changes;

        foreach (var e in ChangeLog.All.Where(e => e.When >= since))
            list.Add(new(e.When, TimelineKind.Rigwise, e.Title, e.Undone ? Loc.T("desfeito") : e.Detail, "Rigwise"));

        Add(list, "System", "Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and (EventID=1001)", window,
            r => new(When(r), TimelineKind.Crash, Loc.T("Ecrã azul"), Code(r), Loc.T("Registos do Windows")));
        Add(list, "System", "Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41)", window,
            r => new(When(r), TimelineKind.Crash, Loc.T("O PC desligou-se de repente"), "", Loc.T("Registos do Windows")));
        Add(list, "Application", "Provider[@Name='Application Error'] and (EventID=1000)", window,
            r => new(When(r), TimelineKind.AppCrash, Loc.T($"{Prop(r, 0)} crashou"), "", Loc.T("Registos do Windows")));

        // drivers de terceiros instalados ou atualizados (os que vêm com o Windows ficam de fora: são centenas)
        Add(list, "Microsoft-Windows-Kernel-PnP/Configuration", "EventID=400", changes, r =>
        {
            var d = Data(r);
            if (d.GetValueOrDefault("DriverInbox") == "true" || d.GetValueOrDefault("DeviceUpdated") != "true") return null;
            return new(When(r), TimelineKind.Driver,
                Loc.T($"Driver {d.GetValueOrDefault("DriverProvider")} {d.GetValueOrDefault("DriverVersion")}").Trim(),
                d.GetValueOrDefault("DriverName") ?? "", Loc.T("Registos do Windows"));
        });

        Add(list, "System", "Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19)", changes, r =>
        {
            var title = Prop(r, 0);
            if (Regex.IsMatch(title, "Defender|Security Intelligence|Informações de Segurança", RegexOptions.IgnoreCase)) return null; // diárias
            var store = Regex.Match(title, @"^9\w{11}-(.+)$"); // apps da Loja: "9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime"
            return store.Success
                ? new(When(r), TimelineKind.StoreApp, Loc.T($"App da Loja atualizada: {store.Groups[1].Value}"), "", "Windows Update")
                : new(When(r), TimelineKind.Update, title, "", "Windows Update");
        });

        Add(list, "Application", "Provider[@Name='MsiInstaller'] and (EventID=11707)", changes, r =>
        {
            var m = Regex.Match(Prop(r, 0), @"Product:\s*(.+?)\s*--");
            return new(When(r), TimelineKind.Program, Loc.T($"Instalado: {(m.Success ? m.Groups[1].Value : Prop(r, 0))}"), "", Loc.T("Registos do Windows"));
        });

        // o mesmo driver para vários dispositivos no mesmo minuto conta uma vez
        return list.GroupBy(e => (e.Kind, e.Title, e.When.ToString("yyyyMMddHHmm"))).Select(g => g.First())
                   .OrderByDescending(e => e.When).ToList();
    }

    /// <summary>
    /// Para o primeiro ecrã azul e o primeiro "desligou-se de repente", as mudanças nos 3 dias antes.
    /// (Crashes de apps e atualizações de apps da Loja ficam de fora: são tantos que tudo pareceria suspeito.)
    /// </summary>
    public static List<Suspect> Suspects(IReadOnlyList<TimelineEvent> events, int daysBefore = 3)
    {
        var result = new List<Suspect>();
        var changes = events.Where(e => e.Kind is TimelineKind.Driver or TimelineKind.Update or TimelineKind.Program or TimelineKind.Rigwise).ToList();
        foreach (var first in events.Where(e => e.Kind == TimelineKind.Crash).GroupBy(e => e.Title).Select(g => g.MinBy(e => e.When)!))
            foreach (var c in changes.Where(c => c.When < first.When && first.When - c.When <= TimeSpan.FromDays(daysBefore)))
            {
                var gap = first.When - c.When;
                var when = gap.TotalHours < 24 ? Loc.T($"{Math.Max(1, (int)gap.TotalHours)} h") : Loc.T($"{(int)gap.TotalDays} dia(s)");
                result.Add(new(c, first, Loc.T($"\"{first.Title}\" aconteceu pela primeira vez neste período {when} depois.")));
            }
        // cada mudança uma vez (a do crash mais próximo); drivers primeiro: são a causa mais comum de ecrãs azuis
        return result.GroupBy(r => r.Change).Select(g => g.MinBy(r => r.FirstCrash.When - r.Change.When)!).OrderBy(s => s.Change.Kind == TimelineKind.Driver ? 0 : s.Change.Kind == TimelineKind.Update ? 1 : 2)
                     .ThenBy(s => s.FirstCrash.When - s.Change.When).ToList();
    }

    static void Add(List<TimelineEvent> list, string log, string filter, TimeSpan window, Func<EventRecord, TimelineEvent?> map)
    {
        try
        {
            foreach (var r in EventLogCheck.Query(log, filter, window).Take(MaxPerSource))
                using (r)
                    if (map(r) is { } e) list.Add(e);
        }
        catch { } // registo inexistente ou sem acesso: fica de fora
    }

    static DateTime When(EventRecord r) => r.TimeCreated ?? DateTime.MinValue;
    static string Prop(EventRecord r, int i) => r.Properties.Count > i ? r.Properties[i].Value?.ToString() ?? "" : "";

    static string Code(EventRecord r)
    {
        var m = Regex.Match(string.Join(" ", r.Properties.Select(p => p.Value?.ToString())), @"0x([0-9a-fA-F]{1,16})");
        return m.Success && ulong.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var c)
            ? CrashDumps.CodeName((uint)c) : "";
    }

    static Dictionary<string, string> Data(EventRecord r)
    {
        try
        {
            return XDocument.Parse(r.ToXml()).Descendants().Where(x => x.Name.LocalName == "Data" && x.Attribute("Name") != null)
                .ToDictionary(x => x.Attribute("Name")!.Value, x => x.Value);
        }
        catch { return new(); }
    }
}
