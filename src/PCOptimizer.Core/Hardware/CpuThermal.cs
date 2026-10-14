using Microsoft.Win32;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Hardware;

/// <summary>
/// O processador agora: velocidade real, se está a ser limitado e porquê, e a temperatura quando se consegue ler.
/// </summary>
/// <param name="SpeedGHz">Velocidade real agora (pode passar a base com o turbo).</param>
/// <param name="LimitPercent">Teto que o Windows/firmware põe ao processador (100 = sem limite).</param>
/// <param name="ThermalLimitPercent">Teto por calor das zonas térmicas ACPI (100 = sem limite).</param>
/// <param name="ThermalThrottling">Uma zona térmica diz que está a abrandar o processador por calor.</param>
/// <param name="TempC">Temperatura do processador, se houver forma de a ler.</param>
public sealed record CpuState(double? SpeedGHz, int LimitPercent, int ThermalLimitPercent, bool ThermalThrottling, int? TempC);

/// <summary>
/// Lê os contadores do Windows (sem administrador e sem drivers). A temperatura do processador vem das zonas térmicas
/// ACPI quando o portátil as preenche, ou do LibreHardwareMonitor quando a app está como administrador e o driver
/// PawnIO já está instalado (a app não o instala).
/// </summary>
public static class CpuThermal
{
    /// <summary>A partir daqui a maioria dos processadores começa a abrandar para arrefecer.</summary>
    public const int HotC = 95;

    public static CpuState Read(double baseGHz)
    {
        double? speed = null;
        var limit = 100;
        Wmi.Each("SELECT PercentProcessorPerformance, PercentPerformanceLimit FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name = '_Total'", o =>
        {
            var perf = Convert.ToDouble(o["PercentProcessorPerformance"] ?? 0);
            if (perf > 0 && baseGHz > 0) speed = baseGHz * perf / 100;
            limit = Convert.ToInt32(o["PercentPerformanceLimit"] ?? 100);
        });

        var thermalLimit = 100;
        var throttling = false;
        int? temp = null;
        Wmi.Each("SELECT Temperature, PercentPassiveLimit, ThrottleReasons FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation", o =>
        {
            var passive = Convert.ToInt32(o["PercentPassiveLimit"] ?? 100);
            thermalLimit = Math.Min(thermalLimit, passive);
            // ThrottleReasons: 1 = por calor, 2 = por limite de energia (só o primeiro interessa aqui)
            if ((Convert.ToInt32(o["ThrottleReasons"] ?? 0) & 1) != 0 || passive < 100) throttling = true;
            var k = Convert.ToInt32(o["Temperature"] ?? 0); // em Kelvin; 0 = o firmware não preenche
            if (k > 273 && k < 400) temp = Math.Max(temp ?? 0, k - 273);
        });

        temp ??= LibreTemp();
        return new CpuState(speed, Math.Clamp(limit, 0, 100), Math.Clamp(thermalLimit, 0, 100), throttling || temp >= HotC, temp);
    }

    /// <summary>Aviso quando o processador está a perder velocidade por calor (ou limitado por outra razão).</summary>
    public static string? Warning(CpuState s)
    {
        if (s.ThermalThrottling)
            return (s.TempC is int t ? Loc.T($"O processador está a {t} °C e a abrandar para arrefecer. ") : Loc.T("O processador está a abrandar para arrefecer. ")) +
                   Loc.T("Confirma que as saídas de ar não estão tapadas (num portátil, não o uses em cima da cama ou do colo), limpa o pó e, se continuar, a pasta térmica pode precisar de ser trocada.");
        if (s.LimitPercent < 100)
            return Loc.T($"O processador está limitado a {s.LimitPercent}% da velocidade. Costuma ser o modo de poupança de energia ou o portátil fora da tomada; se acontecer ligado à tomada e em modo de desempenho, pode ser calor.");
        return null;
    }

    /// <summary>Se o PawnIO está instalado (o driver que o LibreHardwareMonitor usa para ler os sensores do CPU).</summary>
    public static bool PawnIoInstalled
    {
        get
        {
            try { using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"); return k != null; }
            catch { return false; }
        }
    }

    static int? LibreTemp()
    {
        if (!Admin.IsElevated || !PawnIoInstalled) return null;
        try
        {
            var c = new LibreHardwareMonitor.Hardware.Computer { IsCpuEnabled = true };
            c.Open();
            try
            {
                foreach (var h in c.Hardware)
                {
                    h.Update();
                    var t = h.Sensors.FirstOrDefault(x => x.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature && x.Name is "CPU Package" or "Core Max" && x.Value > 0)
                         ?? h.Sensors.FirstOrDefault(x => x.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature && x.Value > 0 && !x.Name.Contains("Distance"));
                    if (t?.Value is float v) return (int)Math.Round(v);
                }
            }
            finally { c.Close(); }
        }
        catch { }
        return null;
    }
}
