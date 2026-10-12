using System.Windows;
using PCOptimizer.Core.Util;
using System.Windows.Threading;

namespace PCOptimizer.App;

public enum GameChoice { Optimize, Always, NotNow, Never }

/// <summary>"Jogo detetado: otimizar?" no canto do ecrã, sem tirar o foco ao jogo. Sem resposta em 30 s = "Agora não".</summary>
public partial class GamePrompt : Window
{
    readonly DispatcherTimer _timeout = new() { Interval = TimeSpan.FromSeconds(30) };
    bool _answered;

    public event Action<GameChoice>? Answered;

    public GamePrompt(string gameName)
    {
        InitializeComponent();
        HeaderText.Text = Loc.T($"🎮  {gameName} detetado");
        _timeout.Tick += (_, _) => Answer(GameChoice.NotNow);
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
            _timeout.Start();
        };
        Closed += (_, _) => { _timeout.Stop(); if (!_answered) Answered?.Invoke(GameChoice.NotNow); };
    }

    void Answer(GameChoice c)
    {
        if (_answered) return;
        _answered = true;
        Answered?.Invoke(c);
        Close();
    }

    void Optimize_Click(object sender, RoutedEventArgs e) => Answer(GameChoice.Optimize);
    void Always_Click(object sender, RoutedEventArgs e) => Answer(GameChoice.Always);
    void NotNow_Click(object sender, RoutedEventArgs e) => Answer(GameChoice.NotNow);
    void Never_Click(object sender, RoutedEventArgs e) => Answer(GameChoice.Never);
}
