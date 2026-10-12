using System.IO;
using System.Windows;
using System.Windows.Controls;
using PCOptimizer.Core.Storage;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

/// <summary>A página "Espaço em disco": programas e jogos, pastas que ocupam espaço e limpeza rápida.</summary>
public partial class MainWindow
{
    List<DriveSummary> _drives = new();
    List<AppVm> _allApps = new();
    readonly List<CleanupVm> _cleanup = QuickCleanup.Items.Select(i => new CleanupVm(i)).ToList();
    bool _storageLoaded;
    CancellationTokenSource? _folderCts;
    DirNode? _currentFolder;

    /// <summary>Carrega a página da primeira vez que é aberta.</summary>
    async Task EnsureStorageLoadedAsync()
    {
        if (_storageLoaded) return;
        _storageLoaded = true;
        CleanupList.ItemsSource = _cleanup;
        UpdateUtilitySliderText();
        await LoadDrivesAsync();
        await LoadAppsAsync();
    }

    async Task LoadDrivesAsync()
    {
        _drives = await StorageDrives.ListAsync();
        var trim = _drives.Any(StorageDrives.IsSsd) ? await StorageDrives.TrimEnabledAsync() : null;
        DrivesList.ItemsSource = _drives.Select(d => new DriveVm(d, trim)).ToList();

        AppDriveCombo.ItemsSource = new[] { Loc.T("Todos os discos") }.Concat(_drives.Select(d => $"{d.Letter} ({d.Media})")).ToList();
        AppDriveCombo.SelectedIndex = 0;
        FolderDriveCombo.ItemsSource = _drives.Select(d => $"{d.Letter}  {d.Label} ({d.Media})").ToList();
        FolderDriveCombo.SelectedIndex = 0;
    }

    async void DriveOptimize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DriveVm vm }) return;
        if (StorageDrives.IsHdd(vm.Drive) && !Confirm(Loc.T($"Desfragmentar {vm.Drive.Letter}?\n\nPode demorar mais de uma hora num disco grande. Podes continuar a usar o PC, mas fica mais lento até acabar.")))
            return;
        if (!EnsureAdmin()) return;
        vm.Busy = true;
        vm.Message = StorageDrives.IsSsd(vm.Drive) ? Loc.T("A enviar TRIM…") : Loc.T("A desfragmentar… (podes mudar de aba)");
        try { vm.Message = await StorageDrives.OptimizeAsync(vm.Drive); }
        catch (Exception ex) { vm.Message = Ui.Error(ex, ""); }
        finally { vm.Busy = false; }
    }

    async Task LoadAppsAsync()
    {
        AppsSummary.Text = Loc.T("A ler o que tens instalado…");
        var media = _drives.ToDictionary(d => d.Letter, d => d.Media, StringComparer.OrdinalIgnoreCase);
        var apps = await AppInventory.ScanAsync(new Progress<string>(s => AppsSummary.Text = s));
        _allApps = apps.Select(a => new AppVm(a, media)).ToList();
        ApplyAppFilter();
        _ = MeasureAppSizesAsync();
    }

    /// <summary>O registo do Windows nem sempre diz o tamanho certo: medimos as pastas em segundo plano.</summary>
    async Task MeasureAppSizesAsync()
    {
        var toMeasure = _allApps.Where(v => !v.App.SizeMeasured && v.App.InstallLocation != null).ToList();
        var done = 0;
        using var gate = new SemaphoreSlim(3);
        var tasks = toMeasure.Select(async vm =>
        {
            await gate.WaitAsync();
            try
            {
                var size = await Task.Run(() => StorageDrives.FolderSize(vm.App.InstallLocation!));
                if (size > 0) vm.App.SizeBytes = size;
                vm.App.SizeMeasured = true;
                AppInventory.Reclassify(vm.App);
                vm.RefreshSize();
            }
            finally
            {
                gate.Release();
                if (Interlocked.Increment(ref done) % 10 == 0) await Dispatcher.InvokeAsync(UpdateAppsSummary);
            }
        });
        await Task.WhenAll(tasks);

        // não contar duas vezes: a pasta do Steam inclui os jogos, que já aparecem à parte
        foreach (var vm in toMeasure)
        {
            var prefix = vm.App.InstallLocation!.TrimEnd('\\') + "\\";
            var nested = _allApps.Where(o => o != vm && o.App.InstallLocation != null &&
                                             o.App.InstallLocation.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                 .Sum(o => o.App.SizeBytes ?? 0);
            if (nested > 0 && vm.App.SizeBytes is long s)
            {
                vm.App.SizeBytes = Math.Max(0, s - nested);
                AppInventory.Reclassify(vm.App);
            }
        }
        foreach (var vm in _allApps) vm.RefreshSize();
        ApplyAppFilter();
    }

    void AppFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (AppSearchPlaceholder != null) AppSearchPlaceholder.Visibility = string.IsNullOrEmpty(AppSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) ApplyAppFilter();
    }

    void UtilitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateUtilitySliderText();
        if (IsLoaded) ApplyAppFilter();
    }

    void UtilityChip_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && UtilitySlider != null && double.TryParse(tag, out var v))
            UtilitySlider.Value = v;
    }

    void UpdateUtilitySliderText()
    {
        if (UtilitySliderText is null) return;
        var v = (int)UtilitySlider.Value;
        // manter os atalhos sincronizados com a barra
        if (UtilityChips != null)
            foreach (var chip in UtilityChips.Children.OfType<RadioButton>())
                chip.IsChecked = (string)chip.Tag == v.ToString();
        UtilitySliderText.Text = v >= 100 ? Loc.T("A mostrar tudo")
            : v <= 15 ? Loc.T("Só os provavelmente inúteis")
            : v <= 40 ? Loc.T("Inúteis e não usados")
            : v <= 60 ? Loc.T("Tudo o que usas pouco")
            : Loc.T($"Utilidade até {v}");
        UtilitySliderText.Foreground = v <= 40 ? Palette.Critical : v <= 60 ? Palette.Warning : Palette.Ok;
    }

    void ApplyAppFilter()
    {
        IEnumerable<AppVm> q = _allApps;
        var search = AppSearchBox.Text.Trim();
        if (search.Length > 0)
            q = q.Where(a => a.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) || a.App.Publisher.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        if (AppDriveCombo.SelectedIndex > 0)
        {
            var letter = _drives[AppDriveCombo.SelectedIndex - 1].Letter;
            q = q.Where(a => string.Equals(a.App.Drive, letter, StringComparison.OrdinalIgnoreCase));
        }
        var kind = (AppKindCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        q = kind switch
        {
            "program" => q.Where(a => a.App.Kind == AppKind.Program),
            "game" => q.Where(a => a.App.Kind == AppKind.Game),
            "store" => q.Where(a => a.App.Kind == AppKind.Store),
            _ => q,
        };
        if (HideEssentialCheck.IsChecked == true) q = q.Where(a => a.App.Kind != AppKind.Essential);
        q = q.Where(a => a.Utility <= UtilitySlider.Value);

        q = ((AppSortCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "useful" => q.OrderByDescending(a => a.Utility).ThenByDescending(a => a.SortSize),
            "size" => q.OrderByDescending(a => a.SortSize),
            "unused" => q.OrderBy(a => a.App.LastUsed ?? DateTime.MinValue).ThenByDescending(a => a.SortSize),
            "name" => q.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => q.OrderBy(a => a.Utility).ThenByDescending(a => a.SortSize),
        };
        var shown = q.ToList();
        // o que deixou de estar visível deixa de estar selecionado (para nunca desinstalar algo que não estás a ver)
        var visible = shown.ToHashSet();
        foreach (var a in _allApps.Where(a => a.IsSelected && !visible.Contains(a))) a.IsSelected = false;
        AppsList.ItemsSource = shown;
        UpdateAppsSummary();

        // lista vazia: explicar porquê em vez de mostrar um buraco
        AppsEmptyBox.Visibility = shown.Count == 0 && _allApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AppsEmptyElevate.Visibility = Admin.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        AppsEmptyText.Text = UtilitySlider.Value < 100
            ? Loc.T("Não há programas inúteis ou que não uses com estes filtros.") +
              (Admin.IsElevated
                  ? Loc.T(" Experimenta \"Pouco usados\" para ver o que tem pouco uso.")
                  : Loc.T($" Atenção: sem administrador, a app só sabe quando usaste {_allApps.Count(a => a.App.LastUsed != null)} dos {_allApps.Count} programas; os outros ficam como \"pouco usados\". ") +
                    Loc.T("Abre como administrador para ler o registo completo de programas abertos do Windows (inclui os jogos lançados pelo Steam, Epic e Riot)."))
            : Loc.T("Experimenta outro filtro ou procura por outro nome.");
        if (BulkUninstallButton != null) UpdateAppSelection();
    }

    void UpdateAppsSummary()
    {
        if (AppsList.ItemsSource is not List<AppVm> shown) return;
        var total = shown.Sum(a => Math.Max(0, a.App.SizeBytes ?? 0));
        var unused = _allApps.Where(a => a.App.Verdict is AppVerdict.Unused or AppVerdict.Useless).ToList();
        var unusedBytes = unused.Sum(a => Math.Max(0, a.App.SizeBytes ?? 0));
        var measuring = _allApps.Count(a => !a.App.SizeMeasured && a.App.InstallLocation != null);
        AppsSummary.Text = Loc.T($"{shown.Count} de {_allApps.Count} itens · {SystemInfo.FormatBytes(total)} na lista · ") +
                           Loc.T($"{unused.Count} que não usas ocupam {SystemInfo.FormatBytes(unusedBytes)}") +
                           (measuring > 0 ? Loc.T($" · a medir {measuring} pastas…") : "");
    }

    async void RefreshApps_Click(object sender, RoutedEventArgs e)
    {
        await LoadDrivesAsync();
        await LoadAppsAsync();
    }

    void AppFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AppVm vm } && vm.App.InstallLocation is { } dir) Shell.Open(dir);
    }

    // ---------------- Selecionar e desinstalar ----------------

    void AppSelect_Click(object sender, RoutedEventArgs e) => UpdateAppSelection();

    void SelectAllApps_Click(object sender, RoutedEventArgs e)
    {
        var on = SelectAllApps.IsChecked == true;
        if (AppsList.ItemsSource is List<AppVm> shown)
            foreach (var vm in shown) vm.IsSelected = on;
        UpdateAppSelection();
    }

    void UpdateAppSelection()
    {
        var sel = _allApps.Where(a => a.IsSelected).ToList();
        BulkUninstallButton.IsEnabled = sel.Count > 0;
        if (sel.Count == 0)
        {
            AppSelectionText.Text = "";
            return;
        }
        var bytes = sel.Sum(a => Math.Max(0, a.App.SizeBytes ?? 0));
        var inUse = sel.Count(a => a.Tier == DeleteTier.Triple);
        AppSelectionText.Text = $"{sel.Count} selecionado(s) · liberta ~{SystemInfo.FormatBytes(bytes)}" +
                                (inUse > 0 ? Loc.T($" · ⚠ {inUse} que estás a usar") : "");
        BulkUninstallButton.Content = Loc.T($"🗑  Desinstalar {sel.Count} selecionado(s)");
    }

    async void AppUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AppVm vm }) return;
        await UninstallManyAsync(new List<AppVm> { vm });
    }

    async void BulkUninstall_Click(object sender, RoutedEventArgs e) =>
        await UninstallManyAsync(_allApps.Where(a => a.IsSelected).ToList());

    async Task UninstallManyAsync(List<AppVm> items)
    {
        items = items.Where(a => a.CanSelect).ToList();
        if (items.Count == 0) return;

        var tier = items.Max(a => a.Tier);
        var bytes = items.Sum(a => Math.Max(0, a.App.SizeBytes ?? 0));
        var names = string.Join("\n", items.Take(15).Select(a => $"• {a.Name}" + (a.App.SizeBytes is long s ? $" ({SystemInfo.FormatBytes(s)})" : "")))
                    + (items.Count > 15 ? Loc.T($"\n… e mais {items.Count - 15}") : "");
        var risky = items.Where(a => a.Tier >= DeleteTier.Double).Select(a => $"{a.Name} — {a.LastUsedText}").ToList();
        var reason = tier == DeleteTier.Triple
            ? Loc.T("Alguns destes programas estão a ser USADOS por ti (abriste-os recentemente):")
            : Loc.T("Alguns destes programas não têm registo de uso, mas podem ser úteis (apps que correm em segundo plano):");
        if (!SafetyConfirm.Ask(this, tier, items.Count == 1 ? $"Desinstalar \"{items[0].Name}\"" : Loc.T($"Desinstalar {items.Count} programas"),
                Loc.T($"{names}\n\nLiberta cerca de {SystemInfo.FormatBytes(bytes)}."), risky, reason))
            return;

        if (!Admin.IsElevated && items.Any(a => a.App.RegistryKeyPath?.StartsWith("HKLM") == true) && items.Count > 1 &&
            Confirm(Loc.T("Dica: como administrador não aparece um pedido de autorização para cada programa.\n\nQueres reabrir a app como administrador primeiro?")))
        {
            Elevate_Click(this, new RoutedEventArgs());
            return;
        }

        BulkUninstallButton.IsEnabled = false;
        var done = 0;
        var i = 0;
        foreach (var vm in items)
        {
            i++;
            vm.Busy = true;
            vm.Message = Loc.T($"A desinstalar ({i} de {items.Count})…");
            AppSelectionText.Text = Loc.T($"A desinstalar {i} de {items.Count}: {vm.Name}…");
            try
            {
                var r = await AppInventory.UninstallAsync(vm.App);
                vm.Message = r.Message;
                if (r.Done)
                {
                    done++;
                    vm.IsSelected = false;
                }
            }
            catch (Exception ex)
            {
                vm.Message = Ui.Error(ex);
            }
            finally
            {
                vm.Busy = false;
            }
        }

        // tirar da lista o que foi mesmo desinstalado
        _allApps.RemoveAll(a => items.Contains(a) && a.Message == "✓ Desinstalado.");
        foreach (var a in _allApps) a.IsSelected = false;
        SelectAllApps.IsChecked = false;
        ApplyAppFilter();
        UpdateAppSelection();
        AppSelectionText.Text = Loc.T($"✓ {done} de {items.Count} desinstalado(s).") + (done < items.Count ? Loc.T(" Vê a mensagem em cada um dos outros.") : "");
        await LoadDrivesAsync();
    }

    // ---------------- Separadores ----------------

    void StorageSeg_Checked(object sender, RoutedEventArgs e)
    {
        if (AppsPanel is null) return;
        AppsPanel.Visibility = SegApps.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FoldersPanel.Visibility = SegFolders.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CleanupPanel.Visibility = SegCleanup.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (SegCleanup.IsChecked == true && _cleanup.All(c => c.SizeText == "a medir…")) _ = MeasureCleanupAsync();
        // a análise de pastas começa sozinha da primeira vez (no disco do Windows)
        if (SegFolders.IsChecked == true && _scanRoot is null && _folderCts is null && _drives.Count > 0)
            FolderScan_Click(FolderScanButton, new RoutedEventArgs());
    }

    // ---------------- Pastas ----------------

    async void FolderScan_Click(object sender, RoutedEventArgs e)
    {
        if (FolderDriveCombo.SelectedIndex < 0) return;
        var drive = _drives[FolderDriveCombo.SelectedIndex];
        _folderCts = new CancellationTokenSource();
        FolderScanButton.IsEnabled = false;
        FolderStopButton.IsEnabled = true;
        FolderList.ItemsSource = null;
        FolderNavBar.Visibility = Visibility.Collapsed;
        try
        {
            var root = await FolderScanner.ScanAsync(drive.Root,
                new Progress<FolderScanner.ScanProgress>(p => FolderStatus.Text = Loc.T($"{p.Files:N0} ficheiros · {SystemInfo.FormatBytes(p.Bytes)} · {p.Current}")),
                _folderCts.Token);
            if (_allApps.Count == 0) await LoadAppsAsync(); // para saber que pastas são de programas instalados
            _advisor = new FolderAdvisor(_allApps.Select(a => a.App));
            _scanRoot = root;
            FolderModeBar.Visibility = Visibility.Visible;
            FolderModeBrowse.IsChecked = true;
            FolderStatus.Text = Loc.T($"✓ {drive.Letter}: {root.Files:N0} ficheiros, {SystemInfo.FormatBytes(root.Size)} contados.") +
                                (Admin.IsElevated ? "" : Loc.T(" (Sem administrador, algumas pastas do sistema não são contadas.)"));
            ShowFolder(root);
        }
        catch (OperationCanceledException)
        {
            FolderStatus.Text = Loc.T("Análise parada.");
        }
        finally
        {
            FolderScanButton.IsEnabled = true;
            FolderStopButton.IsEnabled = false;
        }
    }

    void FolderStop_Click(object sender, RoutedEventArgs e) => _folderCts?.Cancel();

    FolderAdvisor? _advisor;

    void ShowFolder(DirNode node)
    {
        _currentFolder = node;
        StorageScroll.ScrollToTop(); // ao entrar numa pasta, começa do topo da lista nova
        _advisor ??= new FolderAdvisor(_allApps.Select(a => a.App));
        FolderNavBar.Visibility = Visibility.Visible;
        FolderUpButton.IsEnabled = node.Parent != null;
        FolderPathText.Text = $"{node.FullPath}   ·   {SystemInfo.FormatBytes(node.Size)}";
        var safe = _advisor.SafeBytes(node);
        FolderSafeText.Text = safe > 0
            ? Loc.T($"● {SystemInfo.FormatBytes(safe)} aqui dentro são seguros de apagar (caches, temporários, restos)")
            : Loc.T("Nada aqui dentro foi identificado como seguro de apagar.");
        FolderList.ItemsSource = node.Children
            .Where(c => c.Size > 0 || c.Locked)
            .Select(c => new DirVm(c, node.Size, _advisor.Advise(c), _advisor.SafeBytes(c)))
            .Where(v => MatchesFolderFilter(v.Advice))
            .ToList();
        FolderSelectBar.Visibility = Visibility.Visible;
        SelectAllSafeFolders.IsChecked = false;
        UpdateFolderSelection();
    }

    DirNode? _scanRoot;
    string _folderFilter = "all";

    bool MatchesFolderFilter(FolderAdvice a) => _folderFilter switch
    {
        "safe" => a.CanRecycle,
        "caution" => a.Safety is FolderSafety.Caution or FolderSafety.Unknown,
        "danger" => a.Safety == FolderSafety.Danger,
        _ => true,
    };

    void FolderFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        _folderFilter = tag;
        if (_scanRoot is null) return;
        if (FolderModeSafe.IsChecked == true) ShowSafeOnly();
        else if (_currentFolder != null) ShowFolder(_currentFolder);
    }

    void FolderMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_scanRoot is null || FolderNavBar is null) return;
        if (FolderModeSafe.IsChecked == true) ShowSafeOnly();
        else ShowFolder(_currentFolder ?? _scanRoot);
    }

    /// <summary>Todas as pastas seguras de apagar do disco analisado, das maiores para as mais pequenas.</summary>
    void ShowSafeOnly()
    {
        if (_scanRoot is null) return;
        _advisor ??= new FolderAdvisor(_allApps.Select(a => a.App));
        var found = new List<DirNode>();
        void Walk(DirNode n)
        {
            foreach (var c in n.Children)
            {
                if (c.IsFilesGroup || c.Locked || c.Size <= 0) continue;
                if (_advisor.Advise(c).CanRecycle) found.Add(c); // não descer mais: a pasta toda é segura
                else Walk(c);
            }
        }
        Walk(_scanRoot);
        found = found.Where(n => n.Size >= 1 << 20).OrderByDescending(n => n.Size).ToList(); // ignorar migalhas < 1 MB
        var total = found.Sum(n => n.Size);

        FolderNavBar.Visibility = Visibility.Collapsed;
        FolderSelectBar.Visibility = Visibility.Visible;
        FolderStatus.Text = Loc.T($"🧹 {found.Count} pastas seguras de apagar · {SystemInfo.FormatBytes(total)} no total. Fecha as apps antes de apagar as caches delas.");
        FolderList.ItemsSource = found.Select(n => new DirVm(n, total, _advisor.Advise(n), n.Size)
        {
            DisplayOverride = "🧹  " + n.FullPath,
        }).ToList();
        SelectAllSafeFolders.IsChecked = false;
        UpdateFolderSelection();
    }

    void FolderSelect_Click(object sender, RoutedEventArgs e) => UpdateFolderSelection();

    void SelectAllSafeFolders_Click(object sender, RoutedEventArgs e)
    {
        var on = SelectAllSafeFolders.IsChecked == true;
        if (FolderList.ItemsSource is List<DirVm> shown)
            foreach (var vm in shown) vm.IsSelected = on && vm.Advice.CanRecycle; // "todas as seguras": só as verdes
        UpdateFolderSelection();
    }

    void UpdateFolderSelection()
    {
        var sel = (FolderList.ItemsSource as List<DirVm>)?.Where(v => v.IsSelected).ToList() ?? new();
        BulkRecycleButton.IsEnabled = sel.Count > 0;
        BulkRecycleButton.Content = sel.Count == 0 ? Loc.T("🗑  Enviar selecionadas para a Reciclagem") : Loc.T($"🗑  Enviar {sel.Count} para a Reciclagem");
        FolderSelectionText.Text = sel.Count == 0
            ? Loc.T("Marca as pastas a apagar. As «Não apagar» estão bloqueadas; as de «Cuidado» pedem 3 confirmações.")
            : $"{sel.Count} selecionada(s) · {SystemInfo.FormatBytes(sel.Sum(v => v.Node.Size))}" +
              (sel.Any(v => v.Tier == DeleteTier.Triple) ? Loc.T(" · ⚠ inclui pastas com dados de apps") : "");
    }

    async void BulkRecycle_Click(object sender, RoutedEventArgs e)
    {
        var sel = (FolderList.ItemsSource as List<DirVm>)?.Where(v => v.IsSelected && v.CanDelete).ToList() ?? new();
        await RecycleManyAsync(sel);
    }

    async void FolderRecycle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DirVm vm } && vm.CanDelete) await RecycleManyAsync(new List<DirVm> { vm });
    }

    /// <summary>Envia pastas para a Reciclagem, com as confirmações do nível mais arriscado entre elas.</summary>
    async Task RecycleManyAsync(List<DirVm> items)
    {
        items = items.Where(v => v.CanDelete).ToList();
        if (items.Count == 0) return;

        var tier = items.Max(v => v.Tier);
        var bytes = items.Sum(v => v.Node.Size);
        var list = string.Join("\n", items.Take(12).Select(v => $"• {v.Node.FullPath} ({v.SizeText})")) +
                   (items.Count > 12 ? Loc.T($"\n… e mais {items.Count - 12}") : "");
        var risky = items.Where(v => v.Tier >= DeleteTier.Double).Select(v => $"{v.Node.Name} — {v.What}").ToList();
        if (!SafetyConfirm.Ask(this, tier,
                items.Count == 1 ? Loc.T($"Enviar \"{items[0].Node.Name}\" para a Reciclagem") : Loc.T($"Enviar {items.Count} pastas para a Reciclagem"),
                Loc.T($"{list}\n\nTotal: {SystemInfo.FormatBytes(bytes)}.\nFecha primeiro as apps a que pertencem. Podes recuperar tudo na Reciclagem enquanto não a esvaziares."),
                risky, Loc.T("Algumas destas pastas têm dados de apps (sessões, definições, saves) ou não sei bem o que são:")))
            return;

        BulkRecycleButton.IsEnabled = false;
        long freed = 0;
        var i = 0;
        foreach (var vm in items)
        {
            i++;
            vm.Busy = true;
            vm.Message = Loc.T($"A enviar para a Reciclagem ({i} de {items.Count})…");
            FolderSelectionText.Text = Loc.T($"A enviar {i} de {items.Count}…");
            var (b, n, failed) = await Task.Run(() => FolderAdvisor.RecycleContents(vm.Node.FullPath));
            freed += b;
            vm.Message = failed == 0
                ? Loc.T($"✓ {n:N0} itens ({SystemInfo.FormatBytes(b)}) enviados para a Reciclagem.")
                : Loc.T($"✓ {SystemInfo.FormatBytes(b)} enviados. {failed} itens estavam em uso e ficaram (fecha a app e tenta outra vez).");
            vm.IsSelected = false;

            // atualizar os tamanhos sem voltar a analisar o disco todo
            for (var p = vm.Node.Parent; p != null; p = p.Parent) p.Size = Math.Max(0, p.Size - b);
            vm.Node.Size = Math.Max(0, vm.Node.Size - b);
            if (failed == 0) vm.Node.Children.Clear();
            _advisor?.Forget(vm.Node);
            vm.Busy = false;
        }

        FolderSelectionText.Text = Loc.T($"✓ {SystemInfo.FormatBytes(freed)} enviados para a Reciclagem. Para libertar mesmo o espaço, esvazia a Reciclagem (em Limpeza rápida).");
        await LoadDrivesAsync();
        await Task.Delay(3000);
        if (FolderModeSafe.IsChecked == true) ShowSafeOnly();
        else if (_currentFolder != null) ShowFolder(_currentFolder);
    }

    void FolderOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DirVm vm } && vm.CanOpen) ShowFolder(vm.Node);
    }

    void FolderList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FolderList.SelectedItem is DirVm { CanOpen: true } vm) ShowFolder(vm.Node);
    }

    void FolderUp_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolder?.Parent is { } parent) ShowFolder(parent);
    }

    void FolderExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolder != null && Directory.Exists(_currentFolder.FullPath)) Shell.Open(_currentFolder.FullPath);
    }

    // ---------------- Limpeza rápida ----------------

    Task MeasureCleanupAsync() => Task.WhenAll(_cleanup.Select(c => c.MeasureAsync()));

    async void CleanupRefresh_Click(object sender, RoutedEventArgs e) => await MeasureCleanupAsync();

    async void CleanupOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CleanupVm vm } && vm.Item.Open is { } open)
        {
            var msg = await open();
            if (!string.IsNullOrEmpty(msg)) vm.Message = msg;
        }
    }

    async void CleanupClean_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CleanupVm vm } button || vm.Item.Clean is null) return;
        if (vm.Item.NeedsAdmin && !EnsureAdmin()) return;
        if (vm.Item.ConfirmText != null && !Confirm(vm.Item.ConfirmText)) return;
        button.IsEnabled = false;
        try
        {
            vm.Message = "A limpar…";
            var before = vm.SizeText;
            vm.Message = await vm.Item.Clean();
            PCOptimizer.Core.History.ChangeLog.Record(PCOptimizer.Core.History.ChangeKind.Cleanup, $"Limpeza: {vm.Name}", $"Antes: {before}");
            await vm.MeasureAsync();
            await LoadDrivesAsync();
        }
        catch (Exception ex)
        {
            vm.Message = Ui.Error(ex);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}
