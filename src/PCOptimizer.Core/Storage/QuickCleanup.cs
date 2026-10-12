using System.Runtime.InteropServices;
using PCOptimizer.Core.Diagnostics;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Storage;

public sealed class CleanupItem
{
    public required string Icon { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required Func<long?> Measure { get; init; }
    /// <summary>Limpar (null = só dá para abrir a pasta/ferramenta).</summary>
    public Func<Task<string>>? Clean { get; init; }
    public string CleanLabel { get; init; } = "Limpar";
    public string? ConfirmText { get; init; }
    public bool NeedsAdmin { get; init; }
    public Func<Task<string>>? Open { get; init; }
    public string OpenLabel { get; init; } = Loc.T("Abrir");
}

/// <summary>Sítios onde costuma haver gigabytes de lixo, cada um com o tamanho e a forma segura de limpar.</summary>
public static class QuickCleanup
{
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static string SystemRoot => Path.GetPathRoot(Win) ?? "C:\\";

    static readonly string[] ShaderCaches =
    {
        Path.Combine(Local, @"NVIDIA\DXCache"), Path.Combine(Local, @"NVIDIA\GLCache"),
        Path.Combine(Local, "D3DSCache"), Path.Combine(Local, @"AMD\DxCache"), Path.Combine(Local, @"AMD\DxcCache"),
    };

    public static IReadOnlyList<CleanupItem> Items { get; } = new[]
    {
        new CleanupItem
        {
            Icon = "🗑", Name = "Reciclagem",
            Description = Loc.T("Ficheiros que apagaste mas que ainda ocupam espaço. Esvaziar apaga-os de vez."),
            Measure = RecycleBinSize,
            Clean = EmptyRecycleBin, CleanLabel = "Esvaziar",
            ConfirmText = Loc.T("Esvaziar a Reciclagem? Os ficheiros lá dentro são apagados de vez e não dá para os recuperar."),
            Open = () => Task.FromResult(Shell.Open("shell:RecycleBinFolder") ? "" : "Cancelado."),
        },
        new CleanupItem
        {
            Icon = "🧹", Name = Loc.T("Ficheiros temporários"),
            Description = Loc.T("Restos de instalações e de apps. Só são apagados os que têm mais de 1 dia (os recentes podem estar em uso)."),
            Measure = () => TempCleaner.MeasureBytes(),
            Clean = Tools.CleanTemp,
            ConfirmText = Loc.T("Apagar os ficheiros temporários com mais de 1 dia?"),
        },
        new CleanupItem
        {
            Icon = "🎮", Name = Loc.T("Cache de shaders (jogos)"),
            Description = Loc.T("Cache que a gráfica cria para os jogos. Volta a ser criada sozinha: o primeiro arranque de cada jogo fica um pouco mais lento. Útil se um jogo tiver stutter ou gráficos estranhos."),
            Measure = () => ShaderCaches.Where(Directory.Exists).Sum(p => StorageDrives.FolderSize(p)),
            Clean = () => Task.Run(() => Freed(DeleteContents(ShaderCaches))),
            ConfirmText = Loc.T("Apagar a cache de shaders? É recriada automaticamente; o primeiro arranque de cada jogo fica um pouco mais lento."),
        },
        new CleanupItem
        {
            Icon = "📥", Name = Loc.T("Transferências"),
            Description = Loc.T("A pasta Transferências costuma acumular instaladores e ficheiros antigos. Revê-a tu: a app não apaga nada aqui."),
            Measure = () => Directory.Exists(Downloads) ? StorageDrives.FolderSize(Downloads) : null,
            Open = () => Task.FromResult(Shell.Open(Downloads) ? "" : "Cancelado."), OpenLabel = Loc.T("Abrir a pasta"),
        },
        new CleanupItem
        {
            Icon = "🔄", Name = Loc.T("Cache do Windows Update"),
            Description = Loc.T("Atualizações já instaladas que ficaram guardadas. Limpa-se com a Limpeza de Disco do Windows (\"Limpar ficheiros do sistema\")."),
            Measure = () => Admin.IsElevated ? StorageDrives.FolderSize(Path.Combine(Win, @"SoftwareDistribution\Download")) : null,
            Open = Tools.DiskCleanup, OpenLabel = Loc.T("Abrir Limpeza de Disco"),
        },
        new CleanupItem
        {
            Icon = "🖥", Name = Loc.T("Instaladores antigos da NVIDIA"),
            Description = Loc.T("Cópias de instaladores de drivers antigos que a NVIDIA deixa no disco."),
            Measure = () => Admin.IsElevated ? StorageDrives.FolderSize(NvidiaDownloader) : null,
            Clean = () => Task.Run(() => Freed(DeleteContents(new[] { NvidiaDownloader }))),
            NeedsAdmin = true,
            ConfirmText = Loc.T("Apagar os instaladores antigos dos drivers da NVIDIA? O driver instalado não é afetado."),
        },
        new CleanupItem
        {
            Icon = "💥", Name = Loc.T("Despejos de crashes das apps"),
            Description = Loc.T("Relatórios de crashes das apps (não os dos ecrãs azuis, que a app mantém para diagnóstico)."),
            Measure = () => Directory.Exists(Path.Combine(Local, "CrashDumps")) ? StorageDrives.FolderSize(Path.Combine(Local, "CrashDumps")) : null,
            Clean = () => Task.Run(() => Freed(DeleteContents(new[] { Path.Combine(Local, "CrashDumps") }))),
        },
        new CleanupItem
        {
            Icon = "🌙", Name = Loc.T("Hibernação (hiberfil.sys)"),
            Description = Loc.T("Ficheiro do tamanho de parte da tua RAM, usado pela hibernação e pelo \"Arranque rápido\". Desligar liberta o espaço todo; o PC arranca uns segundos mais devagar."),
            Measure = () => SystemFileSize("hiberfil.sys"),
            Clean = async () =>
            {
                var r = await Shell.RunAsync("powercfg", "/h off");
                return r.Ok ? Loc.T("✓ Hibernação desligada. O espaço é libertado de imediato.") : Loc.T("Erro: ") + Shell.CleanError(r);
            },
            CleanLabel = Loc.T("Desligar hibernação"), NeedsAdmin = true,
            ConfirmText = Loc.T("Desligar a hibernação? Também desliga o \"Arranque rápido\" do Windows. Podes voltar a ligar com: powercfg /h on"),
        },
        new CleanupItem
        {
            Icon = "📦", Name = Loc.T("Windows antigo (Windows.old)"),
            Description = Loc.T("Cópia da versão anterior do Windows, deixada depois de uma atualização grande. Remove-se com a Limpeza de Disco (\"Instalações anteriores do Windows\")."),
            Measure = () => Directory.Exists(Path.Combine(SystemRoot, "Windows.old")) ? StorageDrives.FolderSize(Path.Combine(SystemRoot, "Windows.old")) : 0,
            Open = Tools.DiskCleanup, OpenLabel = Loc.T("Abrir Limpeza de Disco"),
        },
    };

    static string Downloads => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    static string NvidiaDownloader => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"NVIDIA Corporation\Downloader");

    static string Freed((long Bytes, int Files) r) =>
        r.Files == 0 ? Loc.T("Não havia nada para apagar (ou os ficheiros estão em uso).") : Loc.T($"✓ Libertados {SystemInfo.FormatBytes(r.Bytes)} ({r.Files:N0} ficheiros).");

    /// <summary>Apaga o conteúdo das pastas (não as próprias pastas). Ficheiros em uso são ignorados.</summary>
    static (long Bytes, int Files) DeleteContents(IEnumerable<string> folders)
    {
        long bytes = 0;
        var files = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", opts))
            {
                try
                {
                    var len = f.Length;
                    f.Delete();
                    bytes += len;
                    files++;
                }
                catch { }
            }
            foreach (var d in new DirectoryInfo(folder).EnumerateDirectories("*", opts).OrderByDescending(d => d.FullName.Length))
            {
                try { if (!d.EnumerateFileSystemInfos().Any()) d.Delete(); } catch { }
            }
        }
        return (bytes, files);
    }

    static long? SystemFileSize(string name)
    {
        try
        {
            var opts = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
            return new DirectoryInfo(SystemRoot).EnumerateFiles(name, opts).FirstOrDefault()?.Length ?? 0;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string? rootPath, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string? rootPath, uint flags);

    static long? RecycleBinSize()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : null;
    }

    static Task<string> EmptyRecycleBin() => Task.Run(() =>
    {
        var before = RecycleBinSize() ?? 0;
        const uint noConfirm = 0x1, noProgress = 0x2, noSound = 0x4;
        var hr = SHEmptyRecycleBin(IntPtr.Zero, null, noConfirm | noProgress | noSound);
        return hr == 0 || before == 0 ? Loc.T($"✓ Reciclagem vazia ({SystemInfo.FormatBytes(before)} libertados).") : Loc.T($"Não foi possível esvaziar (código {hr:X}).");
    });
}
