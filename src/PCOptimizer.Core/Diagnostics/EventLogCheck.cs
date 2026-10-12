using System.Diagnostics.Eventing.Reader;
using System.Text.RegularExpressions;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Diagnostics;

/// <summary>Lê o Visualizador de Eventos: ecrãs azuis, desligares súbitos, erros de hardware e apps que crasham.</summary>
public sealed class EventLogCheck : IDiagnosticCheck
{
    const string Cat = "Estabilidade";
    const long Last30DaysMs = 30L * 24 * 60 * 60 * 1000;

    /// <summary>Início da contagem: os últimos 30 dias, ou a data do "Começar do zero" se for mais recente.</summary>
    public static DateTime Since
    {
        get
        {
            var d30 = DateTime.Now.AddDays(-30);
            return Settings.AppSettings.Current.StabilitySince is DateTime s && s > d30 ? s : d30;
        }
    }

    /// <summary>"nos últimos 30 dias" ou "desde 10/10".</summary>
    public static string PeriodText => Settings.AppSettings.Current.StabilitySince is DateTime s && s > DateTime.Now.AddDays(-30)
        ? Loc.T($"desde {s:dd/MM HH:mm}")
        : Loc.T("nos últimos 30 dias");

    public string Name => Loc.T("Crashes e erros de hardware");

    internal static readonly Dictionary<ulong, (string Name, string Hint, bool Memory)> BugChecks = new()
    {
        [0x1A] = ("MEMORY_MANAGEMENT", Loc.T("problema de RAM ou de driver"), true),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", Loc.T("RAM, driver ou antivírus"), true),
        [0x19] = ("BAD_POOL_HEADER", Loc.T("driver ou RAM"), true),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", Loc.T("driver ou RAM"), true),
        [0x4E] = ("PFN_LIST_CORRUPT", Loc.T("RAM ou driver"), true),
        [0x7A] = ("KERNEL_DATA_INPAGE_ERROR", Loc.T("falha a ler a memória do disco: disco, cabo SATA ou RAM"), true),
        [0x77] = ("KERNEL_STACK_INPAGE_ERROR", Loc.T("disco ou RAM"), true),
        [0xCE] = ("DRIVER_UNLOADED_WITHOUT_CANCELLING_PENDING_OPERATIONS", Loc.T("driver com defeito"), false),
        [0x3D] = ("INTERRUPT_EXCEPTION_NOT_HANDLED", Loc.T("driver com defeito"), false),        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", Loc.T("erro de hardware: CPU, RAM, overclock ou temperatura"), true),
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", Loc.T("normalmente um driver com defeito"), false),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", Loc.T("driver com defeito (muitas vezes de rede ou da gráfica)"), false),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", Loc.T("driver ou firmware do SSD bloqueado"), false),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", Loc.T("driver ou ficheiro do sistema"), false),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", Loc.T("driver com defeito"), false),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", Loc.T("driver com defeito"), false),
        [0xC2] = ("BAD_POOL_CALLER", Loc.T("driver com defeito"), false),
        [0xEF] = ("CRITICAL_PROCESS_DIED", Loc.T("ficheiros do sistema danificados"), false),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", Loc.T("driver com problemas ao suspender/acordar"), false),
        [0x116] = ("VIDEO_TDR_FAILURE", Loc.T("driver ou placa gráfica"), false),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", Loc.T("CPU: overclock, temperatura ou BIOS"), false),
    };

    public Task<IEnumerable<Finding>> RunAsync(CancellationToken ct)
    {
        var findings = new List<Finding>();
        BlueScreens(findings);
        UnexpectedShutdowns(findings);
        HardwareErrors(findings);
        AppCrashes(findings);

        if (findings.Count == 0)
        {
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Ok,
                Title = Loc.T("Sem crashes ") + PeriodText,
                Description = Loc.T("Não há ecrãs azuis, desligares inesperados nem erros de hardware registados."),
            });
        }
        return Task.FromResult<IEnumerable<Finding>>(findings);
    }

    const string BlueScreenFilter = "Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and (EventID=1001)";

    /// <summary>
    /// Ecrãs azuis registados pelo Windows, o mais recente primeiro. Não precisa de administrador
    /// (ao contrário dos minidumps), mas não diz que driver estava a correr.
    /// </summary>
    public static List<CrashDump> BlueScreenEvents(TimeSpan? window = null)
    {
        var list = new List<CrashDump>();
        foreach (var r in window is TimeSpan w ? Query("System", BlueScreenFilter, w) : Query("System", BlueScreenFilter))
        {
            using (r)
            {
                var hex = BugHex(r);
                var p = hex.Skip(1).Concat(new ulong[4]).Take(4).ToArray();
                list.Add(new CrashDump("", r.TimeCreated ?? DateTime.Now, hex.Count > 0 ? (uint)hex[0] : 0, p, null, "", Array.Empty<string>()));
            }
        }
        return list;
    }

    /// <summary>"hoje às 19:02", "ontem às 19:02" ou "a 05/10 às 13:14".</summary>
    public static string WhenText(DateTime t) =>
        t.Date == DateTime.Today ? Loc.T($"hoje às {t:HH:mm}")
        : t.Date == DateTime.Today.AddDays(-1) ? Loc.T($"ontem às {t:HH:mm}")
        : Loc.T($"a {t:dd/MM} às {t:HH:mm}");

    /// <summary>Data fixa ("11/10 às 19:02"): serve de título, que a manutenção usa para não repetir avisos.</summary>
    public static string WhenFixed(DateTime t) => Loc.T($"{t:dd/MM} às {t:HH:mm}");

    static void BlueScreens(List<Finding> findings)
    {
        var events = BlueScreenEvents();
        if (events.Count == 0) return;
        var last = events[0];
        var memoryRelated = events.Any(e => BugChecks.TryGetValue(CrashDumps.BaseCode(e.Code), out var i) && i.Memory);

        // os minidumps dizem também que driver estava a correr, mas só com administrador
        List<CrashDump>? dumps = null;
        var locked = false;
        try { dumps = CrashDumps.ReadAll().Where(d => d.When >= Since).ToList(); }
        catch (UnauthorizedAccessException) { locked = CrashDumps.Count(Since) > 0; }

        var hasDumps = dumps is { Count: > 0 };
        string? guilty = null;
        string verdict;
        if (hasDumps)
        {
            var v = CrashDumps.Verdict(dumps!);
            memoryRelated = v.PointsToRam || (memoryRelated && v.GuiltyDriver is null);
            guilty = v.GuiltyDriver;
            verdict = $"🔎 {v.Title}\n{v.Explanation}";
        }
        else verdict = memoryRelated
            ? Loc.T("Estes erros costumam vir de RAM instável (muitas vezes por causa do XMP/EXPO) ou de um driver. Faz o teste de RAM.")
            : Loc.T("Atualiza os drivers (sobretudo da gráfica, rede e chipset) e verifica os ficheiros do sistema.");

        var shown = hasDumps ? dumps! : events;
        var lines = shown.Take(6).Select(d =>
        {
            var hint = CrashDumps.ParameterHint(d) ?? (BugChecks.TryGetValue(CrashDumps.BaseCode(d.Code), out var i) ? i.Hint : null);
            var who = d.CausedBy is null ? ""
                : CrashDumps.IsWindowsModule(d.CausedBy) ? Loc.T($" — só o Windows ({d.CausedBy}), nenhum driver de terceiros")
                : Loc.T($" — driver {d.CausedBy}");
            return $"• {WhenText(d.When)}  {d.CodeName}{who}" + (hint is null ? "" : $"\n    {hint}");
        });
        var more = shown.Count > 6 ? Loc.T($"\n… e mais {shown.Count - 6}") : "";
        var lockText = locked
            ? Loc.T("\n\n🔒 Para saber que driver estava a correr, a app tem de ler os relatórios de crash: carrega em \"Descobrir o driver\" (pede administrador).")
            : "";

        var f = new Finding
        {
            Category = Cat,
            Severity = events.Count >= 3 ? Severity.Critical : Severity.Warning,
            Title = events.Count == 1
                ? Loc.T($"Ecrã azul a {WhenFixed(last.When)}: {last.CodeName}")
                : Loc.T($"{events.Count} ecrãs azuis {PeriodText} (o último a {WhenFixed(last.When)})"),
            NotifiedElsewhere = true, // o aviso na hora vem do NotifyNewCrashAsync
            // o que cabe nas 2 linhas do cartão: o veredicto dos minidumps, ou então o próprio crash
            Description = hasDumps
                ? verdict + "\n\n" + string.Join("\n", lines) + more + lockText
                : string.Join("\n", lines) + more + "\n\n" + verdict + lockText,
        };
        if (locked)
            f.Actions.Add(new FixAction(Loc.T("Descobrir o driver (administrador)"), () => { UiHooks.RestartAsAdmin?.Invoke(); return Task.FromResult(""); }));
        if (memoryRelated) f.Actions.Add(new FixAction(Loc.T("Testar a RAM (sem reiniciar)"), Tools.OpenRamTest));
        if (guilty != null)
            f.Actions.Add(new FixAction(Loc.T($"Ver drivers ({guilty})"), () => { UiHooks.Navigate?.Invoke("drivers"); return Task.FromResult(""); }));
        f.Actions.Add(new FixAction(Loc.T("Verificar ficheiros do sistema (SFC)"), Tools.SystemFileCheck));
        findings.Add(f);
    }

    static void UnexpectedShutdowns(List<Finding> findings)
    {
        var count = 0;
        foreach (var r in Query("System", "Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41)"))
        {
            using (r) count++;
        }
        if (count < 2) return;

        findings.Add(new Finding
        {
            Category = Cat,
            Severity = count >= 5 ? Severity.Critical : Severity.Warning,
            Title = Loc.T($"O PC desligou-se de repente {count} vezes"),
            Description = Loc.T("O Windows não foi encerrado corretamente. Causas comuns: crash com ecrã azul, sobreaquecimento, ") +
                          Loc.T("fonte de alimentação fraca ou com defeito, overclock instável, ou cortes de energia."),
        });
    }

    static void HardwareErrors(List<Finding> findings)
    {
        int fatal = 0, corrected = 0;
        foreach (var r in Query("System", "Provider[@Name='Microsoft-Windows-WHEA-Logger']"))
        {
            using (r)
            {
                if (r.Id is 18 or 20 or 46) fatal++;
                else corrected++;
            }
        }
        if (fatal > 0)
        {
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Critical,
                Title = Loc.T($"{fatal} erro(s) de hardware graves (WHEA)"),
                Description = Loc.T("A CPU, a RAM ou um dispositivo PCIe reportaram um erro que não conseguiram corrigir. ") +
                              Loc.T("Se tens overclock (incluindo perfis XMP/EXPO da RAM), desliga-o. Depois testa a RAM."),
                Actions = { new FixAction(Loc.T("Testar a RAM (sem reiniciar)"), Tools.OpenRamTest) },
            });
        }
        else if (corrected >= 10)
        {
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Warning,
                Title = Loc.T($"{corrected} erros de hardware corrigidos (WHEA)"),
                Description = Loc.T("O hardware detetou e corrigiu erros. Muitas vezes é inofensivo (ex.: PCIe), mas se tiveres crashes ") +
                              Loc.T("pode indicar overclock instável ou um componente a falhar."),
            });
        }
    }

    static void AppCrashes(List<Finding> findings)
    {
        var crashes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Query("Application", "Provider[@Name='Application Error' or @Name='Application Hang'] and (EventID=1000 or EventID=1002)"))
        {
            using (r)
            {
                var app = r.Properties.Count > 0 ? r.Properties[0].Value?.ToString() : null;
                if (string.IsNullOrWhiteSpace(app)) continue;
                crashes[app] = crashes.GetValueOrDefault(app) + 1;
            }
        }
        foreach (var (app, n) in crashes.Where(kv => kv.Value >= 3).OrderByDescending(kv => kv.Value).Take(3))
        {
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Warning,
                Title = Loc.T($"{app} crashou/bloqueou {n} vezes"),
                Description = Loc.T("Atualiza ou reinstala esta app. Se for um jogo, verifica a integridade dos ") +
                              Loc.T("ficheiros no Steam/Epic e atualiza o driver da gráfica."),
            });
        }
    }

    static IEnumerable<EventRecord> Query(string log, string systemFilter) =>
        Query(log, systemFilter, TimeSpan.FromMilliseconds(Math.Min(Last30DaysMs, (long)(DateTime.Now - Since).TotalMilliseconds)));

    /// <summary>Eventos de um registo do Windows nos últimos <paramref name="window"/> (o mais recente primeiro).</summary>
    internal static IEnumerable<EventRecord> Query(string log, string systemFilter, TimeSpan window)
    {
        var ms = (long)window.TotalMilliseconds;
        var xpath = $"*[System[{systemFilter} and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
        var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
        EventLogReader reader;
        try { reader = new EventLogReader(query); }
        catch (EventLogException) { yield break; }
        using (reader)
        {
            for (var r = reader.ReadEvent(); r != null; r = reader.ReadEvent())
                yield return r;
        }
    }

    /// <summary>Os números hexadecimais do evento: código do erro e depois os 4 parâmetros.</summary>
    static List<ulong> BugHex(EventRecord r)
    {
        var text = string.Join(" ", r.Properties.Select(p => p.Value?.ToString()));
        var ms = Regex.Matches(text, @"0x([0-9a-fA-F]{1,16})");
        if (ms.Count == 0)
        {
            try { ms = Regex.Matches(r.FormatDescription() ?? "", @"0x([0-9a-fA-F]{1,16})"); } catch { }
        }
        return ms.Select(m => Convert.ToUInt64(m.Groups[1].Value, 16)).ToList();
    }
}
