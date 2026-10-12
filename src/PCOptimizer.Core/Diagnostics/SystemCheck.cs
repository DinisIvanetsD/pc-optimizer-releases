using PCOptimizer.Core.Optimization;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Diagnostics;

/// <summary>Coisas gerais: tempo sem reiniciar, atualizações pendentes, plano de energia, temporários, arranque.</summary>
public sealed class SystemCheck : IDiagnosticCheck
{
    const string Cat = "Sistema";

    public string Name => "Sistema";

    public async Task<IEnumerable<Finding>> RunAsync(CancellationToken ct)
    {
        var findings = new List<Finding>();

        var up = SystemInfo.Uptime;
        if (up.TotalDays >= 7)
        {
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Info,
                Title = Loc.T($"O PC não reinicia há {up.TotalDays:N0} dias"),
                Description = Loc.T("Com o \"Arranque rápido\" do Windows, Encerrar não limpa a memória nem os drivers. Só \"Reiniciar\" o faz. ") +
                              Loc.T("Reiniciar de vez em quando resolve muitas lentidões e bugs estranhos."),
            });
        }

        if (Reg.KeyExists(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") ||
            Reg.KeyExists(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
        {
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Warning,
                Title = Loc.T("Há atualizações à espera de reinício"),
                Description = Loc.T("Enquanto não reinicias, o Windows pode estar mais lento e alguns componentes ficam a meio da atualização."),
            });
        }

        var active = await PowerPlanTweak.GetActiveAsync();
        if (active == PowerPlanTweak.PowerSaver)
        {
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Warning,
                Title = Loc.T("O plano de energia está em \"Poupança de energia\""),
                Description = Loc.T("A CPU está limitada para gastar menos energia, o que deixa tudo mais lento. Escolhe um perfil na aba Otimização."),
                Actions = { new FixAction(Loc.T("Ir para Otimização"), () => { UiHooks.Navigate?.Invoke("optimize"); return Task.FromResult(""); }) },
            });
        }

        var tempBytes = TempCleaner.MeasureBytes();
        if (tempBytes >= 1L << 30)
        {
            findings.Add(new Finding
            {
                Category = Cat, Severity = tempBytes >= 5L << 30 ? Severity.Warning : Severity.Info,
                Title = Loc.T($"{SystemInfo.FormatBytes(tempBytes)} em ficheiros temporários"),
                Description = Loc.T("São restos de instalações e apps. Apagar os que têm mais de 1 dia é seguro."),
                Actions = { new FixAction(Loc.T("Limpar ficheiros temporários"), Tools.CleanTemp) },
            });
        }

        var exhaustion = Network.ConnectionWatcher.PastPortExhaustion(30).Where(t => t >= EventLogCheck.Since).ToList();

        // com a correção já aplicada, só contam as quebras depois dela
        var fixedAt = PortFixTime();
        if (fixedAt is DateTime since && exhaustion.Count > 0)
        {
            var before = exhaustion.Count(t => t < since);
            exhaustion = exhaustion.Where(t => t >= since).ToList();
            if (exhaustion.Count < 2 && before > 0)
            {
                findings.Add(new Finding
                {
                    Category = Loc.T("Rede"), Severity = Severity.Ok,
                    Title = Loc.T("Quebras de internet por falta de portas: corrigido ✓"),
                    Description = Loc.T($"Houve {before} quebra(s) antes da correção \"Mais portas de rede\" e ") +
                                  (exhaustion.Count == 0 ? Loc.T("nenhuma desde então.") : Loc.T("só 1 desde então.")) +
                                  Loc.T(" O vigia da aba Rede continua atento."),
                });
            }
        }
        if (exhaustion.Count >= 2)
        {
            findings.Add(new Finding
            {
                Category = Loc.T("Rede"), Severity = exhaustion.Count >= 5 ? Severity.Critical : Severity.Warning,
                Title = fixedAt is null
                    ? Loc.T($"A internet \"caiu\" {exhaustion.Count} vezes por falta de portas de rede")
                    : Loc.T($"A internet ainda \"cai\" por falta de portas ({exhaustion.Count} vezes desde a correção)"),
                Description = Loc.T($"Nos últimos 30 dias o Windows ficou {exhaustion.Count} vezes sem portas de rede livres (a última em {exhaustion[0]:dd/MM 'às' HH:mm}). ") +
                              Loc.T("Quando isto acontece, nenhuma app consegue ligar-se durante uns segundos: parece que a internet foi abaixo. ") +
                              Loc.T("A causa é uma app que abre ligações a mais. O vigia da aba Rede apanha qual é, e o ajuste \"Mais portas de rede\" evita as quebras."),
                Actions = { new FixAction(Loc.T("Ver na aba Rede"), () => { UiHooks.Navigate?.Invoke("network"); return Task.FromResult(""); }) },
            });
        }

        var startup = StartupManager.List().Count(i => i.Enabled);
        if (startup >= 8)
        {
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Warning,
                Title = Loc.T($"{startup} programas arrancam com o Windows"),
                Description = Loc.T("Cada um atrasa o arranque e fica a gastar RAM e CPU em segundo plano. Desativa os que não precisas logo ao ligar o PC."),
                Actions = { new FixAction(Loc.T("Ver programas de arranque"), () => { UiHooks.Navigate?.Invoke("startup"); return Task.FromResult(""); }) },
            });
        }

        if (findings.Count == 0)
        {
            findings.Add(new Finding
            {
                Category = Cat, Severity = Severity.Ok,
                Title = Loc.T("Sistema OK"),
                Description = Loc.T($"Sem atualizações pendentes, {startup} programas no arranque e pouco lixo temporário."),
            });
        }
        return findings;
    }

    /// <summary>
    /// Quando foi aplicada a correção "Mais portas de rede" (null = não está aplicada).
    /// Se não houver registo no histórico, conta a partir do último arranque.
    /// </summary>
    static DateTime? PortFixTime()
    {
        try
        {
            if (TweakCatalog.Get("net.dynports").IsAppliedAsync().GetAwaiter().GetResult() != true) return null;
        }
        catch { return null; }
        var logged = History.ChangeLog.All.FirstOrDefault(e => e.Undo == History.UndoKind.Tweak && !e.Undone &&
                                                                 e.Data.GetValueOrDefault("id") == "net.dynports");
        return logged?.When ?? DateTime.Now - SystemInfo.Uptime;
    }
}
