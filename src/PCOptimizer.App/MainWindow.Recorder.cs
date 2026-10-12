using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using PCOptimizer.Core.History;
using PCOptimizer.Core.Monitoring;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

/// <summary>Diagnóstico › Gravar um problema.</summary>
public partial class MainWindow
{
    const int RecHotkeyId = 0x5050; // Ctrl+Shift+P, só registado enquanto grava
    ProblemRecorder? _recorder;
    bool _recMarking, _recHooked;
    List<Recording> _recordings = new();

    void Rec_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder?.Running == true)
        {
            StopRecording();
            RecStatus.Text = Loc.T("Gravação parada.");
            return;
        }
        _recorder ??= new ProblemRecorder();
        if (!_recHooked)
        {
            _recHooked = true;
            _recorder.Sampled += s => Dispatcher.BeginInvoke(() =>
            {
                if (!_recMarking)
                    RecStatus.Text = Loc.T($"A gravar · CPU {s.Cpu:N0}% · disco {s.Disk:N0}% · ping {(s.PingMs is int p ? $"{p} ms" : "—")}") +
                                     Loc.T(" · quando engasgar, carrega em Ctrl+Shift+P");
            });
            Closed += (_, _) => _recorder?.Dispose();
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == WmHotkey && wParam.ToInt32() == RecHotkeyId) { RecMark_Click(this, new RoutedEventArgs()); handled = true; }
                return IntPtr.Zero;
            });
        }
        _recorder.Start(null, null);
        RegisterHotKey(new WindowInteropHelper(this).Handle, RecHotkeyId, 0x0002 | 0x0004 | 0x4000, 0x50 /* P */);
        RecButton.Content = Loc.T("■  Parar");
        RecMarkButton.IsEnabled = true;
        RecStatus.Text = Loc.T("A começar…");
    }

    void StopRecording()
    {
        _recorder?.Stop();
        UnregisterHotKey(new WindowInteropHelper(this).Handle, RecHotkeyId);
        RecButton.Content = Loc.T("●  Começar a gravar");
        RecMarkButton.IsEnabled = false;
        _recMarking = false;
    }

    async void RecMark_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder?.Running != true || _recMarking) return;
        _recMarking = true;
        RecMarkButton.IsEnabled = false;
        RecStatus.Text = Loc.T($"Marcado às {DateTime.Now:HH:mm:ss}. A gravar os 15 segundos seguintes…");
        try
        {
            var rec = await _recorder.MarkAsync();
            if (rec is null) return;
            ChangeLog.Record(ChangeKind.Recording, Loc.T("Problema gravado"), string.Join(" ", RecordingAnalyzer.Analyze(rec).Select(o => o.Text)));
            LoadRecordings();
            RecStatus.Text = Loc.T("✓ Gravado. Continua a gravar: se voltar a acontecer, carrega outra vez em Ctrl+Shift+P.");
        }
        catch (Exception ex) { RecStatus.Text = Ui.Error(ex); }
        finally
        {
            _recMarking = false;
            RecMarkButton.IsEnabled = _recorder?.Running == true;
        }
    }

    /// <summary>Mostra a gravação mais recente e enche a lista das anteriores.</summary>
    void LoadRecordings()
    {
        _recordings = ProblemRecorder.LoadAll();
        RecResult.Visibility = _recordings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecHistory.ItemsSource = _recordings.Select(r => Loc.T($"Gravação de {r.Mark:dd/MM HH:mm:ss}")).ToList();
        if (_recordings.Count > 0) RecHistory.SelectedIndex = 0;
    }

    void RecHistory_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RecHistory.SelectedIndex < 0 || RecHistory.SelectedIndex >= _recordings.Count) return;
        var rec = _recordings[RecHistory.SelectedIndex];
        RecObservations.ItemsSource = RecordingAnalyzer.Analyze(rec).Select(o => "• " + o.Text).ToList();
        Dispatcher.BeginInvoke(() => DrawRecording(rec), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>CPU, disco e GPU em % e o ping (escala até ao maior valor), com a marca do momento.</summary>
    void DrawRecording(Recording rec)
    {
        RecChart.Children.Clear();
        var s = rec.Samples;
        if (s.Count < 2) return;
        var w = RecChart.ActualWidth > 0 ? RecChart.ActualWidth : 600;
        var h = RecChart.ActualHeight > 0 ? RecChart.ActualHeight : 98;
        var t0 = s[0].Time;
        var span = Math.Max(1, (s[^1].Time - t0).TotalSeconds);
        double X(DateTime t) => (t - t0).TotalSeconds / span * w;
        double Y(double pct) => h - 2 - (h - 4) * Math.Clamp(pct, 0, 100) / 100;
        var pingMax = Math.Max(100, s.Max(x => x.PingMs ?? 0));

        void Line(string color, Func<RecSample, double?> value)
        {
            var line = new Polyline { Stroke = (Brush)new BrushConverter().ConvertFromString(color)!, StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round };
            foreach (var x in s)
                if (value(x) is double v) line.Points.Add(new Point(X(x.Time), Y(v)));
            if (line.Points.Count > 1) RecChart.Children.Add(line);
        }

        Line("#8B5CF6", x => x.Cpu);
        Line("#FFB020", x => x.Disk);
        Line("#3DD68C", x => x.GpuLoad);
        Line("#4FC3F7", x => x.PingMs is int p ? 100.0 * p / pingMax : null);

        var mx = X(rec.Mark);
        RecChart.Children.Add(new Line { X1 = mx, X2 = mx, Y1 = 0, Y2 = h, Stroke = Brushes.White, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 3 } });
        var label = new TextBlock { Text = Loc.T("aconteceu"), Foreground = Brushes.White, FontSize = 11 };
        Canvas.SetLeft(label, Math.Min(mx + 4, w - 60));
        Canvas.SetTop(label, 0);
        RecChart.Children.Add(label);
    }

    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
