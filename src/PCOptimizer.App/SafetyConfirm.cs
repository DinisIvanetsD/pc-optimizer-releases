using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

/// <summary>Quanto custa apagar/desinstalar: quanto mais útil, mais confirmações.</summary>
public enum DeleteTier
{
    /// <summary>Não apagável (essencial / sistema).</summary>
    Blocked = 0,
    /// <summary>Uma confirmação (inútil, não usado, cache).</summary>
    Normal = 1,
    /// <summary>Duas confirmações (pouco usado, provavelmente seguro).</summary>
    Double = 2,
    /// <summary>Três confirmações, a última a escrever "APAGAR" (em uso, dados de apps, não sei).</summary>
    Triple = 3,
}

public static class SafetyConfirm
{
    public static string Word => Loc.T("APAGAR");

    /// <param name="action">ex.: "Desinstalar" ou "Enviar para a Reciclagem"</param>
    /// <param name="summary">o que vai acontecer (lista de itens, tamanho)</param>
    /// <param name="riskyItems">itens que são úteis/arriscados, para os mostrar na 2.ª confirmação</param>
    public static bool Ask(Window owner, DeleteTier tier, string action, string summary, IReadOnlyList<string> riskyItems, string riskReason)
    {
        if (tier == DeleteTier.Blocked)
        {
            MessageBox.Show(owner, Loc.T("Isto está bloqueado: é um componente essencial do sistema ou um programa/pasta do Windows. Apagar podia estragar o PC."),
                "Rigwise", MessageBoxButton.OK, MessageBoxImage.Stop);
            return false;
        }

        // 1.ª confirmação
        var steps = (int)tier;
        if (MessageBox.Show(owner, $"{action}?\n\n{summary}" + (steps > 1 ? Loc.T($"\n\n(Confirmação 1 de {steps})") : ""),
                "Rigwise", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return false;
        if (tier == DeleteTier.Normal) return true;

        // 2.ª confirmação: mostrar o que é útil
        var list = string.Join("\n", riskyItems.Take(12).Select(i => "• " + i)) + (riskyItems.Count > 12 ? Loc.T($"\n… e mais {riskyItems.Count - 12}") : "");
        if (MessageBox.Show(owner, Loc.T($"⚠ ATENÇÃO: {riskReason}\n\n{list}\n\nTens mesmo a certeza?\n\n(Confirmação 2 de {steps})"),
                Loc.T("Rigwise — tens a certeza?"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return false;
        if (tier == DeleteTier.Double) return true;

        // 3.ª confirmação: escrever APAGAR
        return TypeToConfirm(owner, Loc.T($"Última confirmação (3 de 3).\n\n{action} itens que estás a usar ou que guardam dados.\nPara continuar, escreve {Word} na caixa."));
    }

    static bool TypeToConfirm(Window owner, string message)
    {
        var win = new Window
        {
            Title = Loc.T("Rigwise — confirmação final"),
            Owner = owner,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)Application.Current.Resources["BgBrush"],
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextBrush"],
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 13,
        };
        WindowTheme.Apply(win);

        var box = new TextBox { Margin = new Thickness(0, 12, 0, 0), FontSize = 16, CharacterCasing = CharacterCasing.Upper };
        var ok = new Button { Content = "Confirmar", IsEnabled = false, Style = (Style)Application.Current.Resources["DangerButton"], Margin = new Thickness(0) };
        var cancel = new Button { Content = "Cancelar", IsCancel = true };
        box.TextChanged += (_, _) => ok.IsEnabled = box.Text.Trim() == Word;
        ok.Click += (_, _) => win.DialogResult = true;
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && ok.IsEnabled) win.DialogResult = true; };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "⚠", FontSize = 28, Foreground = Palette.Critical });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), LineHeight = 20 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        win.Content = panel;
        win.Loaded += (_, _) => box.Focus();
        return win.ShowDialog() == true;
    }
}
