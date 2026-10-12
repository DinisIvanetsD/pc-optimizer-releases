using System.Management;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Memory;

public sealed record RamModule(string Slot, string Channel, string Brand, string PartNumber, double CapacityGb,
    int RatedMhz, int ConfiguredMhz, string Type)
{
    public override string ToString() =>
        $"{Slot} ({Channel}): {Brand} {PartNumber} · {CapacityGb:N0} GB · {Type} a {ConfiguredMhz} MHz";
}

public sealed record StabilizerStep(string Title, string Detail, string? WhereInBios, string Impact);

public sealed record RamReport(IReadOnlyList<RamModule> Modules, bool XmpActive, string Diagnosis,
    IReadOnlyList<StabilizerStep> Steps, IReadOnlyList<string> Warnings);

/// <summary>
/// "Estabilizador de RAM": não há software que conserte um chip avariado, mas a maior parte da RAM
/// "instável" está simplesmente a funcionar acima das especificações padrão (perfil XMP/EXPO).
/// Isto lê a configuração atual e monta um plano passo-a-passo, do mais simples ao mais avançado,
/// com os nomes dos menus da BIOS do fabricante da motherboard.
/// </summary>
public static class RamStabilizer
{
    public static RamReport Analyze()
    {
        var modules = ReadModules();
        var board = Wmi("SELECT Manufacturer, Product FROM Win32_BaseBoard", o => $"{o["Manufacturer"]} {o["Product"]}").FirstOrDefault() ?? "";
        var cpu = Wmi("SELECT Name FROM Win32_Processor", o => o["Name"]?.ToString() ?? "").FirstOrDefault() ?? "";
        var isAmd = cpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) || cpu.Contains("Ryzen", StringComparison.OrdinalIgnoreCase);
        var warnings = new List<string>();

        if (modules.Count == 0)
            return new RamReport(modules, false, Loc.T("Não foi possível ler a informação dos módulos de RAM."), Array.Empty<StabilizerStep>(), warnings);

        var type = modules[0].Type;
        var speed = modules.Max(m => m.ConfiguredMhz);
        // velocidades oficiais máximas sem overclock (JEDEC)
        var jedecMax = type == "DDR5" ? 5600 : 3200;
        var xmp = speed > jedecMax || (type == "DDR4" && speed > 2666 && modules.All(m => m.ConfiguredMhz >= m.RatedMhz && m.RatedMhz > 2666));
        var lowerSpeed = type == "DDR5" ? Math.Max(4800, speed - 400) : speed > 3200 ? 3200 : Math.Max(2666, speed - 266);

        if (modules.Select(m => m.PartNumber).Distinct().Count() > 1)
            warnings.Add(Loc.T("Os módulos não são todos iguais. Misturar kits diferentes é uma causa muito comum de instabilidade: o kit só é garantido a funcionar em conjunto se for vendido junto."));
        if (modules.Count == 1 || modules.Select(m => m.Channel).Distinct().Count() == 1)
            warnings.Add(Loc.T("A RAM está a funcionar num só canal (single channel). Não causa erros, mas perdes desempenho. Confirma no manual quais são as ranhuras certas (normalmente A2 e B2)."));
        if (modules.Count >= 4)
            warnings.Add(Loc.T("Com 4 módulos o controlador de memória do CPU fica mais carregado. Velocidades altas com 4 módulos são das configurações mais difíceis de estabilizar."));

        var bios = BiosNames(board);
        var steps = new List<StabilizerStep>();

        if (xmp)
        {
            steps.Add(new StabilizerStep(
                Loc.T($"1. Baixar a velocidade de {speed} para {lowerSpeed} MHz (mantendo o XMP)"),
                Loc.T($"Ativa o perfil XMP/EXPO como está e, depois, muda só a frequência da memória para {lowerSpeed} MHz. ") +
                Loc.T("Normalmente perdes menos de 3% de desempenho e os erros desaparecem."),
                bios.Frequency, Loc.T("Quase nenhum")));
            steps.Add(new StabilizerStep(
                Loc.T("2. Se ainda der erros: desligar o XMP/EXPO"),
                Loc.T("A RAM volta às especificações padrão (mais lenta, mas garantidamente dentro das especificações). ") +
                Loc.T("Se mesmo assim der erros, o problema é mesmo hardware."),
                bios.Xmp, Loc.T("~5–10% em jogos que dependem da RAM")));
            steps.Add(new StabilizerStep(
                Loc.T("3. Avançado: subir ligeiramente a tensão da RAM"),
                type == "DDR5"
                    ? Loc.T("Com o XMP/EXPO à velocidade original, sobe a tensão VDD/VDDQ em +0,02–0,05 V (ex.: 1,35 → 1,38 V). Não passes de 1,40 V.")
                    : Loc.T("Com o XMP à velocidade original, sobe a DRAM Voltage de 1,35 V para 1,37–1,38 V. É seguro para DDR4 até 1,40 V."),
                bios.Voltage, Loc.T("Nenhum (só mais um pouco de calor)")));
            if (isAmd && type == "DDR4" && speed >= 3600)
                steps.Add(new StabilizerStep(
                    Loc.T("4. Avançado (Ryzen): FCLK e tensão SoC"),
                    Loc.T($"Em Ryzen, a {speed} MHz o Infinity Fabric (FCLK) tem de estar a {speed / 2} MHz. Alguns CPUs não aguentam: ") +
                    Loc.T($"põe o FCLK em {speed / 2} manualmente e a tensão SoC em 1,10 V (nunca acima de 1,20 V). ") +
                    Loc.T("Se não estabilizar, usa o passo 1 (3200 MHz + FCLK 1600)."),
                    bios.Fclk, Loc.T("Nenhum")));
        }
        else
        {
            steps.Add(new StabilizerStep(
                Loc.T("1. A RAM já está nas especificações padrão"),
                Loc.T("Não há overclock para retirar. Se o teste de RAM der erros com estas definições, é muito provável que um módulo esteja avariado."),
                null, "—"));
        }

        steps.Add(new StabilizerStep(
            $"{steps.Count + 1}. Atualizar a BIOS",
            Loc.T("As atualizações da BIOS (AGESA, em AMD) trazem muitas vezes melhorias na compatibilidade e estabilidade da memória. ") +
            Loc.T("Descarrega-a só do site oficial do fabricante da motherboard."),
            bios.Update, Loc.T("Nenhum")));
        steps.Add(new StabilizerStep(
            Loc.T($"{steps.Count + 1}. Descobrir o módulo avariado (se os erros continuarem)"),
            Loc.T("Desliga o PC da corrente, deixa só um módulo (na ranhura A2) e faz o teste de RAM. Depois troca para o outro. ") +
            Loc.T("Se só um falhar, está avariado: aciona a garantia (a G.Skill, a Corsair e a Kingston têm garantia vitalícia) e usa o bom entretanto."),
            null, Loc.T("Nenhum")));

        var diagnosis = xmp
            ? Loc.T($"A tua RAM está a {speed} MHz com o perfil XMP/EXPO ligado: é um overclock de fábrica, acima das especificações oficiais do CPU ({jedecMax} MHz). ") +
              Loc.T("É a causa mais comum de erros de memória e ecrãs azuis sem a RAM estar avariada, e resolve-se na BIOS sem gastar dinheiro.")
            : Loc.T("A tua RAM está nas especificações padrão. Se tiveres erros de memória, o mais provável é um módulo avariado, um módulo mal encaixado ou incompatibilidade entre módulos.");

        return new RamReport(modules, xmp, diagnosis, steps, warnings);
    }

    /// <summary>Reinicia o PC diretamente para a BIOS/UEFI (precisa de administrador).</summary>
    public static async Task<string> RebootToBiosAsync()
    {
        Simulation.Guard(Loc.T("reiniciar o PC direto para a BIOS"));
        Admin.Require();
        var r = await Shell.RunAsync("shutdown", "/r /fw /t 10 /c \"Rigwise: a reiniciar para a BIOS\"");
        return r.Ok
            ? Loc.T("O PC vai reiniciar para a BIOS dentro de 10 segundos.")
            : Loc.T("Este PC não permite abrir a BIOS diretamente. Reinicia e carrega na tecla Delete (ou F2) enquanto aparece o logótipo.");
    }

    sealed record BiosMenus(string Xmp, string Frequency, string Voltage, string Fclk, string Update);

    static BiosMenus BiosNames(string board)
    {
        if (board.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase) || board.Contains("AORUS", StringComparison.OrdinalIgnoreCase))
            return new BiosMenus(
                "Tecla Delete → F2 (Advanced Mode) → Tweaker → Extreme Memory Profile (X.M.P.) → Disabled",
                Loc.T("Tweaker → Extreme Memory Profile (X.M.P.) → Profile1, depois Tweaker → System Memory Multiplier → valor da nova velocidade (ex.: 32.00 = 3200 MHz)"),
                "Tweaker → Advanced Voltage Settings → DRAM Voltage (CH A/B)",
                "Settings → AMD Overclocking → Infinity Fabric Frequency and Dividers (FCLK); Tweaker → Advanced Voltage Settings → SOC Vcore",
                Loc.T("Q-Flash (no ecrã da BIOS) com o ficheiro do site da Gigabyte, numa pen USB FAT32"));
        if (board.Contains("ASUS", StringComparison.OrdinalIgnoreCase))
            return new BiosMenus(
                "F7 (Advanced Mode) → Ai Tweaker → Ai Overclock Tuner → Auto",
                "Ai Tweaker → Ai Overclock Tuner → D.O.C.P./XMP/EXPO, depois Ai Tweaker → Memory Frequency",
                "Ai Tweaker → DRAM Voltage",
                "Ai Tweaker → FCLK Frequency; Ai Tweaker → VDDCR SOC Voltage",
                Loc.T("Tool → ASUS EZ Flash com o ficheiro do site da ASUS"));
        if (board.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || board.Contains("MSI", StringComparison.OrdinalIgnoreCase))
            return new BiosMenus(
                "F7 (Advanced) → OC → Extreme Memory Profile (XMP/A-XMP/EXPO) → Disabled",
                "OC → A-XMP/EXPO → Profile 1, depois OC → DRAM Frequency",
                "OC → DigitALL Power / Voltage Setting → DRAM Voltage",
                "OC → Advanced DRAM Configuration / FCLK Frequency; OC → CPU NB/SoC Voltage",
                Loc.T("M-Flash com o ficheiro do site da MSI"));
        if (board.Contains("ASRock", StringComparison.OrdinalIgnoreCase))
            return new BiosMenus(
                "F6 (Advanced) → OC Tweaker → DRAM Configuration → Load XMP Setting → Auto",
                "OC Tweaker → Load XMP Setting → XMP 2.0 Profile 1, depois OC Tweaker → DRAM Frequency",
                "OC Tweaker → Voltage Configuration → DRAM Voltage",
                "OC Tweaker → Infinity Fabric Frequency (FCLK); OC Tweaker → VDDCR_SOC Voltage",
                Loc.T("Tool → Instant Flash com o ficheiro do site da ASRock"));
        return new BiosMenus(
            Loc.T("Na BIOS, procura \"XMP\", \"EXPO\" ou \"D.O.C.P.\" (menu de overclocking / memória) e desliga-o"),
            Loc.T("Mantém o XMP ligado e procura \"DRAM Frequency\" / \"Memory Frequency\" para baixar a velocidade"),
            Loc.T("Procura \"DRAM Voltage\" no menu de tensões"),
            Loc.T("Procura \"FCLK\" / \"Infinity Fabric\" e \"SoC Voltage\""),
            Loc.T("Usa a ferramenta de atualização da BIOS com o ficheiro do site do fabricante"));
    }

    static List<RamModule> ReadModules() => Wmi(
        "SELECT DeviceLocator, BankLabel, Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType FROM Win32_PhysicalMemory",
        o =>
        {
            var part = (o["PartNumber"]?.ToString() ?? "").Trim();
            var type = Convert.ToInt32(o["SMBIOSMemoryType"] ?? 0) switch { 34 => "DDR5", 26 => "DDR4", 24 => "DDR3", _ => "DDR" };
            return new RamModule(
                (o["DeviceLocator"]?.ToString() ?? "?").Trim(),
                (o["BankLabel"]?.ToString() ?? "?").Trim(),
                Brand(o["Manufacturer"]?.ToString(), part),
                part,
                Convert.ToUInt64(o["Capacity"] ?? 0UL) / (double)(1L << 30),
                Convert.ToInt32(o["Speed"] ?? 0),
                Convert.ToInt32(o["ConfiguredClockSpeed"] ?? 0),
                type);
        });

    /// <summary>O Windows muitas vezes diz "Unknown" no fabricante; o código do modelo denuncia a marca.</summary>
    static string Brand(string? manufacturer, string part)
    {
        if (!string.IsNullOrWhiteSpace(manufacturer) && !manufacturer.Contains("Unknown", StringComparison.OrdinalIgnoreCase)
            && !manufacturer.StartsWith("0", StringComparison.Ordinal))
            return manufacturer.Trim();
        return part switch
        {
            _ when part.StartsWith("F4-") || part.StartsWith("F5-") => "G.Skill",
            _ when part.StartsWith("CM") => "Corsair",
            _ when part.StartsWith("KF") || part.StartsWith("KHX") || part.StartsWith("KVR") => "Kingston",
            _ when part.StartsWith("BL") || part.StartsWith("CT") => "Crucial",
            _ when part.StartsWith("TF") || part.StartsWith("TD") => "TeamGroup",
            _ when part.StartsWith("PV") => "Patriot",
            _ when part.StartsWith("M3") || part.StartsWith("M4") => "Samsung",
            _ => "Desconhecida",
        };
    }

    static List<T> Wmi<T>(string query, Func<ManagementBaseObject, T> map)
    {
        var list = new List<T>();
        try
        {
            using var s = new ManagementObjectSearcher(query);
            foreach (var o in s.Get())
            {
                using (o) list.Add(map(o));
            }
        }
        catch { }
        return list;
    }
}
