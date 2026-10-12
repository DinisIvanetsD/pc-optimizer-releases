using System.Windows;
using System.Windows.Media;
using PCOptimizer.Core.Apps;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

public sealed class AppPickVm : Observable
{
    /// <param name="idleText">o que o mosaico diz quando não está marcado nem instalado</param>
    public AppPickVm(AppEntry e, string idleText = "")
    {
        Entry = e;
        _idleText = idleText;
    }
    public AppEntry Entry { get; }
    readonly string _idleText;

    bool _selected, _installed, _busy, _shown = true;
    string _status = "";

    public bool Selected { get => _selected; set { Set(ref _selected, value); Raise(nameof(StateText)); } }
    public bool Installed { get => _installed; set { Set(ref _installed, value); Raise(nameof(CanSelect)); Raise(nameof(StatusBrush)); Raise(nameof(StateText)); } }
    public bool Busy { get => _busy; set { Set(ref _busy, value); Raise(nameof(CanSelect)); } }
    public bool CanSelect => !Installed && !Busy;
    public bool Shown { get => _shown; set => Set(ref _shown, value); }
    public string Status { get => _status; set { Set(ref _status, value); Raise(nameof(StateText)); } }
    public string StateText => Status.Length > 0 ? Status : Selected ? Loc.T("Selecionada") : _idleText;
    public SolidColorBrush StatusBrush => Installed ? Palette.Ok : Palette.Muted;
    public string Initial => Entry.Name.Length > 0 ? Entry.Name[..1].ToUpperInvariant() : "?";
    public Geometry? Logo => AppLogos.For(Entry.Id)?.Geometry;
    public Brush? LogoBrush => AppLogos.For(Entry.Id)?.Brush;
    public bool HasLogo => Logo != null;
}

public sealed class AppUpdateVm(AppUpdate u) : Observable
{
    public AppUpdate Update { get; } = u;
    bool _selected = true, _busy, _done;
    string _status = "";
    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public bool Busy { get => _busy; set { Set(ref _busy, value); Raise(nameof(Idle)); } }
    public bool Done { get => _done; set { Set(ref _done, value); Raise(nameof(Idle)); Raise(nameof(StatusBrush)); } }
    public bool Idle => !Busy && !Done;
    public string Status { get => _status; set => Set(ref _status, value); }
    public SolidColorBrush StatusBrush => Done ? Palette.Ok : Palette.Muted;
}

public sealed class AppGroupVm(string name, List<AppPickVm> apps) : Observable
{
    public string Name { get; } = name;
    public List<AppPickVm> Apps { get; } = apps;
    bool _shown = true;
    public bool Shown { get => _shown; set => Set(ref _shown, value); }
    public string Summary => Loc.T($"{Apps.Count(a => a.Installed)} de {Apps.Count} instaladas");
    public void Refresh() => Raise(nameof(Summary));
}

public partial class MainWindow
{
    List<AppPickVm>? _appPicks;
    List<AppGroupVm> _appGroups = new();
    bool _appsBusy;

    async Task EnsureAppsTabLoadedAsync()
    {
        if (_appPicks != null) return;
        _appPicks = AppCatalog.All.Select(a => new AppPickVm(a, Loc.T("Por instalar"))).ToList();
        _appGroups = _appPicks.GroupBy(p => p.Entry.Category).Select(g => new AppGroupVm(g.Key, g.ToList())).ToList();
        AppGroups.ItemsSource = _appGroups;
        await Task.WhenAll(RefreshInstalledAppsAsync(), RefreshBloatAsync());
    }

    void AppsFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return; // o IsChecked do XAML dispara isto antes de os outros controlos existirem
        AppsSearchPlaceholder.Visibility = AppsSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyAppsFilter();
    }

    void ApplyAppsFilter()
    {
        var q = AppsSearchBox.Text.Trim();
        bool? installed = AppsShowMissing.IsChecked == true ? false : AppsShowInstalled.IsChecked == true ? true : null;
        foreach (var g in _appGroups)
        {
            foreach (var p in g.Apps)
                p.Shown = (q.Length == 0 || p.Entry.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                          && (installed is null || p.Installed == installed);
            g.Shown = g.Apps.Any(p => p.Shown);
            g.Refresh();
        }
        AppsEmpty.Visibility = _appGroups.Count > 0 && !_appGroups.Any(g => g.Shown) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ apps do Windows

    List<AppPickVm> _bloat = new();

    async Task RefreshBloatAsync()
    {
        var installed = await WindowsBloat.InstalledAsync();
        _bloat = WindowsBloat.All.Where(a => installed.Contains(a.Id)).Select(a => new AppPickVm(a, Loc.T("Pré-instalada"))).ToList();
        BloatList.ItemsSource = _bloat;
        UpdateBloatSummary();
    }

    void UpdateBloatSummary()
    {
        var sel = _bloat.Count(p => p.Selected);
        RemoveBloatButton.IsEnabled = sel > 0;
        BloatStatus.Text = _bloat.Count == 0 ? Loc.T("✓ Nenhuma app dispensável do Windows instalada.")
            : sel > 0 ? Loc.T($"{sel} selecionada(s). Voltas a tê-las pela Microsoft Store.")
            : Loc.T($"{_bloat.Count} instalada(s). Voltas a tê-las pela Microsoft Store.");
    }

    void BloatPick_Click(object sender, RoutedEventArgs e) => UpdateBloatSummary();

    async void RemoveBloat_Click(object sender, RoutedEventArgs e)
    {
        var todo = _bloat.Where(p => p.Selected).ToList();
        if (todo.Count == 0 || !Confirm(Loc.T($"Remover {todo.Count} app(s) do Windows?\n\n") + string.Join(", ", todo.Select(p => p.Entry.Name)) +
                                        Loc.T("\n\nPara voltar a ter alguma, instala-a da Microsoft Store."))) return;
        RemoveBloatButton.IsEnabled = false;
        var failed = 0;
        foreach (var p in todo)
        {
            p.Busy = true;
            p.Status = Loc.T("A remover…");
            try
            {
                await WindowsBloat.RemoveAsync(p.Entry);
                p.Selected = false;
                p.Installed = true; // fica desativada na lista
                p.Status = Loc.T("Removida");
            }
            catch (Exception ex)
            {
                failed++;
                p.Status = Loc.T("Falhou");
                App.Log(ex);
            }
            finally { p.Busy = false; }
        }
        UpdateBloatSummary();
        BloatStatus.Text = failed == 0 ? Loc.T($"✓ {todo.Count} app(s) removida(s).") : Loc.T($"{todo.Count - failed} removida(s), {failed} falharam.");
    }

    async Task RefreshInstalledAppsAsync()
    {
        if (_appPicks is null) return;
        if (!await Winget.IsAvailableAsync())
        {
            AppsStatus.Text = Loc.T("Falta o winget. Instala o \"Instalador de Aplicações\" da Microsoft Store e volta aqui.");
            UpgradeAllButton.IsEnabled = InstallAppsButton.IsEnabled = false;
            return;
        }
        AppsStatus.Text = Loc.T("A ver o que tens instalado…");
        _ = RefreshUpdatesAsync();
        var installed = await Winget.InstalledIdsAsync();
        foreach (var p in _appPicks)
        {
            p.Installed = installed.Contains(p.Entry.Id);
            p.Status = p.Installed ? Loc.T("Instalada") : "";
            if (p.Installed) p.Selected = false;
        }
        UpdateInstallSummary();
    }

    void UpdateInstallSummary()
    {
        if (_appPicks is null) return;
        var sel = _appPicks.Count(p => p.Selected);
        InstallAppsButton.IsEnabled = sel > 0 && !_appsBusy;
        InstallAppsButton.Content = sel > 0 ? Loc.T($"⬇  Instalar {sel} app(s)") : Loc.T("⬇  Instalar selecionadas");
        ApplyAppsFilter();
        UpgradeAllButton.IsEnabled = !_appsBusy;
        if (!_appsBusy)
            AppsStatus.Text = sel > 0
                ? Loc.T($"{sel} selecionada(s).")
                : Loc.T($"{_appPicks.Count(p => p.Installed)} de {_appPicks.Count} já instaladas. Marca as que queres.");
    }

    void AppPick_Click(object sender, RoutedEventArgs e) => UpdateInstallSummary();

    async void InstallApps_Click(object sender, RoutedEventArgs e)
    {
        if (_appPicks is null) return;
        var todo = _appPicks.Where(p => p.Selected && !p.Installed).ToList();
        if (todo.Count == 0) return;
        _appsBusy = true;
        UpdateInstallSummary();
        AppsProgress.Visibility = Visibility.Visible;
        AppsProgress.Maximum = todo.Count;
        AppsProgress.Value = 0;
        var failed = 0;
        foreach (var p in todo)
        {
            p.Busy = true;
            p.Status = Loc.T("A instalar…");
            AppsStatus.Text = Loc.T($"A instalar {p.Entry.Name} ({AppsProgress.Value + 1} de {todo.Count})…");
            try
            {
                await Winget.InstallAsync(p.Entry);
                p.Installed = true;
                p.Selected = false;
                p.Status = Loc.T("Instalada");
            }
            catch (Exception ex)
            {
                failed++;
                p.Status = Loc.T("Falhou");
                p.Selected = false;
                App.Log(ex);
            }
            finally { p.Busy = false; }
            AppsProgress.Value++;
        }
        _appsBusy = false;
        AppsProgress.Visibility = Visibility.Collapsed;
        UpdateInstallSummary();
        AppsStatus.Text = failed == 0
            ? Loc.T($"✓ {todo.Count} app(s) instalada(s).")
            : Loc.T($"{todo.Count - failed} instalada(s), {failed} falharam (passa o rato no erro ou vê o Histórico).");
    }

    // ------------------------------------------------------------------ atualizações

    List<AppUpdateVm>? _updates;

    async Task RefreshUpdatesAsync()
    {
        UpdatesCard.Visibility = Visibility.Visible;
        UpdatesStatus.Text = Loc.T("A procurar versões novas…");
        UpgradeSelectedButton.IsEnabled = false;
        try
        {
            _updates = (await Winget.UpgradesAsync()).Select(u => new AppUpdateVm(u)).ToList();
            UpdatesList.ItemsSource = _updates;
            UpdateUpdatesSummary();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            UpdatesStatus.Text = Ui.Error(ex, Loc.T("Não deu para procurar atualizações: "));
        }
    }

    void UpdateUpdatesSummary()
    {
        if (_updates is null) return;
        var pending = _updates.Where(u => !u.Done).ToList();
        var sel = pending.Count(u => u.Selected);
        UpgradeSelectedButton.IsEnabled = sel > 0 && !_appsBusy;
        UpgradeSelectedButton.Content = sel > 0 ? Loc.T($"⬆  Atualizar {sel} app(s)") : Loc.T("⬆  Atualizar selecionadas");
        UpdatesStatus.Text = _updates.Count == 0
            ? Loc.T("✓ As tuas apps já estão na versão mais recente.")
            : Loc.T($"{pending.Count} app(s) com versão nova. Só aparecem as que o winget conhece.");
    }

    void UpdatePick_Click(object sender, RoutedEventArgs e) => UpdateUpdatesSummary();

    async void UpgradeSelected_Click(object sender, RoutedEventArgs e)
    {
        var todo = _updates?.Where(u => u.Selected && !u.Done).ToList() ?? new();
        if (todo.Count == 0) return;
        _appsBusy = true;
        UpdateInstallSummary();
        UpdateUpdatesSummary();
        var failed = 0;
        foreach (var u in todo)
        {
            u.Busy = true;
            u.Status = Loc.T("A atualizar…");
            try
            {
                await Winget.UpgradeAsync(u.Update);
                u.Done = true;
                u.Status = Loc.T("✓ Atualizada");
            }
            catch (Exception ex)
            {
                failed++;
                u.Status = Loc.T("Falhou");
                App.Log(ex);
            }
            finally { u.Busy = false; }
        }
        _appsBusy = false;
        UpdateInstallSummary();
        UpdateUpdatesSummary();
        if (failed > 0) UpdatesStatus.Text = Loc.T($"{todo.Count - failed} atualizada(s), {failed} falharam (vê o Histórico).");
    }

    async void UpgradeAllApps_Click(object sender, RoutedEventArgs e)
    {
        // mostra o que vai mudar antes de mexer em qualquer app
        if (_updates is null) await RefreshUpdatesAsync();
        var pending = _updates?.Where(u => !u.Done).ToList() ?? new();
        if (_updates != null && pending.Count == 0)
        {
            AppsStatus.Text = Loc.T("✓ As tuas apps já estão na versão mais recente.");
            return;
        }
        var names = string.Join("\n", pending.Take(15).Select(u => $"• {u.Update.Name}  ({u.Update.Version} → {u.Update.Available})"))
                    + (pending.Count > 15 ? Loc.T($"\n… e mais {pending.Count - 15}") : "");
        var question = _updates is null ? Loc.T("Atualizar todas as apps que o winget conhece?") // a lista falhou: não sei quais
                                        : Loc.T($"Atualizar {pending.Count} app(s)?\n\n") + names;
        if (!Confirm(question + Loc.T("\n\nO Windows pode pedir permissão para algumas."))) return;
        _appsBusy = true;
        UpdateInstallSummary();
        AppsProgress.Visibility = Visibility.Visible;
        AppsProgress.IsIndeterminate = true;
        AppsStatus.Text = Loc.T("A atualizar as apps (pode demorar e o Windows pode pedir permissão)…");
        try { AppsStatus.Text = await Winget.UpgradeAllAsync(); }
        catch (Exception ex) { AppsStatus.Text = Ui.Error(ex, Loc.T("Não deu para atualizar: ")); }
        finally
        {
            _appsBusy = false;
            AppsProgress.IsIndeterminate = false;
            AppsProgress.Visibility = Visibility.Collapsed;
            InstallAppsButton.IsEnabled = _appPicks?.Any(p => p.Selected) == true;
            UpgradeAllButton.IsEnabled = true;
        }
        await RefreshUpdatesAsync();
    }
}
