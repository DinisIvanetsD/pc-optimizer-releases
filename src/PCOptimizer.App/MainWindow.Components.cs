using System.Windows;
using PCOptimizer.Core.Hardware;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

public sealed record DetailVm(string Label, string Value);

public sealed class ComponentVm(Component c)
{
    public string Kind => c.Kind;
    public string Icon => c.Icon;
    public string Name => c.Name;
    public List<DetailVm> Details { get; } = c.Details.Select(d => new DetailVm(d.Label, d.Value)).ToList();
    public string? Warning => c.Warning;
    public bool HasWarning => c.Warning != null;
    public string? Note => c.Note;
    public bool HasNote => c.Note != null;}

public partial class MainWindow
{
    List<Component>? _components;

    async Task EnsureComponentsLoadedAsync()
    {
        if (_components != null) return;
        await LoadComponentsAsync();
    }

    async Task LoadComponentsAsync()
    {
        ComponentsStatus.Text = Loc.T("A ler as peças do PC…");
        _components = await Components.ListAsync();
        // 3 colunas com alturas próprias (pela ordem de leitura: 1.º, 2.º, 3.º cartão na 1.ª linha…)
        var cards = _components.Select(c => new ComponentVm(c)).ToList();
        ComponentsList.ItemsSource = Enumerable.Range(0, 3).Select(col => cards.Where((_, i) => i % 3 == col).ToList()).ToList();
        var warnings = _components.Count(c => c.Warning != null);
        ComponentsStatus.Text = warnings == 0
            ? Loc.T("✓ Nada de estranho nas peças. Os drivers estão na aba Drivers.")
            : Loc.T($"{warnings} coisa(s) a ver: lê os avisos a amarelo.");
    }

    async void ComponentsRefresh_Click(object sender, RoutedEventArgs e) => await LoadComponentsAsync();

    void ComponentsCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_components is null) return;
        var text = string.Join("\n\n", _components.Select(c =>
            $"{c.Kind}: {c.Name}\n" + string.Join("\n", c.Details.Select(d => $"  {d.Label}: {d.Value}")) + (c.Warning is null ? "" : $"\n  ⚠ {c.Warning}") + (c.Note is null ? "" : $"\n  {c.Note}")));
        try
        {
            Clipboard.SetText(text);
            ComponentsStatus.Text = Loc.T("✓ Copiado.");
        }
        catch (Exception ex) { ComponentsStatus.Text = Ui.Error(ex, ""); }
    }
}
