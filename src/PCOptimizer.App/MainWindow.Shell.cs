using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCOptimizer.Core.Reports;
using PCOptimizer.Core.Settings;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

/// <summary>Comportamento da janela: tamanho guardado, atalhos de teclado, boas-vindas, relatório.</summary>
public partial class MainWindow
{
    // ------------------------------------------------------------------ tamanho e posição

    void RestoreWindowBounds()
    {
        if (S.WindowWidth is not double w || S.WindowHeight is not double h || S.WindowLeft is not double l || S.WindowTop is not double t) return;
        // só se ainda couber nos ecrãs atuais (o monitor pode ter sido desligado)
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var title = new Rect(l + 40, t, Math.Max(100, w - 80), 30);
        if (!screen.IntersectsWith(title)) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = l;
        Top = t;
        Width = Math.Max(MinWidth, w);
        Height = Math.Max(MinHeight, h);
        if (S.WindowMaximized) WindowState = WindowState.Maximized;
    }

    void SaveWindowBounds()
    {
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (b.IsEmpty || double.IsNaN(b.Width)) return;
        S.WindowLeft = b.Left;
        S.WindowTop = b.Top;
        S.WindowWidth = b.Width;
        S.WindowHeight = b.Height;
        S.WindowMaximized = WindowState == WindowState.Maximized;
        S.Save();
    }

    // ------------------------------------------------------------------ atalhos de teclado

    void SetupShortcuts()
    {
        PreviewKeyDown += async (_, e) =>
        {
            var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
            var navs = NavPanel.Children.OfType<RadioButton>().ToList();

            // Ctrl+1…9, Ctrl+0: páginas do menu, pela ordem
            var digit = e.Key is >= Key.D0 and <= Key.D9 ? e.Key - Key.D0 : e.Key is >= Key.NumPad0 and <= Key.NumPad9 ? e.Key - Key.NumPad0 : -1;
            if (ctrl && digit >= 0)
            {
                var index = digit == 0 ? 9 : digit - 1;
                if (index < navs.Count) navs[index].IsChecked = true;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F5)
            {
                e.Handled = true;
                await RefreshCurrentPageAsync();
                return;
            }
            if (ctrl && e.Key == Key.F && Tabs.SelectedItem == StorageTab)
            {
                SegApps.IsChecked = true;
                AppSearchBox.Focus();
                e.Handled = true;
            }
        };

        // a dica do atalho aparece ao passar o rato no menu
        var i = 0;
        foreach (var nav in NavPanel.Children.OfType<RadioButton>())
        {
            i++;
            if (i <= 10) nav.ToolTip = $"Ctrl+{(i == 10 ? 0 : i)}";
        }
    }

    async Task RefreshCurrentPageAsync()
    {
        var tab = Tabs.SelectedItem;
        if (tab == HomeTab || tab == DiagTab) await RunScanAsync();
        else if (tab == DriversTab) await ScanDriversAsync();
        else if (tab == StartupTab) await LoadStartupAsync();
        else if (tab == HistoryTab) LoadHistory();
        else if (tab == SettingsTab) LoadSettingsPage();
        else if (tab == StorageTab) await LoadDrivesAsync();
        else if (tab == AppsTab) await Task.WhenAll(RefreshInstalledAppsAsync(), RefreshBloatAsync());
        else if (tab == NetworkTab) { await RefreshQosAsync(); RefreshSyncStatus(); }
        else if (tab == ComponentsTab) await LoadComponentsAsync();
    }

    // ------------------------------------------------------------------ animação ao mudar de página

    /// <summary>A página nova aparece com um fade e sobe 10 px (160 ms). Só opacidade e posição: a GPU faz isto sem custo.</summary>
    /// <summary>Mostra "Ver detalhes" só quando o texto do cartão não cabe nas 2 linhas (depende da largura da janela).</summary>
    void FindingDesc_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not TextBlock tb || tb.Parent is not Panel panel || panel.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().FirstOrDefault() is not { } more) return;
        if (more.IsChecked == true || tb.ActualWidth <= 0) return;
        var full = new TextBlock { Text = tb.Text, FontSize = tb.FontSize, FontFamily = tb.FontFamily, TextWrapping = TextWrapping.Wrap, LineHeight = tb.LineHeight };
        full.Measure(new Size(tb.ActualWidth, double.PositiveInfinity));
        more.Visibility = full.DesiredSize.Height > tb.MaxHeight + 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    void AnimatePageIn()
    {
        if ((Tabs.SelectedItem as TabItem)?.Content is not FrameworkElement page) return;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var time = TimeSpan.FromMilliseconds(220);
        var move = page.RenderTransform as System.Windows.Media.TranslateTransform ?? new System.Windows.Media.TranslateTransform();
        page.RenderTransform = move;
        move.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(14, 0, time) { EasingFunction = ease });
        page.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, time) { EasingFunction = ease });
    }

    // ------------------------------------------------------------------ primeira vez

    void ShowWelcomeIfFirstRun()
    {
        if (S.FirstRunDone) return;
        var w = new WelcomeWindow { Owner = this };
        if (w.ShowDialog() == true)
        {
            if (w.StartWithWindows)
            {
                try
                {
                    AutoStart.Set(true);
                    S.StartWithWindows = true;
                }
                catch { }
            }
        }
        S.FirstRunDone = true;
        S.Save();
        StartTutorial();
    }

    Tutorial? _tutorial;

    void StartTutorial()
    {
        _tutorial ??= new Tutorial(this, NavPanel, tab =>
        {
            if (FindName(tab) is not TabItem t) return false;
            Tabs.SelectedItem = t;
            return true;
        });
        if (!_tutorial.Running) _tutorial.Start();
    }

    void ShowWelcome_Click(object sender, RoutedEventArgs e) => StartTutorial();

    // ------------------------------------------------------------------ relatório

    async void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ScanStatus.Text = Loc.T("A preparar o relatório (a ler os registos do Windows)…");
            var timeline = _timeline ?? await Task.Run(() => PCOptimizer.Core.History.Timeline.Build());
            var path = ReportBuilder.Save(_findings.Select(f => f.Finding).ToList(), _healthScore, AppVersion,
                timeline, PCOptimizer.Core.Monitoring.ProblemRecorder.LoadAll());
            Shell.Open(path);
            ScanStatus.Text = Loc.T($"Relatório guardado em {path}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("Não foi possível criar o relatório: ") + ex.Message, "Rigwise");
        }
    }
}
