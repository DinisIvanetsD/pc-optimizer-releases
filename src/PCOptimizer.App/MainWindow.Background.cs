using System.Windows;
using System.Windows.Threading;
using PCOptimizer.Core.Diagnostics;
using PCOptimizer.Core.Maintenance;
using PCOptimizer.Core.Network;
using PCOptimizer.Core.Updates;
using PCOptimizer.Core.Util;
using Forms = System.Windows.Forms;

namespace PCOptimizer.App;

/// <summary>Trabalho que corre sozinho: arranque, manutenção periódica, atualizações e notificações.</summary>
public partial class MainWindow
{
    bool _startupDone;
    bool _startupLoaded, _networkLoaded;
    DispatcherTimer? _maintenanceTimer;
    bool _maintenanceRunning;
    ReleaseInfo? _availableUpdate;
    string _balloonTab = "monitor";

    /// <summary>Arranque com o Windows: fica só na barra de tarefas, mas vigia e faz a manutenção.</summary>
    public void StartHidden() => _ = StartupWorkAsync();

    /// <summary>Corre uma vez, quer a janela apareça quer não.</summary>
    async Task StartupWorkAsync()
    {
        if (_startupDone) return;
        _startupDone = true;
        await StartGamingAsync();
        await RunScanAsync();
        await NotifyNewCrashAsync();

        _maintenanceTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _maintenanceTimer.Tick += async (_, _) => { await NotifyNewCrashAsync(); await MaintenanceIfDueAsync(); };
        _maintenanceTimer.Start();
        // a primeira verificação espera uns minutos para não pesar no arranque do Windows
        // o Windows só regista o ecrã azul depois de ler o minidump, às vezes já com a app aberta: volta a ver
        _ = Task.Delay(TimeSpan.FromMinutes(3)).ContinueWith(_ => Dispatcher.BeginInvoke(async () => { await NotifyNewCrashAsync(); await MaintenanceIfDueAsync(); }));
        _ = NotifyHeavyAdblockAsync();

        if (S.CheckUpdates && (S.LastUpdateCheck is not DateTime last || DateTime.Now - last > TimeSpan.FromHours(20)))
            _ = CheckUpdatesAsync(quiet: true);
    }

    // ------------------------------------------------------------------ abas que só carregam quando abertas

    async Task EnsureStartupTabLoadedAsync()
    {
        if (_startupLoaded) return;
        _startupLoaded = true;
        await LoadStartupAsync();
        _lastBoot = await Task.Run(PCOptimizer.Core.Benchmark.PcMetrics.LastBoot);
        UpdateStartupSummary();
    }

    async Task EnsureNetworkTabLoadedAsync()
    {
        if (_networkLoaded) return;
        _networkLoaded = true;
        RefreshAdblock();
        await RefreshQosAsync();
        RefreshSyncStatus();
        await LoadNicAsync();
    }

    // ------------------------------------------------------------------ notificações

    void Notify(string title, string text, string tab, bool important)
    {
        if (!S.Notifications || _tray is null) return;
        _balloonTab = tab;
        _tray.ShowBalloonTip(10_000, title, text, important ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    void OpenBalloonTarget()
    {
        ShowFromTray();
        Tabs.SelectedItem = _balloonTab switch
        {
            "drivers" => DriversTab,
            "diag" => DiagTab,
            "storage" => StorageTab,
            "settings" => SettingsTab,
            "history" => HistoryTab,
            "network" => NetworkTab,
            _ => MonitorTab,
        };
    }

    /// <summary>
    /// Depois de um ecrã azul o PC reinicia e a app arranca com o Windows: avisa logo,
    /// sem esperar pela manutenção (que só avisa de problemas críticos e uma vez por dia).
    /// </summary>
    async Task NotifyNewCrashAsync()
    {
        if (ScreenshotMode) return;
        try
        {
            var window = TimeSpan.FromDays(2);
            if (DateTime.Now - EventLogCheck.Since < window) window = DateTime.Now - EventLogCheck.Since;
            var last = (await Task.Run(() => EventLogCheck.BlueScreenEvents(window))).FirstOrDefault();
            if (last is null || (S.LastCrashNotified is DateTime seen && last.When <= seen)) return;
            var backlog = S.LastCrashNotified is null && DateTime.Now - last.When > TimeSpan.FromHours(6);
            S.LastCrashNotified = last.When;
            S.Save();
            if (backlog) return; // primeira vez com esta versão: um crash de ontem não é novidade
            var hint = CrashDumps.ParameterHint(last) ?? Loc.T("abre o Diagnóstico para ver a causa");
            Notify(Loc.T($"O PC teve um ecrã azul {EventLogCheck.WhenText(last.When)}"), $"{last.CodeName}: {hint}", "diag", true);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>A lista antiga de anúncios (70 mil+ domínios) deixa as apps lentas a ligar-se: avisa uma vez.</summary>
    async Task NotifyHeavyAdblockAsync()
    {
        if (ScreenshotMode) return;
        try
        {
            var count = await Task.Run(HostsBlocker.BlockedCount);
            if (count <= HostsBlocker.TooMany || !S.NotifiedKeys.Add("hosts:heavy")) return;
            S.Save();
            Notify(Loc.T("O bloqueio de anúncios está a deixar a internet lenta"), Loc.T("A lista antiga é grande demais. Abre a aba Rede e carrega em Atualizar lista."), "network", true);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // ------------------------------------------------------------------ manutenção

    async Task MaintenanceIfDueAsync()
    {
        if (MaintenanceRunner.IsDue(S)) await RunMaintenanceAsync(manual: false);
    }

    async void RunMaintenance_Click(object sender, RoutedEventArgs e) => await RunMaintenanceAsync(manual: true);

    async Task RunMaintenanceAsync(bool manual)
    {
        if (_maintenanceRunning) return;
        _maintenanceRunning = true;
        RunMaintenanceButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(t => MaintenanceStatus.Text = t);
            var notices = await MaintenanceRunner.RunAsync(S, progress);
            foreach (var n in notices.OrderByDescending(n => n.Important).Take(2))
                Notify(n.Title, n.Text, n.Tab, n.Important);
            if (manual)
                MessageBox.Show(this, notices.Count == 0
                        ? Loc.T("Verificação feita: nada de novo para te avisar ✓")
                        : Loc.T("Verificação feita:\n\n") + string.Join("\n\n", notices.Select(n => $"{n.Title}\n{n.Text}")),
                    "Rigwise", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            if (manual) MaintenanceStatus.Text = Loc.T("A verificação falhou: ") + ex.Message;
        }
        finally
        {
            _maintenanceRunning = false;
            RunMaintenanceButton.IsEnabled = true;
            if (Tabs.SelectedItem == SettingsTab) LoadSettingsPage();
        }
    }

    // ------------------------------------------------------------------ atualizações

    async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate != null) await InstallUpdateAsync(_availableUpdate);
        else await CheckUpdatesAsync(quiet: false);
    }

    async Task CheckUpdatesAsync(bool quiet)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatus.Text = "A procurar…";
        var r = await Updater.CheckAsync();
        S.LastUpdateCheck = DateTime.Now;
        S.Save();
        CheckUpdateButton.IsEnabled = true;
        UpdateStatus.Text = r.Message;

        if (r.State == UpdateState.Available && r.Release is ReleaseInfo rel)
        {
            _availableUpdate = rel;
            CheckUpdateButton.Content = Loc.T($"⬇  Instalar {rel.Version.ToString(3)}");
            CheckUpdateButton.Style = (Style)FindResource("PrimaryButton");
            UpdateBadge.Visibility = Visibility.Visible;
            if (quiet && S.SkippedVersion != rel.Tag)
                Notify(Loc.T("Há uma versão nova do Rigwise"), Loc.T($"Versão {rel.Version.ToString(3)}. Abre as Definições para atualizar."), "settings", false);
        }
    }

    void UpdateFromZip_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("Escolhe o ZIP da versão nova do Rigwise"),
            Filter = Loc.T("ZIP do Rigwise|*.zip"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var (version, script) = Updater.PrepareFromLocalZip(dlg.FileName);
            var current = Updater.AppVersion;
            var what = version is null ? Loc.T("a versão deste ZIP") : Loc.T($"a versão {version.ToString(3)}");
            var warn = version is not null && version < current ? Loc.T($"\n\n⚠ É mais antiga do que a que tens ({current.ToString(3)}).") : "";
            if (MessageBox.Show(this, Loc.T($"Instalar {what}?{warn}\n\nA app fecha, atualiza-se e volta a abrir sozinha. As tuas definições e o histórico ficam."),
                    Loc.T("Atualizar Rigwise"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            Updater.Apply(script);
            _exiting = true;
            Close();
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = Ui.Error(ex, Loc.T("Não deu para atualizar: "));
        }
    }

    async Task InstallUpdateAsync(ReleaseInfo rel)
    {
        var notes = string.IsNullOrWhiteSpace(rel.Notes) ? "" : "\n\nNovidades:\n" + (rel.Notes.Length > 600 ? rel.Notes[..600] + "…" : rel.Notes);
        var answer = MessageBox.Show(this, Loc.T($"Atualizar para a versão {rel.Version.ToString(3)}?{notes}\n\nA app fecha, atualiza-se e volta a abrir sozinha."),
            Loc.T("Atualizar Rigwise"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.No)
        {
            S.SkippedVersion = rel.Tag; // não volta a avisar desta versão
            S.Save();
            return;
        }
        if (answer != MessageBoxResult.Yes) return;

        CheckUpdateButton.IsEnabled = false;
        try
        {
            var script = await Updater.PrepareAsync(rel, new Progress<double>(p => UpdateStatus.Text = $"A descarregar… {p:N0}%"));
            Updater.Apply(script);
            _exiting = true;
            Close();
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = Ui.Error(ex, Loc.T("A atualização falhou: "));
            CheckUpdateButton.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ instalar no PC

    void UpdateInstallCard()
    {
        InstallCard.Visibility = Installer.IsRunningInstalled ? Visibility.Collapsed : Visibility.Visible;
        InstallText.Text = Installer.IsInstalled
            ? Loc.T($"Já há uma cópia instalada em {Installer.InstallDir}, mas estás a usar outra (em {AppContext.BaseDirectory.TrimEnd('\\')}). Instalar atualiza a cópia instalada.")
            : Loc.T($"Estás a usar a app a partir de {AppContext.BaseDirectory.TrimEnd('\\')}. Instalar copia-a para {Installer.InstallDir}, cria o atalho no Menu Iniciar e põe-na em Definições › Aplicações (para desinstalar). Não precisa de administrador.");
    }

    async void InstallApp_Click(object sender, RoutedEventArgs e)
    {
        var desktop = MessageBox.Show(this, Loc.T("Criar também um atalho no Ambiente de Trabalho?"), Loc.T("Instalar Rigwise"),
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        InstallButton.IsEnabled = false;
        try
        {
            var exe = await Installer.InstallAsync(desktop, AppVersion);
            if (MessageBox.Show(this, Loc.T("Instalada ✓\n\nA partir de agora abre a app pelo Menu Iniciar. Abrir a versão instalada agora (e fechar esta)?"),
                    "Rigwise", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                ((App)Application.Current).ReleaseSingleInstance(); // senão a cópia nova pensa que já há uma aberta
                Shell.Open(exe);
                _exiting = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            InstallText.Text = Ui.Error(ex, Loc.T("Não foi possível instalar: "));
        }
        finally
        {
            InstallButton.IsEnabled = true;
        }
    }
}
