using System.Globalization;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Hardware;

public sealed record GpuSensor(string Name, int? TempC, int? FanPercent, double? PowerW, int? LoadPercent);

/// <summary>
/// Sensores que o Windows deixa ler sem instalar drivers: as placas NVIDIA (nvidia-smi, vem com o driver).
/// As ventoinhas da caixa e do CPU só se leem com um driver de kernel, que esta app não instala.
/// </summary>
public static class Sensors
{
    /// <summary>A partir daqui a placa NVIDIA começa a baixar a velocidade para arrefecer.</summary>
    public const int GpuHotC = 83;

    public static async Task<List<GpuSensor>> NvidiaAsync()
    {
        try
        {
            var r = await Shell.RunAsync("nvidia-smi", "--query-gpu=name,temperature.gpu,fan.speed,power.draw,utilization.gpu --format=csv,noheader,nounits", 10_000);
            return r.Ok ? ParseNvidiaSmi(r.StdOut) : new();
        }
        catch { return new(); } // sem NVIDIA (ou sem o nvidia-smi)
    }

    /// <summary>"NVIDIA GeForce RTX 3060 Ti, 85, 99, 149.18, 58" (os valores em falta vêm como "[N/A]").</summary>
    public static List<GpuSensor> ParseNvidiaSmi(string output)
    {
        var list = new List<GpuSensor>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split(',').Select(x => x.Trim()).ToArray();
            if (p.Length < 5 || p[0].Length == 0) continue;
            int? I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
            double? D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            list.Add(new GpuSensor(p[0], I(p[1]), I(p[2]), D(p[3]), I(p[4])));
        }
        return list;
    }

    public static string? GpuWarning(GpuSensor g) => g.TempC >= GpuHotC
        ? Loc.T($"A placa gráfica está a {g.TempC} °C") + (g.FanPercent is int f ? Loc.T($" com a ventoinha a {f}%") : "") +
          Loc.T(". Acima de 83 °C perde desempenho para arrefecer. Limpa o pó, confirma que a caixa tem ar a entrar e a sair e, se não melhorar, a pasta térmica pode precisar de ser trocada.")
        : null;
}
