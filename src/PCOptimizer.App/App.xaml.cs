using System.IO;
using System.Windows;
using PCOptimizer.Core.Settings;
using PCOptimizer.Core.Updates;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

public partial class App : Application
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCOptimizer", "error.log");

    const string MutexName = @"Local\PCOptimizer.SingleInstance";
    const string ShowEventName = @"Local\PCOptimizer.Show";
    Mutex? _single;
    EventWaitHandle? _showEvent;

    /// <summary>
    /// Abre um "issue" novo no repositório público das versões, com o erro já escrito (sem o nome de utilizador).
    /// Nada é enviado sozinho: a pessoa vê o texto no browser e decide se envia.
    /// </summary>
    static void ReportError(Exception ex)
    {
        var body = PCOptimizer.Core.Util.PcSummary.Redact(
            $"**Versão:** {PCOptimizer.App.MainWindow.AppVersion} · Windows build {Environment.OSVersion.Version.Build}\n\n" +
            "**O que estava a fazer:** \n\n```\n" + ex + "\n```");
        if (body.Length > 5000) body = body[..5000] + "\n```"; // o URL tem limite de tamanho
        var url = "https://github.com/DinisIvanetsD/rigwise/issues/new?title=" +
                  Uri.EscapeDataString(PCOptimizer.Core.Util.PcSummary.Redact("Erro: " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0])) + "&body=" + Uri.EscapeDataString(body);
        PCOptimizer.Core.Util.Shell.Open(url);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true; // a app continua a funcionar
            var answer = MessageBox.Show(
                Loc.T($"Aconteceu um erro inesperado, mas a app continua a funcionar.\n\n{args.Exception.Message}\n\n") +
                Loc.T($"Os detalhes ficaram em {LogPath}.\nReportar o erro? Abre uma página no GitHub já preenchida; revês e envias tu."),
                Loc.T("Rigwise — erro inesperado"), MessageBoxButton.YesNo, MessageBoxImage.Error, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes) ReportError(args.Exception);
        };
        // erros em tarefas de fundo: só ficam no registo (não incomodam o utilizador)
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) Log(ex);
        };
        base.OnStartup(e);
        var args = e.Args;
        Loc.Language = AppSettings.Current.Language ?? "auto";
        Localizer.Register();
        PCOptimizer.Core.Updates.Installer.MigrateOldName();

        if (args.Contains("--uninstall"))
        {
            RunUninstall();
            Shutdown();
            return;
        }

        // instalação sem janelas (ex.: scripts): --install [--desktop]; código de saída 0 = instalada
        if (args.Contains("--install"))
        {
            _ = InstallFromCommandLineAsync(args.Contains("--desktop"));
            return;
        }

        // atualização sem janelas: 0 = atualizada (ou já estava), 1 = erro
        if (args.Contains("--update"))
        {
            _ = UpdateFromCommandLineAsync();
            return;
        }

        // só uma cópia aberta: se já houver uma, pede-lhe para aparecer e sai
        // (o modo de capturas para desenvolvimento pode correr ao lado)
        var screenshots = args.Contains("--screenshots");
        if (!screenshots)
        {
            _single = new Mutex(true, MutexName, out var first);
            // reaberta como administrador: a janela antiga ainda está a fechar, espera por ela em vez de desistir
            if (!first && (args.Contains("--diag") || args.Contains("--elevated")))
            {
                try { first = _single.WaitOne(TimeSpan.FromSeconds(10)); }
                catch (AbandonedMutexException) { first = true; }
            }
            if (!first)
            {
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                Shutdown();
                return;
            }
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        }

        var window = new MainWindow();
        MainWindow = window;
        if (_showEvent != null)
        {
            var ev = _showEvent;
            new Thread(() =>
            {
                try
                {
                    while (ev.WaitOne()) window.Dispatcher.BeginInvoke(window.ShowFromTray);
                }
                catch (ObjectDisposedException) { } // a app está a fechar
            }) { IsBackground = true }.Start();
        }

        if (args.Contains("--tray") && !screenshots) window.StartHidden();
        else window.Show();
    }

    /// <summary>Liberta o "só uma cópia" (antes de abrir outra cópia da app, ex.: a instalada).</summary>
    public void ReleaseSingleInstance()
    {
        try { _single?.ReleaseMutex(); } catch { }
        _single?.Dispose();
        _single = null;
        _showEvent?.Dispose();
        _showEvent = null;
    }

    async Task InstallFromCommandLineAsync(bool desktop)
    {
        try
        {
            await Installer.InstallAsync(desktop, PCOptimizer.App.MainWindow.AppVersion);
            Shutdown(0);
        }
        catch (Exception ex)
        {
            Log(ex);
            Shutdown(1);
        }
    }

    async Task UpdateFromCommandLineAsync()
    {
        try
        {
            Updater.AppVersion = new Version(PCOptimizer.App.MainWindow.AppVersion);
            var r = await Updater.CheckAsync();
            if (r.State == UpdateState.Available && r.Release is ReleaseInfo rel)
                Updater.Apply(await Updater.PrepareAsync(rel, null));
            Shutdown(r.State is UpdateState.Available or UpdateState.UpToDate ? 0 : 1);
        }
        catch (Exception ex)
        {
            Log(ex);
            Shutdown(1);
        }
    }

    static void RunUninstall()
    {
        if (MessageBox.Show(Loc.T("Desinstalar o Rigwise?\n\nOs ajustes que aplicaste continuam como estão. Se quiseres voltar ao estado original, ") +
                            Loc.T("abre a app e usa \"Reverter tudo\" na Otimização antes de desinstalar."),
                Loc.T("Desinstalar Rigwise"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var removeData = MessageBox.Show(Loc.T("Apagar também os dados da app (histórico, definições e backups dos ajustes)?\n\n") +
                                         Loc.T("Se disseres que não, ficam guardados para uma futura instalação."),
            Loc.T("Desinstalar Rigwise"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        try
        {
            Installer.Uninstall(removeData);
            MessageBox.Show(Loc.T("O Rigwise foi desinstalado."), "Rigwise");
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.T("Não foi possível desinstalar: ") + ex.Message, "Rigwise");
        }
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            // não deixa o registo crescer para sempre: acima de 1 MB passa a error.old.log
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > 1 << 20) File.Move(LogPath, Path.ChangeExtension(LogPath, ".old.log"), true);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{PCOptimizer.App.MainWindow.AppVersion} {ex}\n\n");
        }
        catch { }
    }
}
