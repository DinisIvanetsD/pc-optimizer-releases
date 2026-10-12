using System.Diagnostics;
using Microsoft.Win32;
using PCOptimizer.Core.Settings;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Updates;

/// <summary>
/// Instalação "por utilizador", sem precisar de administrador: copia a app para
/// %LOCALAPPDATA%\Programs\PC Optimizer, cria o atalho no Menu Iniciar e regista-a em
/// Definições › Aplicações (para se poder desinstalar como qualquer outro programa).
/// </summary>
public static class Installer
{
    /// <summary>PCOPT_INSTALL_ROOT: instala numa pasta de teste (atalhos e registo à parte, sem mexer no arranque).</summary>
    static string? TestRoot => Environment.GetEnvironmentVariable("PCOPT_INSTALL_ROOT");

    static string UninstallKey => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PCOptimizer" + (TestRoot is null ? "" : "-Test");

    public static string InstallDir => TestRoot is string t ? Path.Combine(t, "PC Optimizer") : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PC Optimizer");

    public static string StartMenuShortcut => Path.Combine(
        TestRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Rigwise.lnk");

    public static string DesktopShortcut => Path.Combine(
        TestRoot is string t ? Path.Combine(t, "Desktop") : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Rigwise.lnk");

    /// <summary>
    /// A app chamava-se "PC Optimizer": nas instalações antigas, renomeia os atalhos e o nome em "Apps instaladas".
    /// (A pasta e o .exe ficam com o nome antigo para as atualizações continuarem a funcionar.)
    /// </summary>
    public static void MigrateOldName()
    {
        if (!IsRunningInstalled) return;
        foreach (var lnk in new[] { StartMenuShortcut, DesktopShortcut })
            try
            {
                var old = Path.Combine(Path.GetDirectoryName(lnk)!, "PC Optimizer.lnk");
                if (!File.Exists(old)) continue;
                if (File.Exists(lnk)) File.Delete(old);
                else File.Move(old, lnk);
            }
            catch { }
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true);
            if (k?.GetValue("DisplayName") as string == "PC Optimizer") { k.SetValue("DisplayName", "Rigwise"); k.SetValue("Publisher", "Rigwise"); }
        }
        catch { }
    }

    public static bool IsRegistered
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return k?.GetValue("UninstallString") is string;
        }
    }

    /// <summary>A app que está a correr é a instalada?</summary>
    public static bool IsRunningInstalled =>
        AppContext.BaseDirectory.TrimEnd('\\').Equals(InstallDir, StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(Path.Combine(InstallDir, "PCOptimizer.exe"));

    /// <summary>Copia, cria atalhos e regista. Devolve o caminho do executável instalado.</summary>
    public static async Task<string> InstallAsync(bool desktopShortcut, string version)
    {
        Simulation.Guard(Loc.T("instalar a app no PC (Menu Iniciar)"));
        var source = AppContext.BaseDirectory.TrimEnd('\\');
        if (IsInstalled && TestRoot is null)
        {
            // a cópia instalada aberta prende os ficheiros (robocopy dá "ERROR 32"): dá-lhe uns segundos e depois explica
            for (var i = 0; i < 10 && OtherCopiesRunning(); i++) await Task.Delay(500);
            if (OtherCopiesRunning())
                throw new InvalidOperationException(Loc.T("O Rigwise instalado está aberto. Fecha-o (ícone junto ao relógio › Sair) e tenta outra vez."));
        }
        Directory.CreateDirectory(InstallDir);
        await CopyAppFilesAsync(source, InstallDir);

        var exe = Path.Combine(InstallDir, "PCOptimizer.exe");
        await CreateShortcutAsync(StartMenuShortcut, exe);
        if (desktopShortcut)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DesktopShortcut)!);
            await CreateShortcutAsync(DesktopShortcut, exe);
        }

        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "Rigwise");
            k.SetValue("DisplayVersion", version);
            k.SetValue("Publisher", "Rigwise");
            k.SetValue("DisplayIcon", exe);
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(Storage.StorageDrives.FolderSize(InstallDir) / 1024), RegistryValueKind.DWord);
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }

        // se arrancava com o Windows a partir da pasta antiga, passa a arrancar a instalada
        if (TestRoot is null && AutoStart.IsEnabled)
        {
            using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            run.SetValue("PCOptimizer", $"\"{exe}\" --tray");
        }
        return exe;
    }

    /// <summary>A lista dos ficheiros da app (files.txt, criada ao publicar). Null = compilação de desenvolvimento.</summary>
    public static List<string>? ManifestFiles(string folder)
    {
        var path = Path.Combine(folder, "files.txt");
        if (!File.Exists(path)) return null;
        return File.ReadAllLines(path)
            .Select(l => l.Trim().Replace('/', '\\'))
            // só caminhos relativos dentro da pasta (nada de "..\" nem C:\)
            .Where(l => l.Length > 0 && !Path.IsPathRooted(l) && !l.Split('\\').Contains(".."))
            .ToList();
    }

    /// <summary>
    /// Copia só os ficheiros da app. Sem isto, quem extraísse o ZIP diretamente nas Transferências
    /// e carregasse em Instalar copiava a pasta Transferências inteira.
    /// </summary>
    public static async Task CopyAppFilesAsync(string source, string target)
    {
        if (ManifestFiles(source) is { } files)
        {
            await Task.Run(() =>
            {
                foreach (var rel in files.Append("files.txt"))
                {
                    var from = Path.Combine(source, rel);
                    if (!File.Exists(from)) continue;
                    var to = Path.Combine(target, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(from, to, overwrite: true);
                }
            });
            return;
        }
        // compilação de desenvolvimento (sem files.txt): a pasta tem de ser só da app
        if (!File.Exists(Path.Combine(source, "PCOptimizer.deps.json")) || IsUserFolder(source))
            throw new InvalidOperationException(Loc.T("Esta pasta não parece ser só da app. Extrai o ZIP para uma pasta própria e tenta outra vez."));
        var r = await Shell.RunAsync("robocopy.exe", $"\"{source}\" \"{target}\" /E /R:2 /W:1 /NFL /NDL /NJH /NJS", 300_000);
        if (r.ExitCode >= 8) throw new InvalidOperationException(Loc.T("Não foi possível copiar os ficheiros: ") + Shell.CleanError(r));
    }

    static bool IsUserFolder(string folder)
    {
        var f = folder.TrimEnd('\\');
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            profile, Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        }.Any(p => f.Equals(p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Outra cópia da app aberta (se correr como administrador nem se vê o caminho: conta como aberta).</summary>
    static bool OtherCopiesRunning()
    {
        foreach (var p in Process.GetProcessesByName("PCOptimizer"))
            using (p)
            {
                if (p.Id == Environment.ProcessId) continue;
                try
                {
                    var path = p.MainModule?.FileName;
                    if (path is null || path.StartsWith(InstallDir + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { return true; }
            }
        return false;
    }

    static async Task CreateShortcutAsync(string lnk, string target)
    {
        var script = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut({Shell.Quote(lnk)});" +
                     $"$s.TargetPath={Shell.Quote(target)};$s.WorkingDirectory={Shell.Quote(Path.GetDirectoryName(target)!)};" +
                     $"$s.IconLocation={Shell.Quote(target + ",0")};$s.Description='Rigwise';$s.Save()";
        var r = await Shell.PowerShellAsync(script);
        if (!r.Ok) throw new InvalidOperationException(Loc.T("Não foi possível criar o atalho: ") + Shell.CleanError(r));
    }

    /// <summary>
    /// Remove atalhos, registo e arranque automático; a pasta é apagada por um script depois de a app fechar.
    /// <paramref name="removeData"/> apaga também histórico, definições e backups dos ajustes.
    /// </summary>
    public static void Uninstall(bool removeData)
    {
        Simulation.Guard("desinstalar a app");
        foreach (var f in new[] { StartMenuShortcut, DesktopShortcut })
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        if (TestRoot is null) AutoStart.Set(false);

        // só apaga a pasta de instalação (nunca uma cópia noutro sítio, ex.: a pasta de desenvolvimento)
        var folders = new List<string>();
        if (IsInstalled) folders.Add(InstallDir);
        if (removeData) folders.Add(AppSettings.DataFolder);
        if (folders.Count == 0) return;
        var script = Path.Combine(Path.GetTempPath(), "PCOptimizer-uninstall.cmd");
        File.WriteAllText(script, $"""
            @echo off
            :wait
            tasklist /fi "PID eq {Environment.ProcessId}" | find "{Environment.ProcessId}" >nul && (timeout /t 1 /nobreak >nul & goto wait)
            {string.Join("\r\n", folders.Select(f => $"rmdir /s /q \"{f}\""))}
            del "%~f0"
            """);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false });
    }
}
