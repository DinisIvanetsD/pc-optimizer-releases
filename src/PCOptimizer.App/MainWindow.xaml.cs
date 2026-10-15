using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCOptimizer.Core;
using PCOptimizer.Core.Benchmark;
using PCOptimizer.Core.Diagnostics;
using PCOptimizer.Core.Memory;
using PCOptimizer.Core.Monitoring;
using PCOptimizer.Core.Network;
using PCOptimizer.Core.Optimization;
using PCOptimizer.Core.Util;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace PCOptimizer.App;

public partial class MainWindow : Window
{
    readonly ObservableCollection<FindingVm> _findings = new();
    readonly ObservableCollection<ProfileVm> _profiles = new();
    readonly ObservableCollection<StartupVm> _startup = new();
    readonly ObservableCollection<AppTrend> _trends = new();
    readonly ObservableCollection<FindingVm> _alerts = new();
    readonly ObservableCollection<FindingVm> _netFindings = new();
    readonly ObservableCollection<string> _qos = new();
    readonly ResourceMonitor _monitor = new();
    Forms.NotifyIcon? _tray;
    ProfileVm? _currentProfile;
    List<DnsResult>? _lastDns;
    bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        Localizer.TranslateTabs(Tabs);
        WindowTheme.Apply(this);
        PCOptimizer.Core.Updates.Updater.AppVersion = new Version(AppVersion);
        ApplySettingsAtStartup();
        RestoreWindowBounds();
        SetupShortcuts();
        VersionText.Text = Loc.T($"Versão {AppVersion}") + (Admin.IsElevated ? " · administrador" : "");

        FindingsList.ItemsSource = _findings;
        StartupList.ItemsSource = _startup;
        TrendList.ItemsSource = _trends;
        AlertList.ItemsSource = _alerts;
        NetFindings.ItemsSource = _netFindings;
        QosList.ItemsSource = _qos;

        AdminText.Text = Admin.IsElevated ? "🛡 Administrador" : Loc.T("Utilizador normal");
        AdminText.Foreground = Admin.IsElevated ? Palette.Ok : Palette.Warning;
        ElevateButton.Visibility = Admin.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        AdminBadge.Visibility = Admin.IsElevated ? Visibility.Visible : Visibility.Collapsed; // em modo normal o botão já diz tudo

        UiHooks.RestartAsAdmin = () => Dispatcher.Invoke(() =>
        {
            if (!Admin.IsElevated && Admin.RestartElevated("--diag")) { _exiting = true; Close(); }
        });
        UiHooks.Navigate = tab => Dispatcher.Invoke(() => Tabs.SelectedItem = tab switch
        {
            "startup" => StartupTab,
            "optimize" => OptimizeTab,
            "ramtest" => RamTab,
            "network" => NetworkTab,
            "drivers" => DriversTab,
            // sem cabeçalhos de abas, a TabControl não escolhe a primeira sozinha (ficava tudo vazio)
            _ => HomeTab,
        });

        foreach (var p in TweakCatalog.Profiles) _profiles.Add(new ProfileVm(p));
        ProfileList.ItemsSource = _profiles;
        ProfileList.SelectedIndex = 0;

        ServerCombo.ItemsSource = GameServers.Presets;
        ServerCombo.SelectedIndex = 0;

        _monitor.Updated += s => Dispatcher.BeginInvoke(() => OnMonitorUpdate(s));
        _monitor.Alert += f => Dispatcher.BeginInvoke(() => OnAlert(f));
        _monitor.Start();
        StartConnectionWatcher();

        SetupTray();

        Loaded += async (_, _) =>
        {
            // o Início mostra logo o gráfico e o antes/depois; a análise preenche o resto quando acaba
            RefreshHome();
            var args = Environment.GetCommandLineArgs();
            if (Array.IndexOf(args, "--screenshots") < 0)
                _ = Dispatcher.BeginInvoke(ShowWelcomeIfFirstRun, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            else
            {
                // as capturas precisam de tudo carregado
                await EnsureStartupTabLoadedAsync();
                await EnsureNetworkTabLoadedAsync();
            }
            if (Array.IndexOf(args, "--diag") >= 0) Tabs.SelectedItem = DiagTab; // veio do "Descobrir o driver"
            await StartupWorkAsync();

            var i = Array.IndexOf(args, "--screenshots");
            var only = Array.IndexOf(args, "--only");
            // --size 1920x1040: capturas noutro tamanho de janela (ex.: ecrã cheio)
            var size = Array.IndexOf(args, "--size");
            if (i >= 0 && size >= 0 && size + 1 < args.Length && args[size + 1].Split('x') is [var w, var h]
                && double.TryParse(w, out var width) && double.TryParse(h, out var height))
            {
                WindowState = WindowState.Normal;
                Width = width;
                Height = height;
                await Task.Delay(500);
            }
            if (i >= 0 && i + 1 < args.Length && only >= 0 && only + 1 < args.Length)
                await SaveTabScreenshotsAsync(args[i + 1], args[only + 1]);
            else if (i >= 0 && i + 1 < args.Length)
                await SaveScreenshotsAsync(args[i + 1]);
        };
    }

    async Task CaptureAsync(string folder, string file)
    {
        await CaptureElementAsync((FrameworkElement)Content, Path.Combine(folder, file));
    }

    async Task CaptureElementAsync(FrameworkElement element, string path)
    {
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)element.ActualWidth, (int)element.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(element);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        await using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Para desenvolvimento: só uma aba (topo e fundo), mais rápido do que todas.</summary>
    async Task SaveTabScreenshotsAsync(string folder, string tabName)
    {
        Directory.CreateDirectory(folder);
        // "start": a página tal como abre, sem escolher aba (apanha o ecrã vazio no arranque)
        if (tabName == "start") await CaptureAsync(folder, "start.png");
        else if (tabName == "timeline") { Tabs.SelectedItem = HistoryTab; HistModeTimeline.IsChecked = true; for (var n = 0; n < 60 && _timeline is null; n++) await Task.Delay(500); await Task.Delay(800); await CaptureAsync(folder, "timeline.png"); }
        else if (tabName == "tutorial") { StartTutorial(); await Task.Delay(1500); await CaptureAsync(folder, "tutorial-page.png"); await CaptureElementAsync(_tutorial!.Card, Path.Combine(folder, "tutorial-card.png")); }
        else if (FindName(tabName) is TabItem tab)
        {
            Tabs.SelectedItem = tab;
            await Task.Delay(2500);
            if (tab == DriversTab)
                for (var n = 0; n < 60 && _driversBusy; n++) await Task.Delay(1000);
            await CaptureAsync(folder, $"{tabName}.png");
            var sv = tab.Content as ScrollViewer ?? FindDescendant<ScrollViewer>(tab.Content as DependencyObject);
            if (sv is { ScrollableHeight: > 0 })
            {
                sv.ScrollToEnd();
                await Task.Delay(800);
                await CaptureAsync(folder, $"{tabName}_bottom.png");
            }
        }
        _exiting = true;
        Close();
    }

    static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null) return null;
        for (var n = 0; n < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); n++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, n);
            if (c is T t) return t;
            if (FindDescendant<T>(c) is T found) return found;
        }
        return null;
    }

    /// <summary>Para desenvolvimento: guarda uma imagem de cada aba e fecha a app.</summary>
    async Task SaveScreenshotsAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        await Task.Delay(TimeSpan.FromSeconds(12)); // dar tempo ao monitor e aos estados dos ajustes
        foreach (TabItem tab in Tabs.Items)
        {
            Tabs.SelectedItem = tab;
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await Task.Delay(1500);
            var content = (FrameworkElement)Content;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)content.ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(content);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            await using var fs = File.Create(Path.Combine(folder, $"{tab.Name}.png"));
            enc.Save(fs);
        }
        // também a parte de baixo da aba de RAM (o estabilizador)
        Tabs.SelectedItem = RamTab;
        await Task.Delay(2500);
        if (RamTab.Content is ScrollViewer sv) sv.ScrollToEnd();
        await Task.Delay(800);
        {
            var content = (FrameworkElement)Content;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)content.ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(content);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            await using var fs = File.Create(Path.Combine(folder, "RamTab_bottom.png"));
            enc.Save(fs);
        }
        // drivers: espera pelo Windows Update e captura o resto da página
        Tabs.SelectedItem = DriversTab;
        for (var i = 0; i < 60 && _driversBusy; i++) await Task.Delay(1000);
        await Task.Delay(1000);
        await CaptureAsync(folder, "Drivers_top.png");
        if (DriversTab.Content is ScrollViewer dsv)
        {
            dsv.ScrollToVerticalOffset(dsv.ViewportHeight * 0.9);
            await Task.Delay(800);
            await CaptureAsync(folder, "Drivers_mid.png");
            dsv.ScrollToEnd();
            await Task.Delay(800);
            await CaptureAsync(folder, "Drivers_bottom.png");
        }
        // a página de espaço em disco precisa de mais tempo para carregar
        Tabs.SelectedItem = StorageTab;
        await Task.Delay(9000);
        await CaptureAsync(folder, "Storage_apps.png");
        SegCleanup.IsChecked = true;
        await Task.Delay(6000);
        await CaptureAsync(folder, "Storage_cleanup.png");
        SegFolders.IsChecked = true;
        var local = await PCOptimizer.Core.Storage.FolderScanner.ScanAsync(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), null, CancellationToken.None);
        ShowFolder(local);
        await Task.Delay(1500);
        await CaptureAsync(folder, "Storage_folders.png");
        _scanRoot = local;
        FolderModeBar.Visibility = Visibility.Visible;
        FolderModeSafe.IsChecked = true;
        await Task.Delay(1500);
        await CaptureAsync(folder, "Storage_safe.png");
        SegApps.IsChecked = true;
        UtilitySlider.Value = 40;
        SelectAllApps.IsChecked = true;
        SelectAllApps_Click(this, new RoutedEventArgs());
        await Task.Delay(1500);
        await CaptureAsync(folder, "Storage_select.png");
        SelectAllApps.IsChecked = false;
        SelectAllApps_Click(this, new RoutedEventArgs());

        Tabs.SelectedItem = DiagTab;
        var overlay = new GameOverlay(null, null);
        overlay.ShowSample(new PCOptimizer.Core.Gaming.LiveSample(23, 24, 0, 2.1, false));
        foreach (var (name, win) in new (string, Window)[]
                 {
                     ("Welcome", new WelcomeWindow { Owner = this }), ("GamePrompt", new GamePrompt("VALORANT")), ("GameOverlay", overlay),
                 })
        {
            win.Show();
            await Task.Delay(2500);
            // desenha através de um VisualBrush: o RenderTargetBitmap direto desloca o conteúdo pela margem
            var root = (FrameworkElement)win.Content;
            var m = root.Margin;
            int w = (int)(root.ActualWidth + m.Left + m.Right), h = (int)(root.ActualHeight + m.Top + m.Bottom);
            var dv = new System.Windows.Media.DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle((System.Windows.Media.Brush)FindResource("BgBrush"), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, new Rect(m.Left, m.Top, root.ActualWidth, root.ActualHeight));
            }
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            await using (var fs = File.Create(Path.Combine(folder, $"{name}.png"))) enc.Save(fs);
            win.Close();
        }
        _exiting = true;
        Close();
    }

    // ============================== Geral ==============================

    static bool Confirm(string text, string title = "Rigwise") =>
        MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    bool EnsureAdmin()
    {
        if (Admin.IsElevated) return true;
        if (Confirm(Loc.T("Isto precisa de permissões de administrador.\n\nQueres reabrir a app como administrador?")))
            Elevate_Click(this, new RoutedEventArgs());
        return false;
    }

    void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (!Admin.RestartElevated("--elevated")) return;
        _exiting = true;
        Close();
    }

    void SetupTray()
    {
        _tray = new Forms.NotifyIcon { Text = "Rigwise", Icon = LoadAppIcon(), Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("Abrir"), null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Sair", null, (_, _) => Dispatcher.Invoke(() =>
        {
            _exiting = true;
            Close();
        }));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        _tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(OpenBalloonTarget);
    }

    static Drawing.Icon LoadAppIcon()
    {
        var res = Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
        return res is null ? Drawing.SystemIcons.Application : new Drawing.Icon(res.Stream, 32, 32);
    }

    static Drawing.Icon MakeIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(79, 140, 255));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var font = new Drawing.Font("Segoe UI", 16, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var sf = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
            g.DrawString("P", font, Drawing.Brushes.White, new Drawing.RectangleF(0, 1, 32, 32), sf);
        }
        return Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    bool _closed;

    protected override void OnClosed(EventArgs e)
    {
        _closed = true; // um pedido para mostrar a janela (ícone, segunda instância) pode chegar depois: já não há janela
        base.OnClosed(e);
    }

    public void ShowFromTray()
    {
        if (_closed) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (IsLoaded && Array.IndexOf(Environment.GetCommandLineArgs(), "--screenshots") < 0) SaveWindowBounds();
        if (!_exiting && BackgroundCheck.IsChecked == true)
        {
            e.Cancel = true;
            Hide();
            _tray?.ShowBalloonTip(3000, "Rigwise", Loc.T("Continuo a vigiar o PC em segundo plano. Clica duas vezes no ícone para abrir."), Forms.ToolTipIcon.Info);
            return;
        }
        _ramCts?.Cancel();
        StopGaming();
        _monitor.Dispose();
        _watcher.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.OnClosing(e);
        Application.Current.Shutdown();
    }

    async void FixAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ActionVm vm } button) return;
        var action = vm.Action;
        if (action.ConfirmText != null && !Confirm(action.ConfirmText)) return;
        if (action.RequiresAdmin && !EnsureAdmin()) return;
        button.IsEnabled = false;
        try
        {
            var result = await action.Run();
            if (!string.IsNullOrEmpty(result)) vm.Owner.Message = result;
        }
        catch (Exception ex)
        {
            vm.Owner.Message = Ui.Error(ex);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    // ============================== Diagnóstico ==============================

    async void Scan_Click(object sender, RoutedEventArgs e) => await RunScanAsync();

    async Task RunScanAsync()
    {
        ScanButton.IsEnabled = false;
        _findings.Clear();
        SummaryPanel.Visibility = Visibility.Collapsed;
        try
        {
            var results = await DiagnosticsEngine.RunAsync(new Progress<string>(s => ScanStatus.Text = HomeScanText.Text = s));
            foreach (var f in results) _findings.Add(new FindingVm(f));
            var crit = results.Count(f => f.Severity == Severity.Critical);
            var warn = results.Count(f => f.Severity == Severity.Warning);
            var ok = results.Count(f => f.Severity == Severity.Ok);
            CritCount.Text = Loc.T($"{crit} crítico(s)");
            WarnCount.Text = Loc.T($"{warn} aviso(s)");
            OkCount.Text = $"{ok} OK";
            SummaryPanel.Visibility = Visibility.Visible;
            _lastScan = DateTime.Now;
            UpdateHealth(crit, warn);
            RefreshHome();
            ScanStatus.Text = crit + warn == 0
                ? Loc.T($"Análise concluída às {DateTime.Now:HH:mm}. Não encontrei problemas! 🎉")
                : Loc.T($"Análise concluída às {DateTime.Now:HH:mm}. Encontrei {crit + warn} coisa(s) a resolver.");
        }
        catch (Exception ex)
        {
            ScanStatus.Text = Loc.T("Erro na análise: ") + ex.Message;
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    async void Tool_Click(object sender, RoutedEventArgs e)
    {
        var tool = (sender as Button)?.Tag as string;
        Func<Task<string>>? run = tool switch
        {
            "sfc" => Tools.SystemFileCheck,
            "dism" => Tools.DismRepair,
            "mem" => () => { Tabs.SelectedItem = RamTab; return Task.FromResult(""); },
            "temp" => Tools.CleanTemp,
            _ => null,
        };
        if (run is null) return;
        if (tool == "temp" && !Confirm(Loc.T("Apagar os ficheiros temporários com mais de 1 dia?\n\nOs ficheiros em uso são ignorados."))) return;
        ScanStatus.Text = await run();
    }

    // ============================== Otimização ==============================

    async void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileVm p) return;
        _currentProfile = p;
        ProfileTitle.Text = $"{p.Icon}  {p.Name}";
        ProfileSummary.Text = p.Summary + "  " + Loc.T($"{p.Tweaks.Count} ajustes, cada um explicado em baixo. Marca os que queres e carrega em Aplicar.");
        ProfileNotes.Text = string.IsNullOrEmpty(p.Notes) ? "" : "💡  " + p.Notes;
        ProfileNotesBox.Visibility = string.IsNullOrEmpty(p.Notes) ? Visibility.Collapsed : Visibility.Visible;
        TweakList.ItemsSource = p.Tweaks;
        OptimizeStatus.Text = "";
        await Task.WhenAll(p.Tweaks.Select(t => t.RefreshAsync()));
    }

    async void ApplyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProfile is null) return;
        var selected = _currentProfile.Tweaks.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            OptimizeStatus.Text = Loc.T("Escolhe pelo menos um ajuste.");
            return;
        }
        if (selected.Any(t => t.RequiresAdmin) && !EnsureAdmin()) return;

        var list = string.Join("\n", selected.Select(t => $"• {t.Name}" + (t.Tweak.Risk == Risk.High ? Loc.T("  ⚠ RISCO ALTO") : "")));
        // perfil Gaming sem nenhum teste Antes: sugere medir primeiro (não bloqueia)
        var fpsHint = _currentProfile.Profile.Id == "gaming" && !FpsHistory.All.Any(r => r.Role == FpsRole.Before)
            ? Loc.T("\n\nDica: mede os FPS antes (aba FPS) para provar o ganho.") : "";
        if (!Confirm(Loc.T($"Vou aplicar {selected.Count} ajuste(s) do perfil {_currentProfile.Name}:\n\n{list}\n\n") +
                     Loc.T("Guardo o estado atual de cada um, por isso podes reverter tudo depois. Continuar?") + fpsHint))
            return;

        ApplyButton.IsEnabled = false;
        try
        {
            if (!PCOptimizer.Core.Util.Simulation.Enabled)
            {
                OptimizeStatus.Text = Loc.T("A medir o PC antes de otimizar (para depois comparares)…");
                await PCOptimizer.Core.Benchmark.PcMetrics.EnsureBaselineAsync(_currentProfile.Name, _healthScore);
            }
            if (RestorePointCheck.IsChecked == true)
            {
                OptimizeStatus.Text = Loc.T("A criar ponto de restauro (pode demorar um minuto)…");
                OptimizeStatus.Text = await TweakEngine.CreateRestorePointAsync($"Rigwise – {_currentProfile.Name}");
            }

            int ok = 0, fail = 0;
            var reboot = false;
            foreach (var t in selected)
            {
                OptimizeStatus.Text = $"A aplicar: {t.Name}…";
                try
                {
                    await Task.Run(() => TweakEngine.ApplyAsync(t.Tweak));
                    t.Message = null;
                    ok++;
                    reboot |= t.RequiresReboot;
                }
                catch (Exception ex)
                {
                    t.Message = Ui.Error(ex, "✗ ");
                    fail++;
                }
            }
            await RefreshAllTweaksAsync();
            OptimizeStatus.Text = $"✓ {ok} ajuste(s) aplicado(s)" +
                                  (fail > 0 ? Loc.T($", {fail} falharam (vê a mensagem em cada um)") : "") +
                                  (reboot ? Loc.T(". Reinicia o PC para alguns terem efeito.") : ".");
        }
        finally
        {
            ApplyButton.IsEnabled = true;
        }
    }

    async void RevertProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProfile is null) return;
        var revertible = _currentProfile.Tweaks.Where(t => t.CanRevert).Reverse().ToList();
        if (revertible.Count == 0)
        {
            OptimizeStatus.Text = Loc.T("Não há nada para reverter neste perfil.");
            return;
        }
        if (revertible.Any(t => t.RequiresAdmin) && !EnsureAdmin()) return;
        if (!Confirm(Loc.T($"Repor o estado original de {revertible.Count} ajuste(s) deste perfil?"))) return;

        var ok = 0;
        foreach (var t in revertible)
        {
            try
            {
                if (await Task.Run(() => TweakEngine.RevertAsync(t.Tweak))) ok++;
                t.Message = null;
            }
            catch (Exception ex)
            {
                t.Message = Ui.Error(ex, "✗ ");
            }
        }
        await RefreshAllTweaksAsync();
        OptimizeStatus.Text = Loc.T($"↩ {ok} ajuste(s) revertido(s). Alguns só voltam ao normal depois de reiniciar.");
    }

    async void RevertTweak_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TweakVm t }) return;
        if (t.RequiresAdmin && !EnsureAdmin()) return;
        try
        {
            await Task.Run(() => TweakEngine.RevertAsync(t.Tweak));
            t.Message = Loc.T("↩ Revertido para o estado original.");
        }
        catch (Exception ex)
        {
            t.Message = Ui.Error(ex, "✗ ");
        }
        await RefreshAllTweaksAsync();
    }

    async void RevertAll_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotStore.Keys.Count == 0)
        {
            MessageBox.Show(Loc.T("Ainda não aplicaste nenhum ajuste, por isso não há nada para reverter."), "Rigwise");
            return;
        }
        if (!EnsureAdmin()) return;
        if (!Confirm(Loc.T("Repor TODOS os ajustes feitos por esta app ao estado original do PC?"))) return;
        var (ok, errors) = await Task.Run(TweakEngine.RevertAllAsync);
        await RefreshAllTweaksAsync();
        var msg = Loc.T($"{ok} ajuste(s) revertido(s). Reinicia o PC para tudo voltar ao normal.");
        if (errors.Count > 0) msg += "\n\nErros:\n" + string.Join("\n", errors);
        MessageBox.Show(msg, "Rigwise");
    }

    Task RefreshAllTweaksAsync() => Task.WhenAll(_profiles.SelectMany(p => p.Tweaks).Select(t => t.RefreshAsync()));

    // ============================== Arranque ==============================

    async void RefreshStartup_Click(object sender, RoutedEventArgs e) => await LoadStartupAsync();

    async Task LoadStartupAsync()
    {
        var items = await Task.Run(StartupManager.List);
        _startup.Clear();
        foreach (var i in items) _startup.Add(new StartupVm(i));
        UpdateStartupSummary();
    }

    (double Seconds, bool Exact)? _lastBoot;

    void UpdateStartupSummary()
    {
        var on = _startup.Count(s => s.Enabled);
        StartupSummary.Text = Loc.T($"{on} de {_startup.Count} programas arrancam com o Windows") +
            (_lastBoot is { } b ? Loc.T($" · último arranque: {b.Seconds:N0} s") : "");
    }

    void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: StartupVm vm } cb) return;
        var wanted = cb.IsChecked == true;
        if (vm.Item.RequiresAdmin && !Admin.IsElevated)
        {
            cb.IsChecked = vm.Enabled;
            EnsureAdmin();
            return;
        }
        try
        {
            StartupManager.SetEnabled(vm.Item, wanted);
            vm.Enabled = wanted;
            vm.Message = wanted ? Loc.T("Vai arrancar com o Windows.") : Loc.T("Já não arranca com o Windows (tem efeito no próximo arranque).");
        }
        catch (Exception ex)
        {
            cb.IsChecked = vm.Enabled;
            vm.Message = Ui.Error(ex);
        }
        UpdateStartupSummary();
    }

    // ============================== Monitor ==============================

    void OnMonitorUpdate(MonitorSnapshot s)
    {
        CpuText.Text = $"{s.CpuPercent:N0}%";
        CpuBar.Value = s.CpuPercent;
        RamText.Text = $"{s.Memory.LoadPercent}%  ·  {s.Memory.UsedGb:N1} / {s.Memory.TotalGb:N1} GB";
        RamBar.Value = s.Memory.LoadPercent;
        RamBar.Foreground = s.Memory.LoadPercent >= 90 ? Palette.Critical : s.Memory.LoadPercent >= 80 ? Palette.Warning : Palette.Info;
        CommitText.Text = $"{s.Memory.CommitPercent:N0}%";
        CommitBar.Value = s.Memory.CommitPercent;

        SideCpuText.Text = $"{s.CpuPercent:N0}%";
        SideCpuBar.Value = s.CpuPercent;
        SideRamText.Text = $"{s.Memory.LoadPercent}%";
        SideRamBar.Value = s.Memory.LoadPercent;
        SideRamBar.Foreground = RamBar.Foreground;

        _trends.Clear();
        foreach (var t in s.Apps) _trends.Add(t);
    }

    // ============================== Vigia de quebras de ligação ==============================

    readonly ConnectionWatcher _watcher = new();
    readonly ObservableCollection<ConnectionDrop> _drops = new();

    void StartConnectionWatcher()
    {
        DropsList.ItemsSource = _drops;
        foreach (var d in _watcher.Drops) _drops.Add(d);
        UpdateDropsView();

        _watcher.StatusChanged += s => Dispatcher.BeginInvoke(() =>
        {
            WatchStatus.Text = s;
            WatchStatus.Foreground = s.StartsWith('⚠') ? Palette.Critical : Palette.Ok;
        });
        _watcher.PortsUpdated += p => Dispatcher.BeginInvoke(() =>
        {
            var top = p.Top.FirstOrDefault();
            WatchPorts.Text = Loc.T($"Portas em uso agora: {p.TotalTcp:N0} TCP · {p.TotalUdp:N0} UDP (o limite padrão é {PortSnapshot.DefaultDynamicPorts:N0} de cada)") +
                              (top is null ? "" : Loc.T($"  ·  quem usa mais: {top.Name} ({top.Total:N0})"));
        });
        _watcher.DropDetected += d => Dispatcher.BeginInvoke(() =>
        {
            _drops.Insert(0, d);
            UpdateDropsView();
            if (NotifyCheck.IsChecked == true)
                _tray?.ShowBalloonTip(8000, Loc.T("A internet caiu"), $"{d.DurationText} · {d.CauseText}: {d.Detail}", Forms.ToolTipIcon.Warning);
        });
        _watcher.Start();

        _ = Task.Run(() => ConnectionWatcher.PastPortExhaustion(30)).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            var past = t.Result;
            if (past.Count == 0) return;
            ExhaustionBox.Visibility = Visibility.Visible;
            ExhaustionText.Text =
                Loc.T($"⚠ Nos últimos 30 dias o Windows ficou {past.Count} vezes sem portas de rede (a última em {past[0]:dd/MM 'às' HH:mm}). ") +
                Loc.T("Uma app abre milhares de ligações e bloqueia as outras. A correção dá 3× mais portas ao Windows.");
        }));
        _ = RefreshFixPortsAsync();
    }

    async Task RefreshFixPortsAsync()
    {
        var tweak = TweakCatalog.Get("net.dynports");
        if (await tweak.IsAppliedAsync() == true)
        {
            FixPortsButton.Visibility = Visibility.Collapsed;
            FixPortsStatus.Text = Loc.T("✓ Correção aplicada: o Windows tem agora 55 535 portas.");
        }
    }

    void UpdateDropsView()
    {
        NoDropsText.Visibility = _drops.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DropsTitle.Text = _drops.Count == 0 ? Loc.T("Quebras registadas") : Loc.T($"Quebras registadas ({_drops.Count})");
    }

    async void FixPorts_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdmin()) return;
        if (!Confirm(Loc.T("Aumentar as portas de rede do Windows de 16 384 para 55 535 (TCP e UDP)?\n\nPodes reverter a qualquer momento no perfil «Rede / Jogos online» da aba Otimização."))) return;
        try
        {
            await Task.Run(() => TweakEngine.ApplyAsync(TweakCatalog.Get("net.dynports")));
            FixPortsStatus.Text = Loc.T("✓ Feito. Tem efeito imediato para novas ligações.");
            FixPortsButton.Visibility = Visibility.Collapsed;
            await RefreshAllTweaksAsync();
        }
        catch (Exception ex)
        {
            FixPortsStatus.Text = Ui.Error(ex);
        }
    }

    void ClearDrops_Click(object sender, RoutedEventArgs e)
    {
        _watcher.ClearHistory();
        _drops.Clear();
        UpdateDropsView();
    }

    void OnAlert(Finding f)
    {
        _alerts.Insert(0, new FindingVm(f));
        while (_alerts.Count > 50) _alerts.RemoveAt(_alerts.Count - 1);
        NoAlertsText.Visibility = Visibility.Collapsed;
        if (NotifyCheck.IsChecked == true)
        {
            var icon = f.Severity == Severity.Critical ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Warning;
            _balloonTab = "monitor";
            _tray?.ShowBalloonTip(8000, f.Title, f.Description, icon);
        }
    }

    // ============================== Teste de RAM ==============================

    CancellationTokenSource? _ramCts;
    RamTestResult? _ramResult;
    readonly ObservableCollection<string> _ramErrors = new();

    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // o evento também sobe de ComboBoxes/ListBoxes dentro das abas
        if (e.Source != Tabs) return;
        AnimatePageIn();
        if (Tabs.SelectedItem == RamTab && _ramCts is null) RefreshRamAmount();
        if (Tabs.SelectedItem == StorageTab) _ = EnsureStorageLoadedAsync();
        if (Tabs.SelectedItem == DriversTab) _ = EnsureDriversLoadedAsync();
        if (Tabs.SelectedItem == HistoryTab) LoadHistory();
        if (Tabs.SelectedItem == HomeTab) RefreshHome();
        if (Tabs.SelectedItem == SettingsTab) { LoadSettingsPage(); UpdateInstallCard(); }
        if (Tabs.SelectedItem == StartupTab) _ = EnsureStartupTabLoadedAsync();
        if (Tabs.SelectedItem == NetworkTab) _ = EnsureNetworkTabLoadedAsync();
        if (Tabs.SelectedItem == FpsTab) RefreshFpsCard();
        if (Tabs.SelectedItem == AppsTab) _ = EnsureAppsTabLoadedAsync();
        if (Tabs.SelectedItem == DiagTab && RecHistory.ItemsSource is null) LoadRecordings();
        if (Tabs.SelectedItem == ComponentsTab) _ = EnsureComponentsLoadedAsync();

        // manter o menu sincronizado quando a página muda por código (ex.: "Ver programas de arranque")
        SyncNav();
    }

    void GoOptimize_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = OptimizeTab;

    void Health_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Tabs.SelectedItem = DiagTab;

    /// <summary>Pontuação simples de 0 a 100 a partir dos problemas encontrados.</summary>
    void UpdateHealth(int critical, int warnings)
    {
        var score = Math.Clamp(100 - critical * 20 - warnings * 6, 5, 100);
        _healthScore = score;
        PCOptimizer.Core.Benchmark.PcMetrics.RecordHealth(score);
        var brush = score >= 80 ? Palette.Ok : score >= 50 ? Palette.Warning : Palette.Critical;
        HealthScore.Text = score.ToString();
        HealthScore.Foreground = brush;
        HealthBar.Value = score;
        HealthBar.Foreground = brush;
        HealthText.Text = critical + warnings == 0
            ? Loc.T("Tudo em ordem 🎉")
            : Loc.T($"{critical} crítico(s) · {warnings} aviso(s) — vê o Diagnóstico");
    }

    RamReport? _ramReport;

    async Task LoadStabilizerAsync()
    {
        _ramReport = await Task.Run(RamStabilizer.Analyze);
        RamModulesList.ItemsSource = _ramReport.Modules;
        RamDiagnosis.Text = _ramReport.Diagnosis;
        RamWarningsList.ItemsSource = _ramReport.Warnings;
        RamStepsList.ItemsSource = _ramReport.Steps;
        XmpText.Text = _ramReport.XmpActive ? Loc.T("XMP/EXPO ligado") : Loc.T("Especificações padrão");
        XmpText.Foreground = _ramReport.XmpActive ? Palette.Warning : Palette.Ok;
        await RefreshAutoStabStatusAsync();
    }

    // ---------- Estabilizador automático ----------

    List<ulong>? _badPfns;

    async Task RefreshAutoStabStatusAsync()
    {
        GrantPrivButton.Visibility = Visibility.Collapsed;
        UnisolateButton.Visibility = Visibility.Collapsed;
        if (!Admin.IsElevated)
        {
            AutoStabStatus.Text = Loc.T("Precisa de administrador (para ler a memória física e mexer na lista de memória avariada do Windows).");
            return;
        }
        var lines = new List<string>();
        if (!await Task.Run(PhysicalRamTester.TryEnableLockPagesPrivilege))
        {
            GrantPrivButton.Visibility = Visibility.Visible;
            lines.Add(Loc.T("Passo 1: falta dar à app acesso à memória física (só uma vez; depois tens de terminar sessão)."));
        }
        else
        {
            lines.Add(Loc.T("✓ Acesso à memória física ativo. Pronto para analisar."));
        }
        try
        {
            var isolated = await BadMemoryList.ReadAsync();
            if (isolated.Count > 0)
                lines.Add(Loc.T($"🛡 O Windows já está a ignorar {isolated.Count} zona(s) de RAM avariada ({isolated.Count * 4} KB)."));
            UnisolateButton.Visibility = BadMemoryList.HasBackup ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
        AutoStabStatus.Text = string.Join("\n", lines);
    }

    void GrantPriv_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdmin()) return;
        if (!Confirm(Loc.T("Vou dar à tua conta do Windows o direito \"Bloquear páginas na memória\". É o que permite à app escolher páginas físicas concretas da RAM para testar (o mesmo que usam o SQL Server e alguns jogos).\n\n") +
                     Loc.T("Depois tens de TERMINAR SESSÃO (ou reiniciar) e voltar a abrir a app como administrador.\n\nContinuar?")))
            return;
        try
        {
            PhysicalRamTester.GrantLockPagesRight();
            AutoStabStatus.Text = Loc.T("✓ Feito. Agora termina sessão (ou reinicia o PC), volta a abrir a app como administrador e carrega em \"Analisar e isolar RAM avariada\".");
            GrantPrivButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AutoStabStatus.Text = Ui.Error(ex);
        }
    }

    async void AutoStab_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdmin()) return;
        if (!PhysicalRamTester.TryEnableLockPagesPrivilege())
        {
            await RefreshAutoStabStatusAsync();
            MessageBox.Show(Loc.T("Primeiro carrega em \"1. Ativar acesso à memória física\" e termina sessão."), "Rigwise");
            return;
        }
        var gb = RamAmountSlider.Value;
        if (!Confirm(Loc.T($"Vou testar {gb:N0} GB de RAM física com 2 passagens (cerca de {Math.Max(1, gb / 4):N0}–{Math.Max(2, gb / 2):N0} minutos) e ver se os erros aparecem sempre nos mesmos sítios.\n\n") +
                     Loc.T("Durante o teste o PC fica lento. Fecha jogos e o browser para testar mais RAM. Continuar?")))
            return;

        AutoStabButton.IsEnabled = RamStartButton.IsEnabled = false;
        IsolateButton.Visibility = Visibility.Collapsed;
        AutoStabResult.Visibility = Visibility.Collapsed;
        RamResultCard.Visibility = Visibility.Collapsed;
        RamStatusTitle.Text = Loc.T("A reservar memória física…");
        SetRamErrors(0);
        _monitor.SuppressRamAlerts = true;
        _ramCts = new CancellationTokenSource();
        RamStopButton.IsEnabled = true;

        var tester = new PhysicalRamTester();
        tester.Progress += p => Dispatcher.BeginInvoke(() =>
        {
            RamStatusTitle.Text = Loc.T($"Estabilizador · passagem {p.Pass} de {p.TotalPasses}");
            RamProgress.Value = p.PassPercent;
            RamDetails.Text = Loc.T($"{p.BytesTested / (double)(1L << 30):N1} GB de RAM física  ·  {p.GbPerSecond:N1} GB/s  ·  tempo {p.Elapsed:hh\\:mm\\:ss}");
            SetRamErrors(p.Errors);
        });

        try
        {
            var r = await tester.RunAsync((long)gb << 30, 2, Environment.ProcessorCount, _ramCts.Token);
            ShowStabilizerResult(r);
        }
        catch (Exception ex)
        {
            RamStatusTitle.Text = Ui.Error(ex);
        }
        finally
        {
            _monitor.SuppressRamAlerts = false;
            _ramCts = null;
            RamStopButton.IsEnabled = false;
            AutoStabButton.IsEnabled = RamStartButton.IsEnabled = true;
        }
    }

    void ShowStabilizerResult(PhysicalTestResult r)
    {
        var gb = r.BytesTested / (double)(1L << 30);
        RamStatusTitle.Text = r.Cancelled ? Loc.T("Análise parada") : Loc.T("Análise concluída");
        RamProgress.Value = r.Cancelled ? RamProgress.Value : 100;
        AutoStabResult.Visibility = Visibility.Visible;
        _badPfns = null;

        switch (r.Verdict)
        {
            case StabilityVerdict.Stable:
                AutoStabResult.Foreground = Palette.Ok;
                AutoStabResult.Text = Loc.T($"✓ Nenhuma zona avariada em {gb:N1} GB de RAM física ({r.PassesCompleted} passagens). ") +
                                      Loc.T("Esta parte da RAM está estável. Para cobrir o resto, repete com mais apps fechadas, ou usa o teste do Windows.");
                break;

            case StabilityVerdict.Localized:
                _badPfns = r.BadPages.Keys.ToList();
                AutoStabResult.Foreground = Palette.Warning;
                AutoStabResult.Text = Loc.T($"Encontrei {r.BadPages.Count} zona(s) avariada(s) ({r.BadBytes / 1024} KB de {gb:N1} GB), sempre nos mesmos sítios: ") +
                                      Loc.T("é um defeito físico num chip, e DÁ para isolar. Carrega em \"Isolar zonas avariadas\" e reinicia o PC. ") +
                                      Loc.T("O Windows deixa de as usar e perdes só esses KB.");
                IsolateButton.Content = Loc.T($"Isolar {r.BadPages.Count} zona(s) avariada(s)");
                IsolateButton.Visibility = Visibility.Visible;
                break;

            case StabilityVerdict.Unstable:
                AutoStabResult.Foreground = Palette.Critical;
                AutoStabResult.Text = Loc.T($"Encontrei {r.Errors:N0} erros em {r.BadPages.Count:N0} sítios diferentes, e mudam de sítio entre passagens. ") +
                                      Loc.T("Isto não é um chip avariado: é instabilidade (normalmente o XMP demasiado rápido). Isolar não resolve, porque ") +
                                      Loc.T("os erros apareceriam noutro sítio. A solução está na BIOS: abri o guia em baixo (o passo 1 costuma bastar).");
                BiosExpander.IsExpanded = true;
                break;
        }
    }

    async void Isolate_Click(object sender, RoutedEventArgs e)
    {
        if (_badPfns is null || !EnsureAdmin()) return;
        var bitlocker = BadMemoryList.IsBitLockerOn();
        if (!Confirm(Loc.T($"Vou dizer ao Windows para nunca mais usar estas {_badPfns.Count} zona(s) de RAM ({_badPfns.Count * 4} KB).\n\n") +
                     Loc.T("Tem efeito depois de reiniciar e podes desfazer a qualquer momento em \"Remover isolamento\".") +
                     (bitlocker ? Loc.T("\n\n⚠ O teu disco tem BitLocker: no próximo arranque o Windows pode pedir a chave de recuperação. Confirma que a tens (account.microsoft.com/devices/recoverykey) antes de continuar.") : "") +
                     "\n\nContinuar?"))
            return;
        try
        {
            var added = await BadMemoryList.AddAsync(_badPfns);
            AutoStabResult.Foreground = Palette.Ok;
            AutoStabResult.Text = Loc.T($"✓ {added} zona(s) isolada(s). Reinicia o PC para o Windows deixar de as usar. Depois, faz outra vez a análise: devem aparecer 0 erros novos.");
            IsolateButton.Visibility = Visibility.Collapsed;
            await RefreshAutoStabStatusAsync();
        }
        catch (Exception ex)
        {
            AutoStabResult.Text = Ui.Error(ex);
        }
    }

    async void Unisolate_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdmin()) return;
        if (!Confirm(Loc.T("Repor a lista de memória avariada do Windows como estava antes desta app? (Tem efeito depois de reiniciar.)"))) return;
        try
        {
            await BadMemoryList.RevertAsync();
            AutoStabResult.Visibility = Visibility.Visible;
            AutoStabResult.Foreground = Palette.Ok;
            AutoStabResult.Text = Loc.T("↩ Isolamento removido. Reinicia o PC para ter efeito.");
            await RefreshAutoStabStatusAsync();
        }
        catch (Exception ex)
        {
            AutoStabResult.Text = Ui.Error(ex);
        }
    }

    async void RebootBios_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAdmin()) return;
        if (!Confirm(Loc.T("O PC vai reiniciar e abrir diretamente a BIOS.\n\nGuarda o teu trabalho e fecha as apps antes. Dica: carrega em \"Copiar o guia\" primeiro, ou tira uma foto aos passos com o telemóvel.\n\nContinuar?")))
            return;
        try
        {
            StabilizerStatus.Text = await RamStabilizer.RebootToBiosAsync();
        }
        catch (Exception ex)
        {
            StabilizerStatus.Text = Ui.Error(ex);
        }
    }

    void CopyStabilizer_Click(object sender, RoutedEventArgs e)
    {
        if (_ramReport is null) return;
        var text = Loc.T("ESTABILIZADOR DE RAM — Rigwise\n\n") +
                   string.Join("\n", _ramReport.Modules) + "\n\n" + _ramReport.Diagnosis + "\n\n" +
                   string.Join("\n\n", _ramReport.Steps.Select(s => $"{s.Title}\n{s.Detail}" + (s.WhereInBios is null ? "" : Loc.T($"\nNa BIOS: {s.WhereInBios}"))));
        Clipboard.SetText(text);
        StabilizerStatus.Text = Loc.T("✓ Guia copiado. Cola-o no telemóvel ou no bloco de notas para o teres à mão na BIOS.");
    }

    void RefreshRamAmount()
    {
        if (_ramReport is null) _ = LoadStabilizerAsync();
        var mem = SystemInfo.GetMemory();
        var suggestedGb = Math.Floor(RamTester.SuggestedBytes() / (double)(1L << 30));
        var maxGb = Math.Max(1, Math.Floor(mem.AvailableBytes / (double)(1L << 30) - 0.5));
        RamAmountSlider.Maximum = maxGb;
        RamAmountSlider.Value = Math.Clamp(suggestedGb, 1, maxGb);
        RamFreeText.Text = Loc.T($"Livre agora: {mem.AvailableBytes / (double)(1L << 30):N1} GB de {mem.TotalGb:N1} GB. ") +
                           Loc.T("Recomendado deixar ~1,5 GB para o Windows. Fecha apps para conseguires testar mais.");
        UpdateRamAmountText();
    }

    void RamAmount_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateRamAmountText();

    void UpdateRamAmountText()
    {
        if (RamAmountText is null) return;
        var total = SystemInfo.GetMemory().TotalGb;
        RamAmountText.Text = Loc.T($"{RamAmountSlider.Value:N0} GB ({100 * RamAmountSlider.Value / total:N0}% da RAM)");
    }

    async void RamStart_Click(object sender, RoutedEventArgs e)
    {
        var passes = int.Parse((string)((ComboBoxItem)RamPassesCombo.SelectedItem).Tag);
        var bytes = (long)RamAmountSlider.Value << 30;
        if (!Confirm(Loc.T($"Vou testar {RamAmountSlider.Value:N0} GB de RAM ({(passes == 0 ? Loc.T("até parares") : passes + " passagem(ns)")}).\n\n") +
                     Loc.T("Durante o teste o PC pode ficar mais lento, porque a RAM livre fica ocupada. Guarda o teu trabalho e evita usar o PC. Continuar?")))
            return;

        _ramCts = new CancellationTokenSource();
        _ramErrors.Clear();
        RamErrorList.ItemsSource = _ramErrors;
        RamErrorsCard.Visibility = Visibility.Collapsed;
        RamResultCard.Visibility = Visibility.Collapsed;
        RamStartButton.IsEnabled = false;
        RamStopButton.IsEnabled = true;
        RamAmountSlider.IsEnabled = RamPassesCombo.IsEnabled = false;
        RamStatusTitle.Text = Loc.T("A reservar memória…");
        SetRamErrors(0);
        _monitor.SuppressRamAlerts = true; // o próprio teste enche a RAM de propósito

        var tester = new RamTester();
        tester.Progress += p => Dispatcher.BeginInvoke(() =>
        {
            RamStatusTitle.Text = $"Passagem {p.Pass}{(p.TotalPasses > 0 ? Loc.T($" de {p.TotalPasses}") : "")} · {p.Test}";
            RamProgress.Value = p.PassPercent;
            RamDetails.Text = Loc.T($"{p.BytesTested / (double)(1L << 30):N1} GB em teste  ·  {p.GbPerSecond:N1} GB/s  ·  tempo {p.Elapsed:hh\\:mm\\:ss}") +
                              (p.AllLocked ? "" : Loc.T("  ·  ⚠ parte da memória não ficou presa na RAM física"));
            SetRamErrors(p.Errors);
        });
        tester.ErrorFound += err => Dispatcher.BeginInvoke(() =>
        {
            if (_ramErrors.Count < 100) _ramErrors.Add(err.ToString());
            RamErrorsCard.Visibility = Visibility.Visible;
        });

        try
        {
            _ramResult = await tester.RunAsync(new RamTestOptions(bytes, passes, Environment.ProcessorCount), _ramCts.Token);
            ShowRamResult(_ramResult);
        }
        catch (Exception ex)
        {
            RamStatusTitle.Text = Ui.Error(ex);
        }
        finally
        {
            _monitor.SuppressRamAlerts = false;
            _ramCts = null;
            RamStartButton.IsEnabled = true;
            RamStopButton.IsEnabled = false;
            RamAmountSlider.IsEnabled = RamPassesCombo.IsEnabled = true;
        }
    }

    void RamStop_Click(object sender, RoutedEventArgs e)
    {
        _ramCts?.Cancel();
        RamStatusTitle.Text = "A parar…";
    }

    void SetRamErrors(long errors)
    {
        RamErrorText.Text = errors == 1 ? Loc.T("1 erro") : $"{errors:N0} erros";
        RamErrorBadge.Background = errors == 0 ? Palette.Ok : Palette.Critical;
    }

    void ShowRamResult(RamTestResult r)
    {
        var gb = r.BytesTested / (double)(1L << 30);
        RamStatusTitle.Text = r.Cancelled ? Loc.T("Teste parado") : Loc.T("Teste concluído");
        RamProgress.Value = r.Cancelled ? RamProgress.Value : 100;
        RamResultCard.Visibility = Visibility.Visible;

        if (r.Passed)
        {
            RamResultCard.BorderBrush = Palette.Ok;
            RamResultTitle.Text = r.PassesCompleted == 0
                ? Loc.T("Teste parado antes de completar uma passagem")
                : Loc.T($"✓ Nenhum erro em {gb:N1} GB e {r.PassesCompleted} passagem(ns) ({r.Elapsed:hh\\:mm\\:ss})");
            RamResultText.Text = r.PassesCompleted == 0
                ? Loc.T("Não há dados suficientes para tirar conclusões.")
                : Loc.T("A RAM testada está estável. Para mais certeza: faz 4 ou mais passagens, ou o teste do Windows, que testa 100% da RAM ") +
                  Loc.T("(este teste só cobre a RAM que estava livre). Se continuares a ter ecrãs azuis, a causa pode ser um driver: vê o Diagnóstico.");
        }
        else
        {
            RamResultCard.BorderBrush = Palette.Critical;
            RamResultTitle.Text = Loc.T($"✗ {r.Errors:N0} erro(s) de memória em {gb:N1} GB");
            var bits = r.BitHistogram.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"bit {kv.Key}").ToList();
            var concentrated = r.BitHistogram.Count <= 4;
            RamResultText.Text =
                Loc.T("A tua RAM devolveu dados diferentes dos que foram escritos. Isto explica ecrãs azuis como MEMORY_MANAGEMENT e PFN_LIST_CORRUPT.\n\n") +
                (concentrated
                    ? Loc.T($"Os erros estão sempre nos mesmos bits ({string.Join(", ", bits)}): é típico de um chip de memória avariado.\n\n")
                    : Loc.T("Os erros estão espalhados por muitos bits: é típico de instabilidade (XMP/EXPO, overclock, temperatura) e não de um chip avariado.\n\n")) +
                Loc.T("O que fazer, por ordem:\n") +
                Loc.T("1. Se tens XMP/EXPO ligado na BIOS, desliga-o (ou baixa a velocidade) e repete o teste.\n") +
                Loc.T("2. Atualiza a BIOS da motherboard.\n") +
                Loc.T("3. Testa um módulo de cada vez para descobrires qual falha. Desliga o PC da corrente antes de mexer na RAM.\n") +
                Loc.T("4. Um módulo que falha sozinho, nas velocidades de origem, está avariado: aciona a garantia.\n\n👇 O Estabilizador de RAM, mais abaixo, tem estes passos com os menus exatos da BIOS da tua motherboard.");
        }
    }

    static Finding RamFinding(RamTestResult r) => new()
    {
        Category = Loc.T("Teste de RAM"),
        Severity = r.Passed ? Severity.Ok : Severity.Critical,
        Title = Loc.T($"Teste de RAM: {r.Errors} erro(s) em {r.BytesTested / (double)(1L << 30):N1} GB, {r.PassesCompleted} passagem(ns)"),
        Description = Loc.T($"Bits com falhas: {string.Join(", ", r.BitHistogram.OrderBy(kv => kv.Key).Select(kv => $"bit {kv.Key} ×{kv.Value}"))}\n") +
                      Loc.T("Primeiros erros:\n") + string.Join("\n", r.FirstErrors.Take(10)),
    };

    void RamCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_ramResult is null) return;
        var f = RamFinding(_ramResult);
        Clipboard.SetText($"{f.Title}\n{f.Description}\n\n{PcSummary.Text()}");
        RamStatusTitle.Text = Loc.T("Relatório copiado.");
    }

    void WindowsMemTest_Click(object sender, RoutedEventArgs e)
    {
        if (Confirm(Loc.T("O teste do Windows obriga a reiniciar o PC e demora 10–20 minutos, mas testa 100% da RAM.\n\nGuarda o teu trabalho antes. Continuar?")))
            _ = Tools.MemoryTest();
    }

    // ============================== Rede ==============================

    void ServerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var custom = (ServerCombo.SelectedItem as GameServer)?.Name == GameServers.Custom;
        CustomHostBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
    }

    async void NetTest_Click(object sender, RoutedEventArgs e)
    {
        var server = ServerCombo.SelectedItem as GameServer;
        string? host = server?.Host;
        var label = server?.Name ?? "Servidor";
        if (server?.Name == GameServers.Custom)
        {
            host = CustomHostBox.Text.Trim();
            label = host;
            if (host.Length == 0)
            {
                NetStatus.Text = Loc.T("Escreve o IP ou o nome do servidor.");
                return;
            }
        }

        NetTestButton.IsEnabled = false;
        _netFindings.Clear();
        RouterStat.Text = InternetStat.Text = GameStat.Text = "…";
        GameLabel.Text = "🎮 " + label;
        try
        {
            var report = await Task.Run(() => NetworkDiagnostics.RunAsync(host, label,
                new Progress<string>(s => Dispatcher.Invoke(() => NetStatus.Text = s))));
            RouterLabel.Text = report.Connection is null ? "🏠 Router" : $"🏠 Router ({report.Connection.TypeText})";
            RouterStat.Text = report.Router?.ToString() ?? "—";
            InternetStat.Text = report.Internet?.ToString() ?? "—";
            GameStat.Text = report.Game?.ToString() ?? "—";
            foreach (var f in report.Findings) _netFindings.Add(new FindingVm(f));
            if (report.Wifi is { } w)
                NetStatus.Text = $"Wi-Fi: sinal {w.SignalPercent}% · canal {w.Channel} ({w.Band})";
        }
        catch (Exception ex)
        {
            NetStatus.Text = Ui.Error(ex);
        }
        finally
        {
            NetTestButton.IsEnabled = true;
        }
    }

    async void Bloat_Click(object sender, RoutedEventArgs e)
    {
        BloatButton.IsEnabled = false;
        try
        {
            var r = await Task.Run(() => BufferbloatTest.RunAsync(new Progress<string>(s => Dispatcher.Invoke(() => BloatResult.Text = s))));
            var verdict = r.Grade switch
            {
                "A+" or "A" => Loc.T("Excelente: os downloads quase não afetam o ping."),
                "B" => Loc.T("Aceitável, mas pode haver picos de lag quando alguém faz downloads."),
                _ => Loc.T("Mau: quando a rede está ocupada o ping dispara. Ativa o SQM/QoS no router ou limita os downloads enquanto jogas."),
            };
            BloatResult.Text = Loc.T($"Nota {r.Grade}  ·  parado {r.IdleMs:N0} ms → com download {r.LoadedMs:N0} ms (+{r.AddedMs:N0} ms)  ·  download ≈{r.DownloadMbps:N0} Mbps\n{verdict}");
        }
        catch (Exception ex)
        {
            BloatResult.Text = Ui.Error(ex);
        }
        finally
        {
            BloatButton.IsEnabled = true;
        }
    }

    async void DnsTest_Click(object sender, RoutedEventArgs e)
    {
        DnsTestButton.IsEnabled = false;
        DnsApplyButton.IsEnabled = false;
        DnsStatus.Text = "A testar…";
        try
        {
            _lastDns = await Task.Run(() => DnsBenchmark.RunAsync());
            DnsList.ItemsSource = _lastDns;
            var best = _lastDns.FirstOrDefault(r => r.MedianMs != null);
            var current = _lastDns.Where(r => r.IsCurrent && r.MedianMs != null).MinBy(r => r.MedianMs);
            if (best is null)
            {
                DnsStatus.Text = Loc.T("Nenhum servidor respondeu.");
            }
            else if (best.IsCurrent || (current != null && current.MedianMs - best.MedianMs < 5))
            {
                DnsStatus.Text = Loc.T("O teu DNS atual já é dos mais rápidos. Não precisas de mudar.");
            }
            else
            {
                DnsStatus.Text = Loc.T($"O mais rápido é {best.Label} ({best.TimeText}).");
                DnsApplyButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            DnsStatus.Text = Ui.Error(ex);
        }
        finally
        {
            DnsTestButton.IsEnabled = true;
        }
    }

    async void DnsApply_Click(object sender, RoutedEventArgs e)
    {
        var best = _lastDns?.FirstOrDefault(r => r.MedianMs != null && !r.IsCurrent);
        if (best is null) return;
        if (!EnsureAdmin()) return;
        if (!Confirm(Loc.T($"Mudar o DNS desta ligação para {best.Label} ({best.Server}{(best.Secondary != null ? ", " + best.Secondary : "")})?\n\nPodes repor o original a qualquer momento."))) return;
        try
        {
            await DnsChanger.ApplyAsync(best.Server, best.Secondary);
            DnsStatus.Text = Loc.T($"✓ DNS mudado para {best.Label}.");
            DnsApplyButton.IsEnabled = false;
        }
        catch (Exception ex)
        {
            DnsStatus.Text = Ui.Error(ex);
        }
    }

    async void DnsRevert_Click(object sender, RoutedEventArgs e)
    {
        if (!DnsChanger.HasBackup)
        {
            DnsStatus.Text = Loc.T("O DNS não foi alterado por esta app.");
            return;
        }
        if (!EnsureAdmin()) return;
        try
        {
            await DnsChanger.RevertAsync();
            DnsStatus.Text = Loc.T("↩ DNS original reposto.");
        }
        catch (Exception ex)
        {
            DnsStatus.Text = Ui.Error(ex);
        }
    }

    async Task RefreshQosAsync()
    {
        var list = await Task.Run(GameQos.ListAsync);
        _qos.Clear();
        foreach (var exe in list) _qos.Add(exe);
    }

    async void QosAdd_Click(object sender, RoutedEventArgs e)
    {
        var exe = QosExeBox.Text.Trim();
        if (!GameQos.IsValidExe(exe))
        {
            QosStatus.Text = Loc.T("Escreve só o nome do executável, por exemplo: cs2.exe");
            return;
        }
        if (!EnsureAdmin()) return;
        try
        {
            await GameQos.AddAsync(exe);
            QosStatus.Text = Loc.T($"✓ {exe} tem agora prioridade de rede.");
            QosExeBox.Text = "";
            await RefreshQosAsync();
        }
        catch (Exception ex)
        {
            QosStatus.Text = Ui.Error(ex);
        }
    }

    async void QosRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: string exe }) return;
        if (!EnsureAdmin()) return;
        try
        {
            await GameQos.RemoveAsync(exe);
            QosStatus.Text = Loc.T($"Prioridade de {exe} removida.");
            await RefreshQosAsync();
        }
        catch (Exception ex)
        {
            QosStatus.Text = Ui.Error(ex);
        }
    }

    void RefreshSyncStatus()
    {
        var running = SyncApps.Running();
        SyncButton.IsEnabled = running.Count > 0;
        SyncStatus.Text = running.Count == 0
            ? Loc.T("Nenhuma app de sincronização na nuvem está a correr.")
            : Loc.T($"A correr: {string.Join(", ", running)}. Podem gastar a tua rede a enviar ficheiros enquanto jogas.");
    }

    void SyncClose_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm(Loc.T("Fechar as apps de sincronização (OneDrive, Dropbox, Google Drive…)?\n\nVoltam a abrir no próximo arranque, ou podes abri-las manualmente."))) return;
        var n = SyncApps.CloseAll();
        RefreshSyncStatus();
        SyncStatus.Text = Loc.T($"Fechados {n} processo(s). ") + SyncStatus.Text;
    }
}
