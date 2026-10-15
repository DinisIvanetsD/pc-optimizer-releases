using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PCOptimizer.Core;
using PCOptimizer.Core.Benchmark;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

public sealed record HealthReason(string Text, string Points, SolidColorBrush Brush);

/// <summary>Um problema na lista "O que fazer agora" (usa o mesmo FindingVm do Diagnóstico).</summary>
public sealed class TodoVm
{
    public TodoVm(FindingVm f) => Finding = f;

    public FindingVm Finding { get; }
    public string Title => Finding.Title;
    // a primeira linha que diz alguma coisa (salta títulos como "Códigos de erro:")
    public string Hint => Finding.Description.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(l => l.TrimStart('•', ' '))
        .FirstOrDefault(l => l.Length > 0 && !l.EndsWith(':')) ?? "";
    public SolidColorBrush Brush => Finding.Severity == Severity.Critical ? Palette.Critical : Palette.Warning;
    public List<ActionVm> TopActions => Finding.Actions.Take(1).ToList();
    public string GoLabel => Loc.T("Ver mais");
}

public sealed record CompareRowVm(string Name, string Before, string Now, string Change, SolidColorBrush Brush);

public partial class MainWindow
{
    int? _healthScore;
    DateTime? _lastScan;

    /// <summary>Chamado no fim de cada diagnóstico.</summary>
    void RefreshHome()
    {
        UpdateResetButton();
        var problems = _findings.Where(f => f.Severity is Severity.Critical or Severity.Warning).ToList();

        HomeScore.Text = _healthScore?.ToString() ?? "—";
        var brush = _healthScore is not int s ? Palette.Muted : s >= 80 ? Palette.Ok : s >= 50 ? Palette.Warning : Palette.Critical;
        HomeScore.Foreground = brush;
        HomeVerdict.Foreground = brush;
        HomeVerdict.Text = _healthScore switch
        {
            null => "A analisar…",
            >= 90 => "Excelente",
            >= 75 => "Bom",
            >= 50 => Loc.T("Precisa de atenção"),
            _ => Loc.T("Tem problemas sérios"),
        };

        HomeReasons.ItemsSource = problems.Count == 0
            ? new[] { new HealthReason(Loc.T("Nenhum problema encontrado 🎉"), "", Palette.Ok) }
            : problems.Take(5).Select(f => new HealthReason(f.Title,
                f.Severity == Severity.Critical ? "−20" : "−6",
                f.Severity == Severity.Critical ? Palette.Critical : Palette.Warning)).ToList();
        HomeScanText.Text = _lastScan is DateTime t ? Loc.T($"Analisado às {t:HH:mm}") : "A analisar…";

        HomeTodo.ItemsSource = problems.Take(4).Select(f => new TodoVm(f)).ToList();
        HomeTodoEmpty.Visibility = problems.Count == 0 && _lastScan != null ? Visibility.Visible : Visibility.Collapsed;

        DrawHealthChart();
        RefreshCompare();
    }

    void DrawHealthChart()
    {
        HomeChart.Children.Clear();
        var points = PcMetrics.HealthHistory.TakeLast(30).ToList();
        HomeChartPanel.Visibility = points.Count < 2 ? Visibility.Collapsed : Visibility.Visible;
        HomeChartEmpty.Visibility = Visibility.Collapsed;
        HomeChartText.Text = points.Count >= 2
            ? Loc.T($"{points[0].When:dd/MM} → hoje · de {points[0].Score} para {points[^1].Score}")
            : "";
        if (points.Count < 2) return;

        // o Canvas só tem tamanho depois de desenhado
        var w = HomeChart.ActualWidth > 0 ? HomeChart.ActualWidth : 280;
        var h = HomeChart.ActualHeight > 0 ? HomeChart.ActualHeight : 80;
        double X(int i) => i * w / (points.Count - 1);
        double Y(int score) => h - 4 - (h - 8) * score / 100.0;

        // linhas de referência 50 e 80
        foreach (var level in new[] { 50, 80 })
            HomeChart.Children.Add(new Line
            {
                X1 = 0, X2 = w, Y1 = Y(level), Y2 = Y(level), Stroke = (Brush)FindResource("LineBrush"),
                StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 },
            });

        var last = points[^1].Score;
        var color = last >= 80 ? Palette.Ok : last >= 50 ? Palette.Warning : Palette.Critical;
        var line = new Polyline { Stroke = color, StrokeThickness = 2.2, StrokeLineJoin = PenLineJoin.Round };
        for (var i = 0; i < points.Count; i++) line.Points.Add(new Point(X(i), Y(points[i].Score)));
        HomeChart.Children.Add(line);
        var dot = new Ellipse { Width = 8, Height = 8, Fill = color };
        Canvas.SetLeft(dot, X(points.Count - 1) - 4);
        Canvas.SetTop(dot, Y(last) - 4);
        HomeChart.Children.Add(dot);
    }

    void RefreshCompare()
    {
        var all = PcMetrics.All;
        var before = PcMetrics.Baseline(all);
        var now = all.LastOrDefault();
        var rows = new List<CompareRowVm>();
        if (before is null || now is null || ReferenceEquals(before, now) || before.When == now.When)
            CompareText.Text = before is null
                ? Loc.T("Mede o PC para comparar depois de otimizar.")
                : Loc.T($"Medido a {before.When:dd/MM HH:mm}. Otimiza e mede outra vez.");
        else
        {
            CompareBeforeHeader.Text = Loc.T($"ANTES ({before.When:dd/MM})");
            CompareNowHeader.Text = Loc.T($"AGORA ({now.When:dd/MM HH:mm})");
            CompareText.Text = Loc.T($"\"{before.Label}\" comparado com a última medição.");
            rows.AddRange(PcMetrics.Compare(before, now).Select(d => new CompareRowVm(d.Name, d.Before, d.Now,
                d.Change.Length == 0 ? "—" : d.Change, d.Better switch { true => Palette.Ok, false => Palette.Critical, _ => Palette.Muted })));
        }

        // o último par de testes de FPS, com a diferença no formato do PcMetrics.Compare ("+16 (11%)")
        if (FpsHistory.LatestPair() is { } p)
        {
            var c = FpsVerdict.Compare(p.Before, p.After);
            var diff = p.After.AvgFps - p.Before.AvgFps;
            rows.Add(new CompareRowVm(Loc.T($"FPS ({p.After.Game})"), $"{p.Before.AvgFps:N0}", $"{p.After.AvgFps:N0}",
                (diff > -0.5 ? "+" : "−") + $"{Math.Abs(diff):N0} ({Math.Abs(c.AvgChangePct):N0}%)", // −0,4 arredonda a "+0", não a "−0"
                c.Outcome switch { FpsOutcome.Better => Palette.Ok, FpsOutcome.Worse => Palette.Critical, _ => Palette.Muted }));
        }
        CompareHeader.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompareRows.ItemsSource = rows.Count > 0 ? rows : null;
    }

    async void Measure_Click(object sender, RoutedEventArgs e)
    {
        MeasureButton.IsEnabled = false;
        MeasureButton.Content = "A medir…";
        try
        {
            var label = PcMetrics.All.Count == 0 ? Loc.T("Antes de otimizar (medição manual)") : Loc.T("Medição manual");
            PcMetrics.Save(await PcMetrics.MeasureAsync(label, _healthScore));
            RefreshCompare();
        }
        catch (Exception ex)
        {
            CompareText.Text = Ui.Error(ex, Loc.T("A medição falhou: "));
        }
        finally
        {
            MeasureButton.IsEnabled = true;
            MeasureButton.Content = Loc.T("📏  Medir agora");
        }
    }

    async void HomeScan_Click(object sender, RoutedEventArgs e) => await RunScanAsync();

    async void HomeResetStability_Click(object sender, RoutedEventArgs e)
    {
        var s = PCOptimizer.Core.Settings.AppSettings.Current;
        if (s.StabilitySince is null)
        {
            if (!Confirm(Loc.T("Começar a contar a partir de agora?\n\nOs ecrãs azuis, desligares de repente e crashes de antes deixam de contar para a saúde do PC. ") +
                         Loc.T("Faz isto depois de resolveres a causa (ex.: RAM ou driver). Os registos do Windows não são apagados e podes voltar atrás.")))
                return;
            s.StabilitySince = DateTime.Now;
        }
        else s.StabilitySince = null; // voltar aos últimos 30 dias
        s.Save();
        UpdateResetButton();
        await RunScanAsync();
    }

    void UpdateResetButton()
    {
        var since = PCOptimizer.Core.Settings.AppSettings.Current.StabilitySince;
        HomeResetButton.Content = since is DateTime d ? Loc.T($"A contar desde {d:dd/MM HH:mm} · voltar aos 30 dias") : Loc.T("Contar só a partir de hoje");
    }

    void HomeGoDiag_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = DiagTab;

    void HomeTodoGo_Click(object sender, RoutedEventArgs e)
    {
        Tabs.SelectedItem = DiagTab;
        if (sender is Button { DataContext: TodoVm vm })
            Dispatcher.BeginInvoke(() => (FindingsList.ItemContainerGenerator.ContainerFromItem(vm.Finding) as FrameworkElement)?.BringIntoView(),
                System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>"O que se passa?": abre o sítio certo para o problema e já começa a verificação.</summary>
    async void Problem_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as Button)?.Tag as string)
        {
            case "lag":
                Tabs.SelectedItem = NetworkTab;
                Route_Click(sender, e); // "Onde está o lag?": casa, operador, internet ou servidor
                break;
            case "fps":
                Tabs.SelectedItem = FpsTab;
                break;
            case "slow":
                Tabs.SelectedItem = MonitorTab; // as apps que mais gastam RAM e CPU agora
                break;
            case "crash":
                Tabs.SelectedItem = DiagTab;
                await RunScanAsync();
                break;
            case "space":
                Tabs.SelectedItem = StorageTab;
                SegCleanup.IsChecked = true;
                break;
        }
    }

    void HomeShortcut_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as Button)?.Tag as string)
        {
            case "gaming":
                Tabs.SelectedItem = OptimizeTab;
                ProfileList.SelectedItem = _profiles.FirstOrDefault(p => p.Profile.Id == "gaming") ?? _profiles.FirstOrDefault();
                break;
            case "cleanup":
                Tabs.SelectedItem = StorageTab;
                SegCleanup.IsChecked = true;
                break;
            case "drivers":
                Tabs.SelectedItem = DriversTab;
                break;
            case "ram":
                Tabs.SelectedItem = RamTab;
                break;
        }
    }
}
