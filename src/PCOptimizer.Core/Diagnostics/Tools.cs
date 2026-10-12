using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Diagnostics;

/// <summary>Ferramentas do Windows que a app pode abrir para o utilizador.</summary>
public static class Tools
{
    /// <summary>Em modo simulação devolve a mensagem em vez de executar.</summary>
    static Task<string>? Simulated(string action) =>
        Simulation.Enabled ? Task.FromResult(Loc.T($"🧪 Simulação: nada foi alterado. A app ia {action}.")) : null;

    static string Result(bool opened, string ok) => opened ? ok : "Cancelado.";

    public static Task<string> SystemFileCheck() => Simulated(Loc.T("verificar e reparar os ficheiros do sistema (SFC)")) ?? Task.FromResult(Result(
        Shell.Open("cmd.exe", "/k sfc /scannow", elevated: true),
        Loc.T("Abri o SFC numa janela nova. Demora 5–15 minutos; não a feches.")));

    public static Task<string> DismRepair() => Simulated(Loc.T("reparar a imagem do Windows (DISM)")) ?? Task.FromResult(Result(
        Shell.Open("cmd.exe", "/k DISM /Online /Cleanup-Image /RestoreHealth", elevated: true),
        Loc.T("Abri o DISM numa janela nova. Precisa de internet e pode demorar 10–30 minutos.")));

    public static Task<string> CheckDisk() => Simulated(Loc.T("agendar a verificação do disco (chkdsk)")) ?? Task.FromResult(Result(
        Shell.Open("cmd.exe", "/k chkdsk C: /scan", elevated: true),
        Loc.T("Abri o CHKDSK numa janela nova. Este modo só verifica o disco (não altera nada) e pode demorar alguns minutos.")));

    public static Task<string> ReliabilityMonitor() => Task.FromResult(Result(
        Shell.Open("perfmon.exe", "/rel"), Loc.T("Abri o Monitor de Fiabilidade: mostra dia a dia os crashes e o que mudou no PC.")));

    public static Task<string> OpenRamTest()
    {
        UiHooks.Navigate?.Invoke("ramtest");
        return Task.FromResult("");
    }

    public static Task<string> MemoryTest() => Simulated(Loc.T("agendar o teste de memória do Windows (reinicia o PC)")) ?? Task.FromResult(Result(
        Shell.Open("mdsched.exe", elevated: true),
        Loc.T("Abri o Diagnóstico de Memória do Windows. Escolhe \"Reiniciar agora\" para testar a RAM.")));

    public static Task<string> DiskCleanup() => Task.FromResult(Result(
        Shell.Open("cleanmgr.exe"), Loc.T("Abri a Limpeza de Disco.")));

    public static Task<string> StorageSettings() => Task.FromResult(Result(
        Shell.Open("ms-settings:storagesense"), Loc.T("Abri as definições de Armazenamento.")));

    public static Task<string> DeviceManager() => Task.FromResult(Result(
        Shell.Open("devmgmt.msc"), Loc.T("Abri o Gestor de Dispositivos. Os dispositivos com problemas têm um ⚠ amarelo.")));

    public static Task<string> WindowsUpdate() => Task.FromResult(Result(
        Shell.Open("ms-settings:windowsupdate"), Loc.T("Abri o Windows Update.")));

    public static Task<string> CleanTemp() => Simulated(Loc.T("apagar os ficheiros temporários")) ?? Task.Run(() =>
    {
        var (bytes, files) = TempCleaner.Clean(TimeSpan.FromDays(1));
        return files == 0
            ? Loc.T("Não havia ficheiros temporários para apagar.")
            : Loc.T($"Apagados {files:N0} ficheiros temporários ({SystemInfo.FormatBytes(bytes)} libertados).");
    });
}

/// <summary>Limpa ficheiros temporários com mais de X tempo (os recentes podem estar em uso).</summary>
public static class TempCleaner
{
    static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static IEnumerable<string> Folders()
    {
        yield return Path.GetTempPath();
        if (Admin.IsElevated)
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
    }

    public static long MeasureBytes()
    {
        long total = 0;
        foreach (var folder in Folders())
        {
            try
            {
                foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", Options))
                {
                    try { total += f.Length; } catch { }
                }
            }
            catch { }
        }
        return total;
    }

    public static (long Bytes, int Files) Clean(TimeSpan minAge)
    {
        Simulation.Guard(Loc.T("apagar os ficheiros temporários"));
        long bytes = 0;
        var files = 0;
        var cutoff = DateTime.UtcNow - minAge;
        foreach (var folder in Folders())
        {
            DirectoryInfo root;
            try { root = new DirectoryInfo(folder); } catch { continue; }
            foreach (var f in root.EnumerateFiles("*", Options))
            {
                try
                {
                    if (f.LastWriteTimeUtc > cutoff) continue;
                    var len = f.Length;
                    f.Delete();
                    bytes += len;
                    files++;
                }
                catch
                {
                    // em uso ou sem permissão: ignorar
                }
            }
            // remover pastas que ficaram vazias (das mais fundas para cima)
            foreach (var d in root.EnumerateDirectories("*", Options).OrderByDescending(d => d.FullName.Length))
            {
                try
                {
                    if (!d.EnumerateFileSystemInfos().Any()) d.Delete();
                }
                catch { }
            }
        }
        return (bytes, files);
    }
}
