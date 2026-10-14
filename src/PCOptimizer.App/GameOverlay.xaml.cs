using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PCOptimizer.Core.Gaming;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

/// <summary>
/// Indicador de FPS e ping/perda/jitter por cima do jogo. É uma janela normal, transparente e que deixa
/// passar os cliques — não injeta nada no jogo, por isso não incomoda os anti-cheats. Não aparece
/// em ecrã inteiro exclusivo (só em "sem bordas" ou janela).
/// </summary>
public partial class GameOverlay : Window
{
    readonly DispatcherTimer _topmost = new() { Interval = TimeSpan.FromSeconds(2) };
    bool _moving;

    public event Action<double, double>? Moved;

    public GameOverlay(double? left, double? top)
    {
        InitializeComponent();
        Left = left ?? 12;
        Top = top ?? 12;
        // alguns jogos põem-se por cima de tudo: volta a ficar por cima
        _topmost.Tick += (_, _) => { Topmost = false; Topmost = true; };
        SourceInitialized += (_, _) => SetClickThrough(true);
        Loaded += (_, _) => _topmost.Start();
        Closed += (_, _) => _topmost.Stop();
    }

    public void ShowSample(LiveSample s)
    {
        var ping = s.PingMs is double p ? $"{p:N0} ms" : "-- ms";
        Text.Text = Loc.T($"{ping}  ·  perda {s.LossPercent:N0}%  ·  jitter {s.JitterMs:N0} ms");
        Dot.Fill = s.Quality switch
        {
            LinkQuality.Good => new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50)),
            LinkQuality.Fair => new SolidColorBrush(Color.FromRgb(0xE3, 0xB3, 0x41)),
            _ => new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49)),
        };
    }

    /// <summary>"" esconde a parte da rede (jogos sem servidor: só os FPS).</summary>
    public void ShowWaiting(string text)
    {
        Text.Text = text;
        Dot.Fill = new SolidColorBrush(Color.FromRgb(0x7C, 0x87, 0x99));
        Text.Visibility = Dot.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Fit();
    }

    /// <summary>A parte dos FPS, à esquerda da rede (o texto traz o separador "│" quando a rede aparece). null ou "" esconde-a.</summary>
    public void ShowFps(string? text)
    {
        FpsText.Text = text;
        FpsText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        Fit();
    }

    // sem nada para mostrar (jogo sem servidor e sem FPS) a caixa some, em vez de ficar vazia por cima do jogo
    void Fit() =>
        Box.Visibility = _moving || FpsText.Visibility == Visibility.Visible || Text.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Modo mover: deixa de ser "transparente aos cliques" para se poder arrastar.</summary>
    public void StartMoving()
    {
        _moving = true;
        SetClickThrough(false);
        MoveHint.Visibility = Visibility.Visible;
        Box.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
        Cursor = Cursors.SizeAll;
        Fit();
        Activate();
    }

    void StopMoving()
    {
        _moving = false;
        SetClickThrough(true);
        MoveHint.Visibility = Visibility.Collapsed;
        Box.BorderBrush = Brushes.Transparent;
        Cursor = null;
        Fit();
        Moved?.Invoke(Left, Top);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_moving) DragMove();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_moving && e.Key is Key.Enter or Key.Escape) StopMoving();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_moving) StopMoving();
    }

    void SetClickThrough(bool on)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLong(hwnd, GwlExStyle) | WsExToolWindow | WsExLayered;
        style = on ? style | WsExTransparent | WsExNoActivate : style & ~WsExTransparent & ~WsExNoActivate;
        SetWindowLong(hwnd, GwlExStyle, style);
    }

    const int GwlExStyle = -20, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x08000000;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
