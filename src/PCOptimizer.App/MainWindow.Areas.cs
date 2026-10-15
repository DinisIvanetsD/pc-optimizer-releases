using System.Windows;
using System.Windows.Controls;

namespace PCOptimizer.App;

/// <summary>
/// O menu tem 6 áreas; cada área junta as abas de um assunto e mostra-as como separadores ao lado do menu.
/// As abas continuam a ser as mesmas: só muda a forma de lá chegar.
/// </summary>
public partial class MainWindow
{
    public static readonly (string Key, string[] Tabs)[] Areas =
    {
        ("home", new[] { "HomeTab" }),
        ("fix", new[] { "DiagTab", "OptimizeTab", "DriversTab" }),
        ("games", new[] { "NetworkTab", "FpsTab" }),
        ("clean", new[] { "StorageTab", "StartupTab", "AppsTab" }),
        ("system", new[] { "ComponentsTab", "MonitorTab", "RamTab" }),
        ("history", new[] { "HistoryTab" }),
    };

    /// <summary>A área de uma aba (null para as Definições, que abrem pela engrenagem).</summary>
    public static string? AreaOf(string? tab) => Areas.FirstOrDefault(a => a.Tabs.Contains(tab)).Key;

    /// <summary>A última aba aberta em cada área: voltar a uma área abre onde se estava.</summary>
    readonly Dictionary<string, string> _lastTabInArea = new();

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string area } || Tabs is null) return;
        var current = (Tabs.SelectedItem as TabItem)?.Name;
        if (AreaOf(current) == area) return; // já está nesta área (a sincronização chamou isto)
        var tabs = Areas.First(a => a.Key == area).Tabs;
        var target = _lastTabInArea.TryGetValue(area, out var last) ? last : tabs[0];
        if (FindName(target) is TabItem tab) Tabs.SelectedItem = tab;
    }

    void Settings_Click(object sender, RoutedEventArgs e) => Tabs.SelectedItem = SettingsTab;

    /// <summary>Marca a área da aba aberta e mostra os separadores dessa área. Chamado a cada mudança de aba.</summary>
    void SyncNav()
    {
        var name = (Tabs.SelectedItem as TabItem)?.Name;
        var area = AreaOf(name);
        if (area != null && name != null) _lastTabInArea[area] = name;
        foreach (var nav in NavPanel.Children.OfType<RadioButton>())
        {
            var on = (string)nav.Tag == area;
            if (nav.IsChecked != on) nav.IsChecked = on;
        }

        SubNavPanel.Children.Clear();
        var tabs = Areas.FirstOrDefault(a => a.Key == area).Tabs;
        if (tabs is not { Length: > 1 }) return;
        foreach (var t in tabs)
        {
            if (FindName(t) is not TabItem tab) continue;
            var chip = new RadioButton
            {
                Content = tab.Header, // já traduzido pelo Localizer
                Tag = t,
                GroupName = "subnav",
                IsChecked = t == name,
                Margin = new Thickness(0, 0, 6, 0),
                Style = (Style)FindResource("ChipButton"),
            };
            chip.Checked += (_, _) => Tabs.SelectedItem = tab;
            SubNavPanel.Children.Add(chip);
        }
    }
}
