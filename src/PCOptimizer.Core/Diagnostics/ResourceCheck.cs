using System.Management;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Diagnostics;

/// <summary>Verifica RAM, memória virtual e CPU, e encontra as apps que mais gastam.</summary>
public sealed class ResourceCheck : IDiagnosticCheck
{
    const string RamCat = "Memória RAM";
    const string CpuCat = "Processador";

    public string Name => Loc.T("Memória e CPU");

    public async Task<IEnumerable<Finding>> RunAsync(CancellationToken ct)
    {
        var findings = new List<Finding>();
        var cpu = new CpuSampler();
        var apps = await ProcessSampler.SampleAsync(TimeSpan.FromSeconds(2), ct);
        var cpuTotal = cpu.Sample();
        var mem = SystemInfo.GetMemory();

        var visible = apps.Where(a => !ProcessSampler.Ignored.Contains(a.Name)).ToList();
        var byRam = visible.OrderByDescending(a => a.PrivateBytes).ToList();
        string TopRam(int n) => string.Join(", ",
            byRam.Take(n).Select(a => $"{a.DisplayName} ({SystemInfo.FormatBytes(a.PrivateBytes)})"));

        // ---- RAM ----
        var ramProblems = findings.Count;
        if (mem.LoadPercent >= 90)
        {
            findings.Add(new Finding
            {
                Category = RamCat, Severity = Severity.Critical,
                Title = Loc.T($"RAM quase cheia ({mem.LoadPercent}%)"),
                Description = Loc.T($"Estás a usar {mem.UsedGb:N1} de {mem.TotalGb:N1} GB. O Windows começa a usar o disco como ") +
                              Loc.T($"memória e o PC fica muito lento ou \"bugado\". As apps que mais gastam: {TopRam(4)}."),
                Actions = byRam.Take(2).Where(a => !ProcessSampler.Protected.Contains(a.Name))
                    .Select(a => ProcessSampler.KillAction(a.Name)).ToList(),
            });
        }
        else if (mem.LoadPercent >= 80)
        {
            findings.Add(new Finding
            {
                Category = RamCat, Severity = Severity.Warning,
                Title = Loc.T($"RAM muito ocupada ({mem.LoadPercent}%)"),
                Description = Loc.T($"Estás a usar {mem.UsedGb:N1} de {mem.TotalGb:N1} GB. Se abrires mais coisas, o PC vai abrandar. ") +
                              Loc.T($"As apps que mais gastam: {TopRam(4)}."),
            });
        }

        if (mem.CommitPercent >= 90)
        {
            findings.Add(new Finding
            {
                Category = RamCat, Severity = Severity.Critical,
                Title = Loc.T($"Memória virtual quase esgotada ({mem.CommitPercent:N0}%)"),
                Description = Loc.T("A RAM mais o ficheiro de paginação estão quase cheios. As apps podem crashar ou recusar-se a abrir. ") +
                              Loc.T("Fecha apps ou deixa o Windows gerir automaticamente o tamanho do ficheiro de paginação."),
            });
        }

        var hogLimit = Math.Max(2L << 30, (long)(mem.TotalBytes * 0.2));
        foreach (var hog in byRam.Where(a => a.PrivateBytes >= hogLimit).Take(3))
        {
            var f = new Finding
            {
                Category = RamCat, Severity = Severity.Warning,
                Title = Loc.T($"{hog.DisplayName} está a usar {SystemInfo.FormatBytes(hog.PrivateBytes)} de RAM"),
                Description = Loc.T($"É {100.0 * hog.PrivateBytes / mem.TotalBytes:N0}% da memória do PC. Se esta app não devia ") +
                              Loc.T("precisar de tanto, pode ter um memory leak. Reiniciá-la liberta a memória. ") +
                              Loc.T("A aba Monitor deteta automaticamente apps cuja memória sobe sem parar."),
            };
            if (!ProcessSampler.Protected.Contains(hog.Name)) f.Actions.Add(ProcessSampler.KillAction(hog.Name));
            findings.Add(f);
        }

        if (mem.TotalGb < 7.5)
        {
            findings.Add(new Finding
            {
                Category = RamCat, Severity = Severity.Info,
                Title = Loc.T($"Só tens {mem.TotalGb:N0} GB de RAM"),
                Description = Loc.T("Hoje em dia o Windows 11 com um browser já usa 6–8 GB. Para jogos e edição recomenda-se 16 GB ou mais. ") +
                              Loc.T("Mais RAM é das melhorias mais baratas e eficazes."),
            });
        }

        if (findings.Count == ramProblems)
        {
            findings.Add(new Finding
            {
                Category = RamCat, Severity = Severity.Ok,
                Title = Loc.T($"Memória RAM OK ({mem.LoadPercent}% em uso)"),
                Description = Loc.T($"{mem.UsedGb:N1} de {mem.TotalGb:N1} GB em uso. Maiores consumidores: {TopRam(3)}."),
            });
        }

        // ---- CPU ----
        var cpuProblems = findings.Count;
        var byCpu = visible.OrderByDescending(a => a.CpuPercent).ToList();
        if (cpuTotal >= 90)
        {
            findings.Add(new Finding
            {
                Category = CpuCat, Severity = Severity.Warning,
                Title = Loc.T($"CPU sobrecarregada ({cpuTotal:N0}%)"),
                Description = Loc.T("O processador está quase no máximo. O que está a gastar mais: ") +
                              string.Join(", ", byCpu.Take(3).Select(a => $"{a.DisplayName} ({a.CpuPercent:N0}%)")) + ".",
            });
        }

        foreach (var app in byCpu.Where(a => a.CpuPercent >= 40).Take(2))
        {
            var f = new Finding
            {
                Category = CpuCat, Severity = Severity.Warning,
                Title = Loc.T($"{app.DisplayName} está a usar {app.CpuPercent:N0}% da CPU"),
                Description = Loc.T("Se não estás a usar esta app ativamente (render, jogo, compilação), ela pode estar presa num ciclo. ") +
                              Loc.T("Fechá-la e voltar a abri-la costuma resolver."),
            };
            if (!ProcessSampler.Protected.Contains(app.Name)) f.Actions.Add(ProcessSampler.KillAction(app.Name));
            findings.Add(f);
        }

        if (cpuTotal >= 60 && ProcessorPerformance() is double perf && perf < 70)
        {
            findings.Add(new Finding
            {
                Category = CpuCat, Severity = Severity.Warning,
                Title = Loc.T("A CPU está a trabalhar abaixo da velocidade normal"),
                Description = Loc.T($"Com carga alta, a CPU está a {perf:N0}% da velocidade base. Isto acontece com sobreaquecimento ") +
                              Loc.T("(pó, pasta térmica seca, ventoinha avariada) ou com um plano de energia de poupança."),
            });
        }

        if (findings.Count == cpuProblems)
        {
            findings.Add(new Finding
            {
                Category = CpuCat, Severity = Severity.Ok,
                Title = Loc.T($"Processador OK ({cpuTotal:N0}% em uso)"),
                Description = Loc.T("Nenhuma app está a gastar CPU de forma anormal neste momento."),
            });
        }

        return findings;
    }

    static double? ProcessorPerformance()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2",
                "SELECT PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'");
            foreach (ManagementObject o in searcher.Get())
            {
                using (o) return Convert.ToDouble(o["PercentProcessorPerformance"]);
            }
        }
        catch { }
        return null;
    }
}
