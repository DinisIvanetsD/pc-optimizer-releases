using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Monitoring;

public enum ObservationKind { Cpu, Disk, Network, Gpu, Ram, App, Nothing }

/// <summary>O que se viu, porque pode ter causado o engasgo e o que testar a seguir.</summary>
public sealed record Observation(ObservationKind Kind, string Text);

/// <summary>
/// Compara o momento do engasgo (5 s antes da marca até 2 s depois) com o resto da gravação.
/// Diz o que mudou e sugere o próximo teste, sem afirmar um culpado.
/// </summary>
public static class RecordingAnalyzer
{
    public static readonly TimeSpan WindowBefore = TimeSpan.FromSeconds(5), WindowAfter = TimeSpan.FromSeconds(2);

    public static List<Observation> Analyze(Recording rec)
    {
        var inWin = rec.Samples.Where(s => s.Time >= rec.Mark - WindowBefore && s.Time <= rec.Mark + WindowAfter).ToList();
        var rest = rec.Samples.Except(inWin).ToList();
        var obs = new List<Observation>();
        if (inWin.Count == 0 || rest.Count < 5)
            return new() { new(ObservationKind.Nothing, Loc.T("A gravação é curta demais para comparar. Deixa gravar pelo menos 30 segundos antes de carregar em \"Aconteceu agora\".")) };

        static double Median(IEnumerable<double> v)
        {
            var a = v.OrderBy(x => x).ToArray();
            return a.Length == 0 ? 0 : a[a.Length / 2];
        }

        var top = inWin.SelectMany(s => s.TopApps).Select(AppName).GroupBy(n => n).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        var usual = rest.SelectMany(s => s.TopApps).Select(AppName).ToHashSet();
        var newcomer = top.FirstOrDefault(n => !usual.Contains(n));
        var suspect = newcomer ?? top.FirstOrDefault();

        // processador
        var cpu = inWin.Max(s => s.Cpu);
        var cpuNormal = Median(rest.Select(s => s.Cpu));
        if (cpu >= 85 && cpu - cpuNormal >= 25)
            obs.Add(new(ObservationKind.Cpu, Loc.T($"O processador subiu para {cpu:N0}% (normalmente {cpuNormal:N0}%).") +
                (suspect != null ? Loc.T($" O que mais gastava era {suspect}.") : "") +
                Loc.T(" Repete o teste com essa app fechada ou com menos programas abertos.")));

        // disco
        var disk = inWin.Max(s => s.Disk);
        var diskNormal = Median(rest.Select(s => s.Disk));
        if (disk >= 80 && disk - diskNormal >= 40)
            obs.Add(new(ObservationKind.Disk, Loc.T($"O disco esteve {disk:N0}% ocupado (normalmente {diskNormal:N0}%).") +
                Loc.T(" Pode ser uma atualização, o antivírus ou um jogo a carregar dados. Repete com o Windows Update em pausa (modo jogo) ou com o jogo num SSD.")));

        // rede
        var pings = rest.Where(s => s.PingMs.HasValue).Select(s => (double)s.PingMs!.Value).ToList();
        var lost = inWin.Count(s => s.PingMs is null);
        var lostBefore = rest.Count(s => s.PingMs is null);
        if (pings.Count >= 5)
        {
            var pingNormal = Median(pings);
            var peak = inWin.Where(s => s.PingMs.HasValue).Select(s => s.PingMs!.Value).DefaultIfEmpty(0).Max();
            if (lost >= 3 && lost > lostBefore * inWin.Count / Math.Max(1, rest.Count) + 1)
                obs.Add(new(ObservationKind.Network, Loc.T($"Perderam-se {lost} pings durante o engasgo (até {rec.PingTarget}).") +
                    Loc.T(" A ligação falhou nesse momento. Usa \"Onde está o lag?\" na aba Rede para ver se é em casa, no operador ou no servidor.")));
            else if (peak >= pingNormal + 60)
                obs.Add(new(ObservationKind.Network, Loc.T($"O ping subiu para {peak} ms (normalmente {pingNormal:N0} ms).") +
                    Loc.T(" Pode ser alguém a fazer downloads na mesma rede ou o Wi-Fi. Repete com cabo ou com os downloads parados.")));
        }

        // placa gráfica
        var gpu = inWin.Where(s => s.GpuLoad.HasValue).Select(s => s.GpuLoad!.Value).DefaultIfEmpty(-1).Max();
        var temp = inWin.Where(s => s.GpuTempC.HasValue).Select(s => s.GpuTempC!.Value).DefaultIfEmpty(-1).Max();
        if (temp >= 83)
            obs.Add(new(ObservationKind.Gpu, Loc.T($"A placa gráfica chegou a {temp} °C. A partir de 83 °C baixa a velocidade para arrefecer.") +
                Loc.T(" Limpa o pó e confirma a ventilação da caixa, e repete o teste.")));
        else if (gpu >= 97)
            obs.Add(new(ObservationKind.Gpu, Loc.T($"A placa gráfica estava no máximo ({gpu}%).") +
                Loc.T(" O jogo está a pedir mais do que ela dá: baixa a qualidade gráfica ou limita os FPS e repete.")));

        // memória
        var ramMin = inWin.Min(s => s.RamFreeGb);
        if (ramMin < 1.0)
            obs.Add(new(ObservationKind.Ram, Loc.T($"Restavam só {ramMin:N1} GB de RAM livre.") +
                Loc.T(" Quando a RAM acaba, o Windows usa o disco e tudo engasga. Fecha o browser e as apps de fundo (vê a aba Monitor) e repete.")));

        // app que só apareceu no engasgo
        if (newcomer != null && obs.All(o => o.Kind != ObservationKind.Cpu))
            obs.Add(new(ObservationKind.App, Loc.T($"\"{newcomer}\" só apareceu entre as apps que mais gastavam durante o engasgo.") +
                Loc.T(" Pode ser uma tarefa de fundo dessa app. Repete com ela fechada.")));

        if (obs.Count == 0)
            obs.Add(new(ObservationKind.Nothing, Loc.T("Nada fora do normal no processador, disco, rede, placa gráfica e memória.") +
                Loc.T(" Pode ser algo que estas medições não apanham (por exemplo, um único fotograma lento). Grava outra vez quando acontecer, para ver se se repete.")));
        return obs;
    }

    /// <summary>"chrome 23%" → "chrome".</summary>
    static string AppName(string top) => top.LastIndexOf(' ') is var i and > 0 ? top[..i] : top;
}
