using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PCOptimizer.Core.Benchmark;
using PCOptimizer.Core.Gaming;
using PCOptimizer.Core.History;
using PCOptimizer.Core.Optimization;
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
            pair = Loc.T($"Antes {b.AvgFps:N0} → Depois {a.AvgFps:N0} ({FpsVerdict.Signed(c.AvgChangePct)}%)") + (c.Outcome == FpsOutcome.Better ? " ✓" : "");
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
        // só com um jogo a medir, para não roubar o Ctrl+Shift+F às outras apps o resto do tempo
        RegisterHotKey(new WindowInteropHelper(this).Handle, FpsHotkeyId, 0x0002 | 0x0004 | 0x4000 /* Ctrl | Shift | sem repetição */, 0x46 /* F */);

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
        // jogo detetado = sem servidor: o overlay só mostra os FPS, sem separador; durante o teste mostra a contagem
        if (_testTimer is null) _overlay?.ShowFps(s is null ? null : Loc.T($"{s.AvgFps:N0} FPS · 1% {s.Low1Fps:N0}") + (detected ? "" : " │ "));
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
            summary = s.Seconds < 60
                ? Loc.T($"Sessão de {s.Seconds:N0} s: {s.AvgFps:N0} FPS médios, 1% low {s.Low1Fps:N0}, {s.Stutters} engasgos.")
                : Loc.T($"Sessão de {s.Seconds / 60:N0} min: {s.AvgFps:N0} FPS médios, 1% low {s.Low1Fps:N0}, {s.Stutters} engasgos.");
        }
        RefreshFpsCard();
        return summary;
    }

    /// <summary>Para o temporizador e a captura, esvazia a janela e a lista. Devolve os frames da sessão.</summary>
    List<double> EndCapture()
    {
        InterruptFpsTest(); // jogo fechou, outro jogo ou a app a fechar: o teste a meio não conta
        UnregisterHotKey(new WindowInteropHelper(this).Handle, FpsHotkeyId);
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
        FpsGameText.Text = _game?.Game.Name ?? Loc.T("Nenhum jogo aberto.");

        // o último veredito sobrevive a reabrir a app (o Desfazer pode pedir para reabrir como administrador);
        // durante um teste fica escondido
        if (_testTimer is null)
        {
            if (FpsHistory.LatestPair() is { } p &&
                FpsVerdict.ShouldShow(FpsVerdict.Compare(p.Before, p.After).Outcome,
                    FpsVerdict.TweaksBetween(ChangeLog.All, p.Before.When, p.After.When).Count, S.FpsVerdictKeptAt, p.After.When))
                ShowVerdict(p.Before, p.After);
            else FpsVerdictBox.Visibility = Visibility.Collapsed;
        }

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

    // ------------------------------------------------------------------ teste guiado (Ctrl+Shift+F), veredito e desfazer

    const int TestCountdown = 3, TestSeconds = 60, TestWaitSeconds = 30;
    DispatcherTimer? _testTimer;
    /// <summary>Os ajustes que o "Desfazer esses ajustes" do último veredito reverte.</summary>
    List<ChangeEntry> _verdictTweaks = new();
    /// <summary>O When do Depois do veredito mostrado (o Manter guarda-o nas definições).</summary>
    DateTime _verdictAfter;

    void TestFps_Click(object sender, RoutedEventArgs e) => StartFpsTest();

    /// <summary>
    /// Contagem de 3 s e 60 s a medir. Sem jogo, sem acesso ou com um teste a decorrer, não faz nada. Pelo botão do
    /// cartão a Rigwise está à frente: a contagem só começa com o jogo em primeiro plano (até 30 s à espera).
    /// </summary>
    void StartFpsTest()
    {
        if (_testTimer != null || _game is not { } g || PerfLogAccess.Current != FpsAccess.Ready) return;
        FpsVerdictBox.Visibility = Visibility.Collapsed;
        var t = -TestCountdown; // negativo: contagem; de 0 a 60: a medir
        var waited = 0;
        var holding = ForegroundPid() != g.Pid;
        ShowTestText(holding ? Loc.T("Volta ao jogo para começar") : TestCountdown.ToString());
        _testTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _testTimer.Tick += (_, _) =>
        {
            if (_game?.Pid != g.Pid) { InterruptFpsTest(); return; } // jogo fechado
            var front = ForegroundPid() == g.Pid;
            if (t < 0)
            {
                if (!front) // a contagem fica parada (e recomeça do 3) até o jogo voltar à frente
                {
                    if (++waited >= TestWaitSeconds) { InterruptFpsTest(); return; }
                    t = -TestCountdown;
                    holding = true;
                    ShowTestText(Loc.T("Volta ao jogo para começar"));
                    return;
                }
                if (holding) { holding = false; ShowTestText(TestCountdown.ToString()); return; }
                if (++t < 0) { ShowTestText((-t).ToString()); return; }
                lock (_fpsGate) _testFrames = new();
            }
            else t++;
            // Alt+Tab: o teste deixou de medir o mesmo sítio
            if (!front) { InterruptFpsTest(); return; }
            if (t < TestSeconds) ShowTestText(Loc.T($"A medir {TestSeconds - t} s · fica no mesmo sítio"));
            else FinishFpsTest(g);
        };
        _testTimer.Start();
    }

    /// <summary>No overlay (no lugar dos FPS ao vivo) e no cartão, para quem carregou no botão com o overlay desligado.</summary>
    void ShowTestText(string text)
    {
        FpsTestStatus.Text = text;
        _overlay?.ShowFps(text + (_game is { Detected: false } ? " │ " : ""));
    }

    /// <summary>Descarta o teste a decorrer, se houver.</summary>
    void InterruptFpsTest()
    {
        if (_testTimer is null) return;
        EndFpsTest();
        FpsTestStatus.Text = Loc.T("Teste interrompido, repete.");
    }

    /// <summary>Para o temporizador e devolve os frames do teste. O UpdateFps volta a pôr os FPS ao vivo no overlay.</summary>
    List<double> EndFpsTest()
    {
        _testTimer?.Stop();
        _testTimer = null;
        _overlay?.ShowFps(null);
        lock (_fpsGate)
        {
            var frames = _testFrames ?? new();
            _testFrames = null;
            return frames;
        }
    }

    void FinishFpsTest(RunningGame g)
    {
        var s = FrameStats.From(EndFpsTest());
        if (s is null || s.Seconds < FpsVerdict.MinSeconds)
        {
            FpsTestStatus.Text = Loc.T("Teste interrompido, repete.");
            return;
        }
        var now = DateTime.Now;
        var before = FpsHistory.PendingBefore(g.Game.Exe);
        var role = FpsVerdict.NextTestRole(before, ChangeLog.All, now);
        // o Depois mediria ajustes que ainda não fizeram efeito: não grava nada
        if (role == FpsRole.After && RebootPending(before!, now))
        {
            FpsTestStatus.Text = RebootFirstText;
            return;
        }
        var run = FpsRun.From(g.Game.Name, g.Game.Exe, role, s, now);
        FpsHistory.Add(run);
        RefreshFpsCard();
        if (role == FpsRole.Before)
            FpsTestStatus.Text = Loc.T("Guardado como Antes. Otimiza e repete o teste no mesmo sítio.");
        else
        {
            FpsTestStatus.Text = "";
            ShowVerdict(before!, run); // After só com um Antes pendente
        }
    }

    void ShowVerdict(FpsRun before, FpsRun after)
    {
        var c = FpsVerdict.Compare(before, after);
        _verdictTweaks = FpsVerdict.TweaksBetween(ChangeLog.All, before.When, after.When);
        _verdictAfter = after.When;
        FpsVerdictTitle.Text = c.Outcome switch { FpsOutcome.Better => "✓", FpsOutcome.Same => "=", FpsOutcome.Worse => "✗", _ => "±" } + "  " + after.Game;
        FpsVerdictText.Text = FpsVerdict.Text(c, _verdictTweaks.Count, before.When);

        var good = c.Outcome == FpsOutcome.Better; // as mesmas cores do veredito "Onde está o lag"
        FpsVerdictBox.Background = new SolidColorBrush(good ? Color.FromRgb(0x14, 0x2A, 0x1C) : Color.FromRgb(0x3A, 0x2A, 0x14));
        FpsVerdictBox.BorderBrush = new SolidColorBrush(good ? Color.FromRgb(0x23, 0x4A, 0x30) : Color.FromRgb(0x5A, 0x43, 0x20));

        var undo = FpsVerdict.OffersUndo(c, _verdictTweaks.Count);
        FpsUndoButton.Visibility = undo ? Visibility.Visible : Visibility.Collapsed;
        FpsKeepButton.Visibility = _verdictTweaks.Count > 0 && !good ? Visibility.Visible : Visibility.Collapsed;
        // misto ou FPS limitados (só Manter): Manter em primeiro; sem diferença ou pior: Desfazer em primeiro. O primeiro é o botão de destaque.
        var keepFirst = c.Outcome == FpsOutcome.Mixed || !undo;
        var panel = (System.Windows.Controls.Panel)FpsKeepButton.Parent;
        panel.Children.Remove(FpsKeepButton);
        panel.Children.Insert(keepFirst ? 0 : 1, FpsKeepButton);
        Style primary = (Style)FindResource("PrimaryButton"), plain = (Style)FindResource(typeof(System.Windows.Controls.Button));
        FpsKeepButton.Style = keepFirst ? primary : plain;
        FpsUndoButton.Style = keepFirst ? plain : primary;
        FpsVerdictBox.Visibility = Visibility.Visible;
    }

    async void FpsUndo_Click(object sender, RoutedEventArgs e)
    {
        FpsUndoButton.IsEnabled = false;
        try
        {
            // TweakCatalog.Get, Simulation.Guard e Admin.Require podem lançar: vai tudo para o catch
            if (_verdictTweaks.Any(t => TweakCatalog.Get(t.Data["id"]).RequiresAdmin) && !EnsureAdmin()) return;
            // pode dar menos que o número de ajustes: os que partilham a cópia (planos de energia) revertem juntos
            var n = await FpsVerdict.UndoTweaksAsync(_verdictTweaks);
            FpsVerdictBox.Visibility = Visibility.Collapsed;
            FpsTestStatus.Text = Loc.T($"✓ {n} ajuste(s) desfeito(s). Reinicia o PC se algum o pedia.");
        }
        catch (Exception ex) { FpsTestStatus.Text = Ui.Error(ex); }
        finally { FpsUndoButton.IsEnabled = true; }
        await RefreshAllTweaksAsync(); // também depois de um erro a meio: alguns já podem ter sido revertidos
    }

    void FpsKeep_Click(object sender, RoutedEventArgs e)
    {
        S.FpsVerdictKeptAt = _verdictAfter; // este veredito já não volta a aparecer
        S.Save();
        FpsVerdictBox.Visibility = Visibility.Collapsed;
    }

    /// <summary>Ao abrir um jogo com um Antes à espera e ajustes feitos depois dele.</summary>
    void RemindFpsTest(RunningGame g)
    {
        var before = FpsHistory.PendingBefore(g.Game.Exe);
        var now = DateTime.Now;
        if (FpsVerdict.NextTestRole(before, ChangeLog.All, now) == FpsRole.After)
            FpsTestStatus.Text = RebootPending(before!, now) ? RebootFirstText : Loc.T("Repete o teste no mesmo sítio para ver se a otimização ajudou.");
    }

    static string RebootFirstText => Loc.T("Reinicia o PC antes do teste Depois: alguns ajustes só funcionam depois de reiniciar.");

    /// <summary>Há ajustes desde o Antes que pedem reinício, feitos depois do último arranque do Windows.</summary>
    static bool RebootPending(FpsRun before, DateTime now) =>
        FpsVerdict.RebootPending(FpsVerdict.TweaksBetween(ChangeLog.All, before.When, now),
            // um id que já não existe no catálogo (histórico antigo) não pede reinício; o Get lançaria dentro do temporizador
            id => TweakCatalog.All.FirstOrDefault(t => t.Id == id)?.RequiresReboot ?? false,
            DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64));

    static int ForegroundPid()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return (int)pid;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
