using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

/// <summary>
/// Tutorial da primeira vez: um balão por baixo de cada aba, que abre a página e explica para que serve.
/// </summary>
public sealed class Tutorial
{
    sealed record Step(string Tab, string Title, string Text);

    static readonly Step[] Steps =
    {
        new("HomeTab", Loc.T("Início"), Loc.T("A saúde do teu PC e o que vale a pena fazer primeiro. Começa sempre por aqui.")),
        new("DiagTab", Loc.T("Diagnóstico"), Loc.T("Todos os problemas encontrados (ecrãs azuis, RAM, discos, rede) e o botão para resolver cada um.")),
        new("OptimizeTab", Loc.T("Otimização"), Loc.T("Escolhe um perfil (Gaming, Privacidade…) e aplica. Cada ajuste pode ser desfeito.")),
        new("AppsTab", "Apps", Loc.T("Instala as apps que queres de uma vez e mantém-nas atualizadas.")),
        new("DriversTab", "Drivers", Loc.T("Drivers em falta ou antigos, das fontes oficiais (NVIDIA, AMD, Microsoft).")),
        new("NetworkTab", Loc.T("Rede"), Loc.T("Modo jogo, de onde vem o lag e bloqueio de anúncios.")),
        new("FpsTab", "FPS", Loc.T("Os FPS de cada jogo e um teste de 60 s que prova se as otimizações deram mais FPS.")),
        new("HistoryTab", Loc.T("Histórico"), Loc.T("Tudo o que a app mudou no PC, com um botão para desfazer.")),
    };

    readonly MainWindow _win;
    readonly Panel _nav;
    readonly Func<string, bool> _open;
    readonly Popup _popup = new() { StaysOpen = true, AllowsTransparency = true, Placement = PlacementMode.Bottom, VerticalOffset = 6 };
    readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold };
    readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) };
    readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly Button _next = new() { Margin = new Thickness(0) };
    int _i;

    /// <summary>open: muda para a aba com esse nome (devolve false se não existir).</summary>
    public Tutorial(MainWindow win, Panel nav, Func<string, bool> open)
    {
        _win = win;
        _nav = nav;
        _open = open;
        _text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _count.SetResourceReference(TextBlock.ForegroundProperty, "FaintBrush");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _next.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        _next.Click += (_, _) => Go(_i + 1);
        var skip = new Button { Content = Loc.T("Saltar"), Margin = new Thickness(0, 0, 8, 0) };
        skip.Click += (_, _) => End();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(skip);
        buttons.Children.Add(_next);
        var footer = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_count);

        var card = new Border
        {
            Width = 320, Padding = new Thickness(16, 14, 16, 14), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Child = new StackPanel { Children = { _title, _text, footer } },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = 0.5 },
            Margin = new Thickness(0, 0, 12, 12),
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        _popup.Child = card;

        // o Popup não segue a janela sozinho: volta a pôr-se no sítio, e esconde-se quando a janela perde o foco
        win.LocationChanged += (_, _) => Nudge();
        win.SizeChanged += (_, _) => Nudge();
        win.Deactivated += (_, _) => { if (Running) _popup.IsOpen = false; };
        win.Activated += (_, _) => { if (Running) _popup.IsOpen = true; };
    }

    public bool Running { get; private set; }

    /// <summary>O balão (para as capturas de desenvolvimento).</summary>
    public FrameworkElement Card => (FrameworkElement)_popup.Child;

    public void Start()
    {
        Running = true;
        Go(0);
    }

    void Go(int i)
    {
        // salta abas que não existem nesta versão
        while (i < Steps.Length && !_open(Steps[i].Tab)) i++;
        if (i >= Steps.Length) { End(); return; }
        _i = i;
        var step = Steps[i];
        _title.Text = step.Title;
        _text.Text = step.Text;
        _count.Text = $"{i + 1} / {Steps.Length}";
        _next.Content = i == Steps.Length - 1 ? Loc.T("Concluir") : Loc.T("Seguinte  →");
        _popup.IsOpen = false;
        _popup.PlacementTarget = _nav.Children.OfType<RadioButton>().FirstOrDefault(r => (string)r.Tag == step.Tab);
        _popup.IsOpen = true;
        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase() };
        _popup.Child.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    void Nudge()
    {
        if (!_popup.IsOpen) return;
        _popup.HorizontalOffset += 0.01;
        _popup.HorizontalOffset -= 0.01;
    }

    void End()
    {
        Running = false;
        _popup.IsOpen = false;
        _open("HomeTab");
    }
}
