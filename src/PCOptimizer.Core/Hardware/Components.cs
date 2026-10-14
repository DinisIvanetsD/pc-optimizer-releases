using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Hardware;

/// <summary>Uma peça do PC com os detalhes que interessam e, se houver, um aviso (a amarelo) ou uma nota (informação neutra).</summary>
public sealed record Component(string Kind, string Icon, string Name, IReadOnlyList<(string Label, string Value)> Details, string? Warning = null, string? Note = null);

/// <summary>As peças do PC (CPU, GPU, RAM, discos, placa-mãe, rede) lidas pelo WMI. Só lê, não muda nada.</summary>
public static class Components
{
    public static async Task<List<Component>> ListAsync()
    {
        var sensors = await Sensors.NvidiaAsync();
        return await Task.Run(() =>
        {
            var list = new List<Component>();
            Cpu(list);
            Gpu(list, sensors);
            Ram(list);
            Board(list);
            Disks(list);
            Network(list);
            Batteries(list);
            Fans(list, sensors);
            return list;
        });
    }

    static void Cpu(List<Component> list) =>
        Wmi.Each("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor", o =>
        {
            var baseGHz = Convert.ToDouble(o["MaxClockSpeed"]) / 1000;
            var s = CpuThermal.Read(baseGHz);
            var details = new List<(string, string)>
            {
                (Loc.T("Núcleos"), Loc.T($"{o["NumberOfCores"]} ({o["NumberOfLogicalProcessors"]} threads)")),
                (Loc.T("Velocidade base"), $"{baseGHz:0.0#} GHz"),
            };
            if (s.SpeedGHz is double now) details.Add((Loc.T("Velocidade agora"), $"{now:0.0#} GHz"));
            if (s.TempC is int t) details.Add((Loc.T("Temperatura"), $"{t} °C"));
            details.Add((Loc.T("Abrandar por calor"), s.ThermalThrottling ? Loc.T("⚠ Sim, agora") : Loc.T("✓ Não")));
            string? note = s.TempC is null
                ? Loc.T("A temperatura do processador só se lê com o driver PawnIO (pawnio.eu) e a app aberta como administrador. A app não o instala sozinha.")
                : null;
            list.Add(new(Loc.T("Processador"), "🧠", o["Name"]?.ToString()?.Trim() ?? "CPU", details, CpuThermal.Warning(s), note));
        });

    static void Batteries(List<Component> list)
    {
        foreach (var b in Battery.List())
        {
            var details = new List<(string, string)>();
            if (Battery.HealthPercent(b.DesignedMWh, b.FullMWh) is int h) details.Add((Loc.T("Saúde"), Battery.HealthText(h)));
            if (b.DesignedMWh > 0) details.Add((Loc.T("Capacidade de fábrica"), $"{b.DesignedMWh / 1000.0:0.#} Wh"));
            if (b.FullMWh > 0) details.Add((Loc.T("Capacidade agora"), $"{b.FullMWh / 1000.0:0.#} Wh"));
            details.Add((Loc.T("Ciclos de carga"), b.Cycles > 0 ? b.Cycles.ToString() : Loc.T("o portátil não diz")));
            if (b.FullMWh > 0 && b.RemainingMWh > 0)
            {
                var pct = Math.Min(100, (int)Math.Round(100.0 * b.RemainingMWh / b.FullMWh));
                details.Add((Loc.T("Carga"), $"{pct}% · " + (b.Charging ? Loc.T("a carregar") : b.OnAc ? Loc.T("ligado à tomada") : Loc.T("na bateria"))));
            }
            var name = string.Join(" ", new[] { b.Maker, b.Name }.Where(x => x.Length > 0));
            list.Add(new(Loc.T("Bateria"), "🔋", name, details, Battery.Warning(b)));
        }
    }

    static void Gpu(List<Component> list, List<GpuSensor> sensors)
    {
        var vram = VramByName();
        Wmi.Each("SELECT Name, AdapterRAM, DriverVersion, DriverDate FROM Win32_VideoController", o =>
        {
            var name = o["Name"]?.ToString() ?? "GPU";
            // o AdapterRAM do WMI pára nos 4 GB; o registo do driver tem o valor certo
            var bytes = vram.TryGetValue(name, out var v) ? v : Convert.ToInt64(o["AdapterRAM"] ?? 0L);
            var details = new List<(string, string)>();
            if (bytes > 0) details.Add((Loc.T("Memória"), SystemInfo.FormatBytes(bytes)));
            details.Add((Loc.T("Driver"), $"{o["DriverVersion"]}{Date(o["DriverDate"])}"));
            var g = sensors.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (g?.TempC is int t) details.Add((Loc.T("Temperatura"), $"{t} °C"));
            if (g?.LoadPercent is int u) details.Add((Loc.T("Em uso"), $"{u}%" + (g.PowerW is double w ? $" · {w:0} W" : "")));
            list.Add(new(Loc.T("Placa gráfica"), "🎮", name, details, g is null ? null : Sensors.GpuWarning(g)));
        });
    }

    static Dictionary<string, long> VramByName()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames().Where(n => n.All(char.IsDigit)) ?? [])
            {
                using var k = cls!.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is string desc && k.GetValue("HardwareInformation.qwMemorySize") is long size)
                    map[desc] = size;
            }
        }
        catch { }
        return map;
    }

    static void Ram(List<Component> list)
    {
        var sticks = new List<(long Size, int Speed, int Configured, string Part, int Type)>();
        Wmi.Each("SELECT Capacity, Speed, ConfiguredClockSpeed, PartNumber, SMBIOSMemoryType FROM Win32_PhysicalMemory", o =>
            sticks.Add((Convert.ToInt64(o["Capacity"] ?? 0L), Convert.ToInt32(o["Speed"] ?? 0), Convert.ToInt32(o["ConfiguredClockSpeed"] ?? 0),
                        o["PartNumber"]?.ToString()?.Trim() ?? "", Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0))));
        if (sticks.Count == 0) return;
        var s = sticks[0];
        var type = s.Type switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", _ => "RAM" };
        var running = sticks.Min(x => x.Configured > 0 ? x.Configured : x.Speed);
        var rated = sticks.Max(x => Math.Max(x.Speed, RatedSpeed(x.Part) ?? 0));
        var details = new List<(string, string)>
        {
            (Loc.T("Total"), $"{SystemInfo.FormatBytes(sticks.Sum(x => x.Size))} {type} · {Loc.T($"{sticks.Count} módulo(s)")}"),
            (Loc.T("Velocidade"), running > 0 ? $"{running} MT/s" : "?"),
        };
        if (s.Part.Length > 0) details.Add((Loc.T("Modelo"), s.Part));
        list.Add(new(Loc.T("Memória RAM"), "🧩", $"{SystemInfo.FormatBytes(sticks.Sum(x => x.Size))} {type}", details,
            XmpWarning(running, rated)));
    }

    /// <summary>Aviso quando a RAM está mais lenta do que a velocidade para que foi feita (XMP/EXPO desligado na BIOS).</summary>
    public static string? XmpWarning(int running, int rated) =>
        running > 0 && rated > running + 100
            ? Loc.T($"A RAM está a {running} MT/s mas foi feita para {rated} MT/s. Liga o XMP (Intel) ou EXPO (AMD) na BIOS para a pôr à velocidade certa.")
            : null;

    /// <summary>Velocidade de fábrica pelo código do módulo (G.Skill F4-3600…, Corsair …3200C16, Kingston KF436…).</summary>
    public static int? RatedSpeed(string part)
    {
        var m = Regex.Match(part, @"^F[45]-(\d{4})");                 // G.Skill
        if (!m.Success) m = Regex.Match(part, @"^CM[A-Z0-9]+?(\d{4})C\d{2}"); // Corsair
        if (m.Success) return int.Parse(m.Groups[1].Value);
        m = Regex.Match(part, @"^KF[45](\d{2})C");                   // Kingston Fury: 36 = 3600
        return m.Success ? int.Parse(m.Groups[1].Value) * 100 : null;
    }

    static void Board(List<Component> list)
    {
        string? board = null, bios = null;
        Wmi.Each("SELECT Manufacturer, Product FROM Win32_BaseBoard", o => board = $"{o["Manufacturer"]} {o["Product"]}".Trim());
        Wmi.Each("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS", o => bios = $"{o["SMBIOSBIOSVersion"]}{Date(o["ReleaseDate"])}");
        if (board is null) return;
        list.Add(new(Loc.T("Placa-mãe"), "🔌", board, new[] { (Loc.T("BIOS"), bios ?? "?") }));
    }

    static void Disks(List<Component> list)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, MediaType, BusType, Size, HealthStatus FROM MSFT_PhysicalDisk");
            foreach (var o in s.Get())
                using (o)
                {
                    var media = Convert.ToInt32(o["MediaType"] ?? 0) switch { 3 => "HDD", 4 => "SSD", _ => Loc.T("Disco") };
                    if (Convert.ToInt32(o["BusType"] ?? 0) == 17) media = "SSD NVMe";
                    var health = Convert.ToInt32(o["HealthStatus"] ?? 0);
                    list.Add(new(Loc.T("Disco"), "💾", o["FriendlyName"]?.ToString() ?? media, new[]
                    {
                        (Loc.T("Tipo"), $"{media} · {SystemInfo.FormatBytes(Convert.ToDouble(o["Size"] ?? 0UL))}"),
                        (Loc.T("Saúde"), health switch { 0 => Loc.T("✓ Bom"), 1 => Loc.T("⚠ Aviso"), 2 => Loc.T("✕ Com problemas"), _ => "?" }),
                    }, health is 1 or 2 ? Loc.T("O Windows diz que este disco tem problemas. Faz já uma cópia dos teus ficheiros.") : null));
                }
        }
        catch { }
    }

    static void Network(List<Component> list)
    {
        var drivers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Wmi.Each("SELECT DeviceID, DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DeviceClass = 'NET'", o =>
        {
            if (o["DeviceID"] is string id) drivers[id] = $"{o["DriverVersion"]}{Date(o["DriverDate"])}";
        });
        // só placas reais em PCI/USB (fora o Bluetooth e as virtuais)
        Wmi.Each("SELECT Name, Speed, NetEnabled, PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = True", o =>
        {
            var pnp = o["PNPDeviceID"]?.ToString() ?? "";
            if (!pnp.StartsWith("PCI", StringComparison.OrdinalIgnoreCase) && !pnp.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) return;
            var on = o["NetEnabled"] is true;
            var speed = Convert.ToUInt64(o["Speed"] ?? 0UL);
            var details = new List<(string, string)>
            {
                (Loc.T("Estado"), on ? (speed is > 0 and < ulong.MaxValue / 2 ? Loc.T($"Ligada · {FormatSpeed(speed)}") : Loc.T("Ligada")) : Loc.T("Desligada")),
            };
            if (drivers.TryGetValue(pnp, out var drv)) details.Add((Loc.T("Driver"), drv));
            list.Add(new(Loc.T("Placa de rede"), "🌐", o["Name"]?.ToString() ?? "", details));
        });
    }

    static void Fans(List<Component> list, List<GpuSensor> sensors)
    {
        var details = sensors.Where(g => g.FanPercent != null).Select(g => (Loc.T("Placa gráfica"), $"{g.FanPercent}%")).ToList();
        var name = details.Count > 0 ? Loc.T("Só a da placa gráfica") : Loc.T("Sem leitura no Windows");
        list.Add(new(Loc.T("Ventoinhas"), "🌀", name, details,
            Note: Loc.T("As da caixa e do CPU só aparecem com um driver especial. Vê-as na BIOS ou com o HWiNFO (aba Apps).")));
    }

    static string FormatSpeed(ulong bps) => bps >= 1_000_000_000 ? $"{bps / 1e9:0.#} Gbps" : $"{bps / 1e6:0} Mbps";

    static string Date(object? wmiDate)
    {
        try { return wmiDate is string d ? $" · {ManagementDateTimeConverter.ToDateTime(d):dd/MM/yyyy}" : ""; }
        catch { return ""; }
    }
}
