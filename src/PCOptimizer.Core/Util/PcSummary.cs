using System.Management;

namespace PCOptimizer.Core.Util;

/// <summary>Resumo das peças do PC (para o relatório e o botão "Copiar"), sem o nome de utilizador.</summary>
public static class PcSummary
{
    public static string Redact(string s)
    {
        var user = Environment.UserName;
        return string.IsNullOrEmpty(user) ? s : s.Replace(user, "<utilizador>", StringComparison.OrdinalIgnoreCase);
    }

    static string? _summary;

    public static string Text()
    {
        if (_summary != null) return _summary;
        var lines = new List<string>();
        var os = Reg.Get(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName");
        var ver = Reg.Get(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");
        var build = Environment.OSVersion.Version.Build;
        // o registo ainda diz "Windows 10" no Windows 11; o número da build é o que conta
        lines.Add($"- Windows: {(build >= 22000 ? "Windows 11" : os)} {ver} (build {build})");
        lines.Add($"- RAM: {SystemInfo.GetMemory().TotalGb:N0} GB");
        Wmi("SELECT Name FROM Win32_Processor", o => lines.Add($"- CPU: {o["Name"]?.ToString()?.Trim()}"));
        var ram = Memory.RamStabilizer.Analyze();
        foreach (var m in ram.Modules) lines.Add($"- RAM {m}");
        if (ram.Modules.Count > 0) lines.Add(Loc.T($"- Perfil XMP/EXPO: {(ram.XmpActive ? Loc.T("ligado (overclock da RAM)") : Loc.T("desligado"))}"));
        Wmi("SELECT Name, DriverVersion FROM Win32_VideoController", o => lines.Add($"- GPU: {o["Name"]} (driver {o["DriverVersion"]})"));
        Wmi("SELECT Manufacturer, Product FROM Win32_BaseBoard", o => lines.Add($"- Motherboard: {o["Manufacturer"]} {o["Product"]}"));
        Wmi("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS", o =>
        {
            var date = o["ReleaseDate"] is string d ? ManagementDateTimeConverter.ToDateTime(d).ToString("yyyy-MM-dd") : "?";
            lines.Add($"- BIOS: {o["SMBIOSBIOSVersion"]} ({date})");
        });
        Wmi("SELECT Model, Size FROM Win32_DiskDrive", o =>
        {
            var gb = o["Size"] is ulong size ? size / 1_000_000_000 : 0;
            lines.Add(Loc.T($"- Disco: {o["Model"]} ({gb} GB)"));
        });
        return _summary = string.Join("\n", lines);
    }

    static void Wmi(string query, Action<ManagementBaseObject> each) => Util.Wmi.Each(query, each);
}
