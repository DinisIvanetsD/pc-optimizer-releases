using System.Windows;
using System.Windows.Threading;
using PCOptimizer.Core.Benchmark;
using PCOptimizer.Core.Gaming;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

/// <summary>Uma linha da lista "FPS por jogo".</summary>
public sealed record FpsGameVm(string Name, string LastText, string AvgText, string PairText)
{
    public static FpsGameVm From(FpsGameSummary s)
    {
        var pair = "";
        if (s.Before is { } b && s.After is { } a)
        {
            var c = FpsVerdict.Compare(b, a);
            pair = Loc.T($"Antes {b.AvgFps:N0} → Depois {a.AvgFps:N0} ({c.AvgChangePct:+0;−0}%)") + (c.Outcome == FpsOutcome.Better ? " ✓" : "");
        }
        return new(s.Game,
            s.Last is { } l ? Loc.T($"Última: {l.AvgFps:N0} FPS (1% {l.Low1Fps:N0})") : "",
            s.Avg5 is { } avg5 ? Loc.T($"Média de 5: {avg5:N0}") : "",
            pair);
    }
}

public partial class MainWindow
{
    // shortcut: limite de ~32 MB por sessão (≈4 h a 240 FPS); passar a histograma se for preciso mais
    const int MaxSessionFrames = 4_000_000;

    /// <summary>Protege a captura, a janela e as listas: os frames chegam numa thread de fundo.</summary>
    readonly object _fpsGate = new();
    FrameCapture? _fps;
    FrameWindow _fpsWindow = new(10);
    List<double> _sessionFrames = new();
    /// <summary>Frames do teste guiado; null sem teste a decorrer.</summary>
    List<double>? _testFrames = null;
    DispatcherTimer? _fpsTimer;

    /// <summary>Mede os FPS do jogo ao vivo (quadro do Modo jogo e overlay), se a medição estiver ativa.</summary>
    void StartFps(RunningGame g)
    {
        EndCapture(); // nunca duas capturas: o Dispose de uma antiga fecharia a sessão ETW da nova
        GameFps.Text = GameLow1.Text = "--";
        if (PerfLogAccess.Current != FpsAccess.Ready) return;

        var cap = new FrameCapture(g.Pid);
        cap.Frame += ms =>
        {
            lock (_fpsGate)
            {
                if (_fps != cap) return; // frame atrasado de uma captura já parada
                _fpsWindow.Add(ms);
                if (_sessionFrames.Count < MaxSessionFrames) _sessionFrames.Add(ms);
                _testFrames?.Add(ms);
            }
        };
        // pode chegar dentro do Start() ou numa thread de fundo
        cap.Failed += msg => Dispatcher.BeginInvoke(() =>
        {
            App.Log(new InvalidOperationException(msg));
            if (_fps == cap) HideFps();
        });
        lock (_fpsGate) _fps = cap;

        _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _fpsTimer.Tick += (_, _) => UpdateFps(g.Detected);
        _fpsTimer.Start();
        cap.Start();
    }

    void UpdateFps(bool detected)
    {
        var s = _fpsWindow.Stats();
        GameFps.Text = s is null ? "--" : $"{s.AvgFps:N0}";
        GameLow1.Text = s is null ? "--" : $"{s.Low1Fps:N0}";
        // jogo detetado = sem servidor: o overlay só mostra os FPS, sem separador
        _overlay?.ShowFps(s is null ? null : Loc.T($"{s.AvgFps:N0} FPS · 1% {s.Low1Fps:N0}") + (detected ? "" : " │ "));
    }

    /// <summary>Os FPS desaparecem do quadro e do overlay. Os frames já medidos ficam para o resumo da sessão.</summary>
    void HideFps()
    {
        _fpsTimer?.Stop();
        GameFps.Text = GameLow1.Text = "--";
        _overlay?.ShowFps(null);
    }

    /// <summary>Para a medição e grava a sessão. Devolve o resumo, ou null se não houve frames.</summary>
    string? StopFps(RunningGame g)
    {
        var frames = EndCapture();
        HideFps();
        string? summary = null;
        if (FrameStats.From(frames) is { } s)
        {
            FpsHistory.Add(FpsRun.From(g.Game.Name, g.Game.Exe, FpsRole.Session, s, DateTime.Now));
            summary = Loc.T($"Sessão de {s.Seconds / 60:N0} min: {s.AvgFps:N0} FPS médios, 1% low {s.Low1Fps:N0}, {s.Stutters} engasgos.");
        }
        RefreshFpsCard();
        return summary;
    }

    /// <summary>Para o temporizador e a captura, esvazia a janela e a lista. Devolve os frames da sessão.</summary>
    List<double> EndCapture()
    {
        _fpsTimer?.Stop();
        _fpsTimer = null;
        FrameCapture? cap;
        List<double> frames;
        lock (_fpsGate)
        {
            cap = _fps;
            _fps = null;
            frames = _sessionFrames;
            _sessionFrames = new();
            _fpsWindow = new FrameWindow(10);
        }
        cap?.Dispose(); // fora do lock: fecha a sessão ETW e pode demorar
        return frames;
    }

    /// <summary>Cartão "FPS por jogo": o acesso (ativar / terminar sessão), o botão de teste e a lista por jogo.</summary>
    void RefreshFpsCard()
    {
        var access = PerfLogAccess.Current; // cria uma WindowsIdentity: ler só aqui, nunca num temporizador
        FpsAccessButton.Visibility = access == FpsAccess.NeedsEnable ? Visibility.Visible : Visibility.Collapsed;
        FpsAccessText.Text = access == FpsAccess.NeedsSignOut ? Loc.T("Termina sessão no Windows para ativar os FPS.") : "";
        FpsTestButton.Visibility = _game != null && access == FpsAccess.Ready ? Visibility.Visible : Visibility.Collapsed;

        var games = FpsHistory.Summaries().Select(FpsGameVm.From).ToList();
        FpsGamesList.ItemsSource = games;
        FpsEmptyText.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void EnableFps_Click(object sender, RoutedEventArgs e)
    {
        if (!Confirm(Loc.T("Ativar a medição de FPS?\n\nO Windows vai pedir administrador uma vez para juntar o teu utilizador ao grupo \"Performance Log Users\". Esse grupo pode ler eventos de desempenho do sistema.\n\nDepois tens de terminar sessão no Windows. Dá para desfazer no Histórico."))) return;
        FpsAccessButton.IsEnabled = false;
        try
        {
            await PerfLogAccess.EnableAsync();
            RefreshFpsCard();
        }
        catch (Exception ex) { FpsAccessText.Text = Ui.Error(ex); }
        finally { FpsAccessButton.IsEnabled = true; }
    }

    // A Tarefa 9 (teste guiado, veredito e desfazer) preenche estes três.
    void TestFps_Click(object sender, RoutedEventArgs e) { }

    void FpsUndo_Click(object sender, RoutedEventArgs e) { }

    void FpsKeep_Click(object sender, RoutedEventArgs e) { }
}
