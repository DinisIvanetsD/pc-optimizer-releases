using System.Diagnostics;
using System.IO.Compression;
using PCOptimizer.Core.Updates;
using Xunit;

namespace PCOptimizer.Tests;

/// <summary>
/// Instalador e atualizador a sério, mas numa pasta de teste (PCOPT_INSTALL_ROOT):
/// não toca na instalação verdadeira, no Menu Iniciar nem no arranque com o Windows.
/// </summary>
[Collection("install")]
public class InstallUpdateTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "pcopt-install-" + Guid.NewGuid().ToString("N")[..8]);

    public InstallUpdateTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("PCOPT_INSTALL_ROOT", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PCOPT_INSTALL_ROOT", null);
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task Instala_cria_atalhos_e_regista_e_desinstala_limpo()
    {
        var exe = await Installer.InstallAsync(desktopShortcut: true, version: "9.9.9");

        Assert.True(File.Exists(exe));
        Assert.StartsWith(_root, exe);
        Assert.True(File.Exists(Installer.StartMenuShortcut));
        Assert.True(File.Exists(Installer.DesktopShortcut));
        Assert.True(Installer.IsRegistered);

        Installer.Uninstall(removeData: false);
        Assert.False(File.Exists(Installer.StartMenuShortcut));
        Assert.False(File.Exists(Installer.DesktopShortcut));
        Assert.False(Installer.IsRegistered);
    }

    [Fact]
    public void Atualizacao_substitui_os_ficheiros_depois_de_a_app_fechar()
    {
        // a "app instalada", versão antiga
        var target = Path.Combine(_root, "app");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "PCOptimizer.exe"), "antiga");
        File.WriteAllText(Path.Combine(target, "settings-locais.txt"), "fica");

        // o ZIP da versão nova (com uma pasta lá dentro, como o GitHub às vezes faz)
        var src = Path.Combine(_root, "zipsrc", "PCOptimizer");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "PCOptimizer.exe"), "nova");
        File.WriteAllText(Path.Combine(src, "novo.dll"), "dll");
        var zip = Path.Combine(_root, Updater.AssetName);
        ZipFile.CreateFromDirectory(Path.Combine(_root, "zipsrc"), zip);

        // um processo que já terminou faz de "app que fechou"
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();

        var script = Updater.PrepareFromZip(zip, Path.Combine(_root, "work"), target, p.Id, restart: false);
        using (var run = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            Assert.True(run.WaitForExit(60_000));

        Assert.Equal("nova", File.ReadAllText(Path.Combine(target, "PCOptimizer.exe")));
        Assert.Equal("dll", File.ReadAllText(Path.Combine(target, "novo.dll")));
        Assert.Equal("fica", File.ReadAllText(Path.Combine(target, "settings-locais.txt"))); // não apaga o que não vem no ZIP
    }

    [Fact]
    public void ZIP_local_le_a_versao_da_app_que_vem_dentro()
    {
        // um executável verdadeiro com versão (o próprio cmd.exe, renomeado) faz de app nova
        var src = Path.Combine(_root, "zipver");
        Directory.CreateDirectory(src);
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        File.Copy(cmd, Path.Combine(src, "PCOptimizer.exe"));
        var zip = Path.Combine(_root, "local.zip");
        ZipFile.CreateFromDirectory(src, zip);

        var (version, script) = Updater.PrepareFromLocalZip(zip);

        var fi = FileVersionInfo.GetVersionInfo(cmd); // o texto da versão no Windows pode vir desatualizado: contam os números
        Assert.Equal(new Version(fi.FileMajorPart, fi.FileMinorPart, fi.FileBuildPart, fi.FilePrivatePart), version);
        Assert.True(File.Exists(script));
        Assert.Contains(AppContext.BaseDirectory.TrimEnd('\\'), File.ReadAllText(script)); // atualiza a pasta da app que está a correr
    }

    [Fact]
    public async Task Instalar_copia_so_os_ficheiros_da_app()
    {
        // o ZIP extraído diretamente nas "Transferências", ao lado de ficheiros pessoais
        var downloads = Path.Combine(_root, "Transferencias");
        Directory.CreateDirectory(Path.Combine(downloads, "runtimes", "win"));
        File.WriteAllText(Path.Combine(downloads, "PCOptimizer.exe"), "app");
        File.WriteAllText(Path.Combine(downloads, "runtimes", "win", "a.dll"), "dll");
        File.WriteAllText(Path.Combine(downloads, "fotos-das-ferias.zip"), "pessoal");
        File.WriteAllText(Path.Combine(_root, "fora.txt"), "fora da pasta");
        File.WriteAllLines(Path.Combine(downloads, "files.txt"), new[] { "PCOptimizer.exe", "runtimes/win/a.dll", @"..\fora.txt" });

        var target = Path.Combine(_root, "instalado");
        await Installer.CopyAppFilesAsync(downloads, target);

        Assert.True(File.Exists(Path.Combine(target, "PCOptimizer.exe")));
        Assert.True(File.Exists(Path.Combine(target, "runtimes", "win", "a.dll")));
        Assert.False(File.Exists(Path.Combine(target, "fotos-das-ferias.zip"))); // o que não é da app fica onde está
        Assert.False(File.Exists(Path.Combine(_root, "fora.txt.copy")));
        Assert.Equal(3, Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length); // exe, dll e files.txt
    }

    [Fact]
    public void ZIP_sem_a_app_e_recusado()
    {
        var src = Path.Combine(_root, "vazio");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "leiame.txt"), "x");
        var zip = Path.Combine(_root, "mau.zip");
        ZipFile.CreateFromDirectory(src, zip);
        Assert.Throws<InvalidOperationException>(() => Updater.PrepareFromZip(zip, Path.Combine(_root, "w2"), _root, 1, restart: false));
    }

    [Fact]
    public void VerifyDownload_accepts_the_published_hash_and_refuses_changes()
    {
        var f = Path.Combine(_root, "u.zip");
        File.WriteAllText(f, "abc");
        const string sha = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"; // SHA-256 de "abc"
        Updater.VerifyDownload(f, 3, sha.ToLowerInvariant());
        Updater.VerifyDownload(f, 3, null); // versões antigas sem hash: só o tamanho

        Assert.Throws<InvalidOperationException>(() => Updater.VerifyDownload(f, 4, sha));
        Assert.False(File.Exists(f)); // o ficheiro mau é apagado

        File.WriteAllText(f, "abd");
        Assert.Throws<InvalidOperationException>(() => Updater.VerifyDownload(f, 3, sha));
    }
}
