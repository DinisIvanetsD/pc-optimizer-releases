using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PCOptimizer.Core.Gaming;
using PCOptimizer.Core.Network;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

/// <summary>Uma linha da análise "Onde está o lag".</summary>
public sealed class HopVm
{
    public int Ttl { get; init; }
    public string SegmentText { get; init; } = "";
    public Brush SegmentBrush { get; init; } = Brushes.Gray;
    public string AddressText { get; init; } = "";
    public string AvgText { get; init; } = "";
    public string WorstText { get; init; } = "";
    public string LossText { get; init; } = "";
    public Brush LossBrush { get; init; } = Brushes.Gray;
    public string? HostName { get; init; }
}

public partial class MainWindow
{
    GameMonitor? _games;
    RunningGame? _game;
    GameTrafficWatcher? _traffic;
    LiveLatency? _live;
    DetectedServer? _server;
    GameOverlay? _overlay;
    GamePrompt? _prompt;
    DispatcherTimer? _trafficTimer;
    bool _gameSessionOn, _routeBusy, _nicBusy;
    NicAdapter? _nic;
    /// <summary>Nas capturas de ecrã para desenvolvimento não se abrem janelas por cima do jogo.</summary>
    static readonly bool ScreenshotMode = Environment.GetCommandLineArgs().Contains("--screenshots");
    List<NicChange> _nicPlan = new();

    // ------------------------------------------------------------------ arranque / saída

    async Task StartGamingAsync()
    {
        // uma sessão que ficou por repor (a app ou o PC foram abaixo a meio de um jogo):
        // repõe se o jogo DESSA sessão já não estiver aberto (outros jogos abertos não contam)
        if (GameSession.Active is { } pending && !GameSession.IsRunning(pending.Exe))
            try { await GameSession.RestoreAsync(); } catch (Exception ex) { App.Log(ex); }

        RegisterOverlayHotkey();
        _games = new GameMonitor(() => S);
        _games.Started += g => Dispatcher.BeginInvoke(() => OnGameStarted(g));
        _games.Stopped += g => Dispatcher.BeginInvoke(async () => await OnGameStoppedAsync(g));
        if (S.GameMode) _games.Start();
        UpdateOverlayButton();
    }

    /// <summary>Ao fechar a app: repõe a sessão e fecha a sessão de eventos de rede do Windows.</summary>
    void StopGaming()
    {
        _games?.Dispose();
        _trafficTimer?.Stop();
        _live?.Dispose();
        if (_game is { } g) StopFps(g); // grava o que já se mediu e fecha a sessão ETW dos FPS
        _traffic?.Dispose(); // importante: as sessões ETW não morrem sozinhas com o processo
        if (_gameSessionOn)
            try { Task.Run(GameSession.RestoreAsync).Wait(TimeSpan.FromSeconds(15)); } catch { }
    }

    void ApplyGameMode(bool on)
    {
        if (on) _games?.Start();
        else _games?.Stop();
    }

    // ------------------------------------------------------------------ definições do modo jogo

    void LoadGameSettings()
    {
        var names = GameCatalog.All(S).GroupBy(g => g.Exe.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Name);
        var remembered = S.GameChoices
            .Select(kv => $"{names.GetValueOrDefault(kv.Key.ToLowerInvariant(), kv.Key)}: {(kv.Value == "always" ? Loc.T("otimizar sempre") : Loc.T("nunca perguntar"))}")
            .ToList();
        GameChoicesText.Text = remembered.Count == 0
            ? Loc.T("Ainda não há respostas lembradas: a app pergunta sempre que abres um jogo.")
            : Loc.T("Respostas lembradas: ") + string.Join(" · ", remembered);
        ForgetGameChoicesButton.IsEnabled = remembered.Count > 0;
        CustomGamesList.ItemsSource = S.CustomGames.ToList();
    }

    void ForgetGameChoices_Click(object sender, RoutedEventArgs e)
    {
        S.GameChoices.Clear();
        S.Save();
        LoadGameSettings();
    }

    void AddCustomGame_Click(object sender, RoutedEventArgs e)
    {
        var exe = System.IO.Path.GetFileName(CustomGameBox.Text.Trim().Trim('"'));
        if (exe.Length > 0 && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        if (!GameCatalog.IsValidExe(exe))
        {
            MessageBox.Show(this, Loc.T("Escreve só o nome do executável, por exemplo: MeuJogo.exe"), "Rigwise");
            return;
        }
        if (!S.CustomGames.Contains(exe, StringComparer.OrdinalIgnoreCase)) S.CustomGames.Add(exe);
        S.Save();
        CustomGameBox.Text = "";
        LoadGameSettings();
    }

    void RemoveCustomGame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string exe }) return;
        S.CustomGames.RemoveAll(x => x.Equals(exe, StringComparison.OrdinalIgnoreCase));
        S.Save();
        LoadGameSettings();
    }

    // ------------------------------------------------------------------ jogo abre / fecha

    void OnGameStarted(RunningGame g)
    {
        _game = g;
        GameTitle.Text = $"🎮  {g.Game.Name}";
        GameBadgeText.Text = Loc.T("Jogo aberto");
        GameBadgeText.Foreground = (Brush)FindResource("OkBrush");
        GameServerText.Text = "A descobrir o servidor…";
        GameStatsGrid.Visibility = Visibility.Visible;
        GameRouteButton.Visibility = Visibility.Collapsed;
        GameAdminNote.Visibility = Admin.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        GameActionsText.Text = "";
        GameQualityText.Text = "";
        foreach (var t in new[] { GamePing, GameLoss, GameJitter }) t.Text = "--";

        _traffic = new GameTrafficWatcher(g.Pid);
        _traffic.Start();
        _trafficTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _trafficTimer.Tick += (_, _) => UpdateGameServer();
        _trafficTimer.Start();
        StartFps(g);
        RefreshFpsCard(); // mostra o botão "Testar FPS" (com acesso Ready)
        RemindFpsTest(g);
        GameTraffic.Text = Admin.IsElevated ? "--" : Loc.T("só com admin");
        if (S.OverlayEnabled && !ScreenshotMode) ShowOverlay("A procurar o servidor…");

        // a app reabriu a meio do jogo: retoma a sessão que já estava ativa (para a repor no fim)
        if (GameSession.Active is { } active && active.Exe.Equals(g.Game.Exe, StringComparison.OrdinalIgnoreCase))
        {
            _gameSessionOn = true;
            GameRestoreButton.Visibility = Visibility.Visible;
            GameActionsText.Text = Loc.T("⚡ Otimizado enquanto jogas (sessão retomada).");
            return;
        }

        var key = g.Game.Exe.ToLowerInvariant();
        var choice = S.GameChoices.TryGetValue(key, out var c) ? c : null;
        // jogo detetado pelo ecrã inteiro: medido em silêncio, sem pergunta nem sessão automática (pode ser um falso positivo)
        if (g.Detected) GameOptimizeButton.Visibility = Visibility.Visible;
        else if (choice == "always") _ = StartGameSessionAsync();
        else if (choice != "never" && !ScreenshotMode)
        {
            GameOptimizeButton.Visibility = Visibility.Visible;
            _prompt = new GamePrompt(g.Game.Name);
            _prompt.Answered += answer => _ = OnGamePromptAsync(g, answer);
            _prompt.Show();
        }
        else GameOptimizeButton.Visibility = Visibility.Visible;
    }

    async Task OnGamePromptAsync(RunningGame g, GameChoice answer)
    {
        _prompt = null;
        var key = g.Game.Exe.ToLowerInvariant();
        if (answer == GameChoice.Always) S.GameChoices[key] = "always";
        if (answer == GameChoice.Never) S.GameChoices[key] = "never";
        if (answer is GameChoice.Always or GameChoice.Never) S.Save();
        if (answer is GameChoice.Optimize or GameChoice.Always && _game?.Pid == g.Pid) await StartGameSessionAsync();
    }

    async Task OnGameStoppedAsync(RunningGame g)
    {
        _prompt?.Close();
        _trafficTimer?.Stop();
        _live?.Dispose();
        _live = null;
        _traffic?.Dispose();
        _traffic = null;
        _server = null;
        _game = null;
        _overlay?.Hide();
        // depois de esconder o overlay (fechar a sessão ETW pode demorar) e com _game já null: o RefreshFpsCard do
        // StopFps esconde o botão "Testar FPS"
        var fps = StopFps(g);

        GameTitle.Text = Loc.T("Nenhum jogo aberto.");
        GameBadgeText.Text = Loc.T("À espera de um jogo");
        GameBadgeText.Foreground = (Brush)FindResource("MutedBrush");
        GameServerText.Text = Loc.T($"Última sessão: {g.Game.Name}.");
        GameStatsGrid.Visibility = Visibility.Collapsed;
        GameOptimizeButton.Visibility = GameRestoreButton.Visibility = GameRouteButton.Visibility = Visibility.Collapsed;
        GameQualityText.Text = "";

        if (_gameSessionOn)
        {
            _gameSessionOn = false;
            try
            {
                await GameSession.RestoreAsync();
                GameActionsText.Text = Loc.T("✓ O PC voltou ao normal (energia, prioridade de rede e downloads).");
            }
            catch (Exception ex) { GameActionsText.Text = Ui.Error(ex, Loc.T("Não deu para repor tudo: ")); }
        }
        else GameActionsText.Text = "";
        if (fps != null) GameActionsText.Text = (GameActionsText.Text + "\n" + fps).TrimStart();
    }

    async Task StartGameSessionAsync()
    {
        if (_game is not RunningGame g || _gameSessionOn) return;
        GameOptimizeButton.IsEnabled = false;
        try
        {
            var done = await GameSession.StartAsync(g, S.PauseUpdatesWhilePlaying);
            _gameSessionOn = true;
            GameOptimizeButton.Visibility = Visibility.Collapsed;
            GameRestoreButton.Visibility = Visibility.Visible;
            GameActionsText.Text = done.Count == 0
                ? Loc.T("Já estava tudo otimizado.")
                : Loc.T("⚡ Otimizado enquanto jogas: ") + string.Join(" · ", done) + "." +
                  (Admin.IsElevated ? "" : Loc.T(" (Prioridade de rede e pausa do Windows Update precisam de administrador.)"));
        }
        catch (Exception ex) { GameActionsText.Text = Ui.Error(ex); }
        finally { GameOptimizeButton.IsEnabled = true; }
    }

    async void GameOptimize_Click(object sender, RoutedEventArgs e)
    {
        _prompt?.Close();
        await StartGameSessionAsync();
    }

    async void GameRestore_Click(object sender, RoutedEventArgs e)
    {
        GameRestoreButton.IsEnabled = false;
        try
        {
            await GameSession.RestoreAsync();
            _gameSessionOn = false;
            GameActionsText.Text = Loc.T("✓ Reposto. O PC está como antes.");
            GameRestoreButton.Visibility = Visibility.Collapsed;
            if (_game != null) GameOptimizeButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex) { GameActionsText.Text = Ui.Error(ex); }
        finally { GameRestoreButton.IsEnabled = true; }
    }

    // ------------------------------------------------------------------ servidor e ligação ao vivo

    void UpdateGameServer()
    {
        if (_traffic is null) return;
        var (traffic, seconds, up, down) = _traffic.Take();
        if (_traffic.SeesUdp) GameTraffic.Text = $"↑ {up:N1}  ↓ {down:N1} KB/s";
        var server = ServerPicker.Pick(traffic, seconds);
        if (server is null)
        {
            if (_server is null) GameServerText.Text = Loc.T("Ainda sem servidor: estás no menu? Entra numa partida.");
            return;
        }
        // muda de servidor só se for outro endereço (ou se passou de lobby TCP para o jogo em UDP)
        if (_server != null && _server.Address.Equals(server.Address)) return;
        if (_server is { Udp: true } && !server.Udp) return;

        _server = server;
        GameServerText.Text = server.Udp
            ? Loc.T($"Servidor do jogo: {server.Address}:{server.Port}  ·  {server.PacketsPerSecond:N0} pacotes/s")
            : Loc.T($"Ligado a {server.Address}:{server.Port} (lobby/serviços){(Admin.IsElevated ? Loc.T(" — entra numa partida para ver o servidor do jogo") : "")}");
        RouteHostBox.Text = server.Address.ToString();
        GameRouteButton.Visibility = Visibility.Visible;

        _live?.Dispose();
        _live = new LiveLatency(server.Address);
        _live.Updated += s => Dispatcher.BeginInvoke(() => ShowLive(s));
        _live.Start();
    }

    void ShowLive(LiveSample s)
    {
        if (_game is null) return;
        GamePing.Text = s.PingMs is double p ? $"{p:N0} ms" : "--";
        GameLoss.Text = $"{s.LossPercent:N0}%";
        GameJitter.Text = $"{s.JitterMs:N1} ms";
        var brush = (Brush)FindResource(s.Quality switch { LinkQuality.Good => "OkBrush", LinkQuality.Fair => "WarnBrush", _ => "CritBrush" });
        GamePing.Foreground = GameLoss.Foreground = GameJitter.Foreground = brush;
        GameQualityText.Foreground = brush;
        GameQualityText.Text = s.Quality switch
        {
            LinkQuality.Good => Loc.T("✓ Ligação boa para jogar."),
            LinkQuality.Fair => Loc.T("Ligação aceitável, com ") + Worst(s) + Loc.T(". Se sentires lag, vê \"Onde está o lag?\"."),
            _ => Loc.T("⚠ Ligação má: ") + Worst(s) + Loc.T(". Carrega em \"Onde está o lag?\" para saber de onde vem."),
        } + (s.NearServer ? Loc.T("  (O servidor não responde a pings: medido no último salto antes dele.)") : "");
        if (_overlay is { IsVisible: true }) _overlay.ShowSample(s);
    }

    static string Worst(LiveSample s) =>
        s.LossPercent >= 1 ? Loc.T($"{s.LossPercent:N0}% de pacotes perdidos")
        : s.JitterMs >= 8 ? Loc.T($"ping instável (jitter {s.JitterMs:N0} ms)")
        : Loc.T($"ping alto ({s.AvgMs:N0} ms)");

    // ------------------------------------------------------------------ overlay

    void ShowOverlay(string waiting)
    {
        if (_overlay is null)
        {
            _overlay = new GameOverlay(S.OverlayLeft, S.OverlayTop);
            _overlay.Moved += (l, t) =>
            {
                S.OverlayLeft = l;
                S.OverlayTop = t;
                S.Save();
                if (_game is null) _overlay?.Hide();
            };
        }
        _overlay.ShowWaiting(_game is { Detected: true } ? "" : waiting); // jogo detetado: sem servidor, só os FPS
        _overlay.Show();
    }

    void OverlayToggle_Click(object sender, RoutedEventArgs e) => ToggleOverlay();

    void ToggleOverlay()
    {
        S.OverlayEnabled = !S.OverlayEnabled;
        S.Save();
        if (!S.OverlayEnabled) _overlay?.Hide();
        else if (_game != null) ShowOverlay("A medir…");
        UpdateOverlayButton();
    }

    void UpdateOverlayButton() =>
        OverlayToggleButton.Content = S.OverlayEnabled ? Loc.T("Overlay: ligado (Ctrl+Shift+O)") : Loc.T("Overlay: desligado (Ctrl+Shift+O)");

    void OverlayMove_Click(object sender, RoutedEventArgs e)
    {
        ShowOverlay(_game is null ? Loc.T("Overlay de exemplo") : "A medir…");
        _overlay!.StartMoving();
    }

    // Ctrl+Shift+O em qualquer lado (também dentro do jogo); Ctrl+Shift+F (teste de FPS) só se regista com um jogo a medir (StartFps)
    const int HotkeyId = 0x4F50, FpsHotkeyId = 0x4650, WmHotkey = 0x0312;

    void RegisterOverlayHotkey()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == WmHotkey && wParam.ToInt32() == HotkeyId) { ToggleOverlay(); handled = true; }
            if (msg == WmHotkey && wParam.ToInt32() == FpsHotkeyId) { StartFpsTest(); handled = true; }
            return IntPtr.Zero;
        });
        RegisterHotKey(hwnd, HotkeyId, 0x0002 | 0x0004 | 0x4000 /* Ctrl | Shift | sem repetição */, 0x4F /* O */);
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    // ------------------------------------------------------------------ onde está o lag

    void GameRoute_Click(object sender, RoutedEventArgs e)
    {
        if (_server != null) RouteHostBox.Text = _server.Address.ToString();
        Route_Click(sender, e);
    }

    async void Route_Click(object sender, RoutedEventArgs e)
    {
        if (_routeBusy) return;
        var host = RouteHostBox.Text.Trim();
        if (host.Length == 0) host = "1.1.1.1";
        _routeBusy = true;
        RouteButton.IsEnabled = false;
        RouteVerdictBox.Visibility = Visibility.Collapsed;
        RouteList.ItemsSource = null;
        try
        {
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip!))
                ip = (await Dns.GetHostAddressesAsync(host)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                     ?? throw new InvalidOperationException(Loc.T("Não encontrei esse servidor."));
            var report = await Task.Run(() => RouteAnalyzer.RunAsync(ip, 25, new Progress<string>(t => Dispatcher.BeginInvoke(() => RouteStatus.Text = t))));
            ShowRoute(report);
            RouteStatus.Text = report.ReachedTarget ? "" : Loc.T("O servidor não responde a pings (é normal): mostra o caminho até onde dá.");
        }
        catch (Exception ex) { RouteStatus.Text = Ui.Error(ex); }
        finally
        {
            _routeBusy = false;
            RouteButton.IsEnabled = true;
        }
    }

    void ShowRoute(RouteReport r)
    {
        var resp = r.Hops.ToList();
        RouteList.ItemsSource = resp.Select((h, i) =>
        {
            var real = RouteAnalyzer.IsRealLoss(resp, i);
            return new HopVm
            {
                Ttl = h.Ttl,
                SegmentText = h.Segment switch { RouteSegment.Home => "Casa", RouteSegment.Provider => "Operador", RouteSegment.Internet => "Internet", _ => "Servidor" },
                SegmentBrush = (Brush)FindResource(h.Segment switch { RouteSegment.Home => "AccentBrush", RouteSegment.Provider => "WarnBrush", RouteSegment.Internet => "MutedBrush", _ => "OkBrush" }),
                AddressText = h.Address?.ToString() ?? "* * *",
                AvgText = h.Responds ? $"{h.Avg:N0} ms" : "—",
                WorstText = h.Responds ? $"pior {h.Worst:N0}" : "",
                LossText = !h.Responds ? Loc.T("não responde (normal)") : real ? $"perda {h.LossPercent:N0}%" : h.LossPercent >= 4 ? Loc.T("ignora alguns testes (normal)") : Loc.T("sem perda"),
                LossBrush = (Brush)FindResource(real ? "CritBrush" : "MutedBrush"),
                HostName = h.HostName,
            };
        }).ToList();

        var v = r.Verdict;
        RouteVerdictTitle.Text = v.Title;
        RouteVerdictText.Text = v.Detail;
        var good = v.Source == LagSource.None;
        RouteVerdictBox.Background = new SolidColorBrush(good ? Color.FromRgb(0x14, 0x2A, 0x1C) : Color.FromRgb(0x3A, 0x2A, 0x14));
        RouteVerdictBox.BorderBrush = new SolidColorBrush(good ? Color.FromRgb(0x23, 0x4A, 0x30) : Color.FromRgb(0x5A, 0x43, 0x20));
        RouteVerdictBox.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ placa de rede

    async Task LoadNicAsync()
    {
        try
        {
            _nic = await NicTuning.ReadAsync();
            if (_nic is null)
            {
                NicTitle.Text = Loc.T("Não encontrei a placa de rede ligada à internet.");
                return;
            }
            _nicPlan = NicTuning.Plan(_nic.Values);
            NicTitle.Text = $"{_nic.Description} ({_nic.Name})";
            NicList.ItemsSource = _nicPlan;
            NicApplyButton.IsEnabled = _nicPlan.Count > 0;
            NicRevertButton.Visibility = NicTuning.HasBackup(_nic.Guid) ? Visibility.Visible : Visibility.Collapsed;
            NicStatus.Text = _nicPlan.Count == 0 ? Loc.T("✓ Já está afinada para jogos.") : Loc.T($"{_nicPlan.Count} opção(ões) a mudar.");
        }
        catch (Exception ex) { NicStatus.Text = Ui.Error(ex); }
    }

    async void NicApply_Click(object sender, RoutedEventArgs e)
    {
        if (_nic is null || _nicPlan.Count == 0 || _nicBusy) return;
        if (_game != null)
        {
            NicStatus.Text = Loc.T("Fecha o jogo primeiro: a internet cai uns segundos ao aplicar.");
            return;
        }
        if (!EnsureAdmin()) return;
        if (!Confirm(Loc.T($"Afinar a placa de rede {_nic.Description}?\n\n") + string.Join("\n", _nicPlan.Select(c => $"• {c.Label}: {c.From} → {c.To}")) +
                     Loc.T("\n\nA placa reinicia no fim: a internet cai uns 5 segundos. Dá para desfazer no Histórico."))) return;
        await RunNicAsync(() => NicTuning.ApplyAsync(_nic, _nicPlan), Loc.T("✓ Placa de rede afinada."));
    }

    async void NicRevert_Click(object sender, RoutedEventArgs e)
    {
        if (_nic is null || _nicBusy) return;
        if (!EnsureAdmin()) return;
        if (!Confirm(Loc.T("Repor a placa de rede como estava?\n\nA internet cai uns 5 segundos."))) return;
        await RunNicAsync(async () => { await NicTuning.RevertAsync(_nic.Guid); }, Loc.T("✓ Placa de rede reposta como estava."));
    }

    async Task RunNicAsync(Func<Task> action, string ok)
    {
        _nicBusy = true;
        NicApplyButton.IsEnabled = false;
        NicStatus.Text = Loc.T("A aplicar… (a internet vai cair uns segundos)");
        try
        {
            await action();
            await LoadNicAsync();
            NicStatus.Text = ok;
        }
        catch (Exception ex)
        {
            NicStatus.Text = Ui.Error(ex);
            NicApplyButton.IsEnabled = _nicPlan.Count > 0;
        }
        finally { _nicBusy = false; }
    }
}
