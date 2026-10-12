using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using PCOptimizer.Core.Drivers;
using PCOptimizer.Core.Optimization;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

public partial class MainWindow
{
    readonly ObservableCollection<DeviceVm> _devices = new();
    readonly ObservableCollection<DriverUpdateVm> _driverUpdates = new();
    readonly ObservableCollection<KeyDriverVm> _keyDrivers = new();
    readonly ObservableCollection<OldPackageVm> _oldPackages = new();
    bool _driversLoaded;
    bool _driversBusy;
    string? _wuError;
    readonly SemaphoreSlim _installGate = new(1, 1); // um instalador de cada vez

    async Task EnsureDriversLoadedAsync()
    {
        if (_driversLoaded) return;
        _driversLoaded = true;
        DeviceList.ItemsSource = _devices;
        DriverUpdateList.ItemsSource = _driverUpdates;
        KeyDriverList.ItemsSource = _keyDrivers;
        OldPackageList.ItemsSource = _oldPackages;
        DriversAdminBox.Visibility = Admin.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        await ScanDriversAsync();
    }

    async void DriversRescan_Click(object sender, RoutedEventArgs e) => await ScanDriversAsync();

    async Task ScanDriversAsync()
    {
        if (_driversBusy) return;
        SetDriversBusy(true, Loc.T("A analisar os drivers…"), Loc.T("A ver o Gestor de Dispositivos e os drivers instalados."));
        try
        {
            // o Windows Update é o mais lento: arranca já em paralelo
            var wu = DriverManager.SearchWindowsUpdateAsync();
            var (devices, keys, old) = await Task.Run(() =>
                (DriverManager.ProblemDevices(), DriverManager.KeyDrivers(), DriverManager.OldPackages()));

            _devices.Clear();
            foreach (var d in devices) _devices.Add(new DeviceVm(d));
            _keyDrivers.Clear();
            foreach (var k in keys.OrderByDescending(k => k.Status == KeyDriverStatus.Missing).ThenByDescending(k => k.Status == KeyDriverStatus.Update))
                _keyDrivers.Add(new KeyDriverVm(k));
            _oldPackages.Clear();
            foreach (var p in old) _oldPackages.Add(new OldPackageVm(p));
            UpdateDriversView();

            // versões novas nas fontes oficiais (NVIDIA, AMD, Catálogo do Microsoft Update)
            DriversSubtitle.Text = Loc.T("A procurar versões novas nos sites oficiais e no Windows Update…");
            try
            {
                var offers = await DriverDownloads.FindOffersAsync(keys, devices);
                foreach (var k in _keyDrivers)
                    k.Offer = offers.FirstOrDefault(o => o.MatchKey == (k.Driver.Category == "Chipset" ? "chipset" : k.Device));
                foreach (var d in _devices)
                    d.Offer = offers.FirstOrDefault(o => o.MatchKey == d.Device.InstanceId);
            }
            catch { /* sem internet: ficam os botões dos sites */ }

            _driverUpdates.Clear();
            _wuError = null;
            try
            {
                foreach (var u in await wu) _driverUpdates.Add(new DriverUpdateVm(u));
            }
            catch (Exception ex)
            {
                _wuError = ex.Message;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("A análise de drivers falhou: ") + ex.Message, "Rigwise", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            SetDriversBusy(false);
            UpdateDriversView();
        }
    }

    void SetDriversBusy(bool busy, string? title = null, string? subtitle = null)
    {
        _driversBusy = busy;
        DriversProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        DriversRescanButton.IsEnabled = !busy;
        DriversFixAllButton.IsEnabled = !busy;
        InstallDriversButton.IsEnabled = !busy;
        DeleteOldButton.IsEnabled = !busy;
        if (title != null) DriversSummary.Text = title;
        if (subtitle != null) DriversSubtitle.Text = subtitle;
    }

    void UpdateDriversView()
    {
        var broken = _devices.Count(d => d.CanReinstall);
        var toUpdate = _keyDrivers.Count(k => k.HasOffer || k.Driver.Status is KeyDriverStatus.Update or KeyDriverStatus.Missing);
        var downloads = _keyDrivers.Count(k => k.HasOffer) + _devices.Count(d => d.HasOffer);
        var oldSize = _oldPackages.Sum(p => p.Package.SizeBytes);

        DrvProblemsCount.Text = broken.ToString();
        DrvProblemsCount.Foreground = broken > 0 ? Palette.Critical : Palette.Ok;
        DrvWuCount.Text = _wuError != null ? "?" : _driverUpdates.Count.ToString();
        DrvWuCount.Foreground = _driverUpdates.Count > 0 ? Palette.Warning : Palette.Ok;
        DrvUpdateCount.Text = toUpdate.ToString();
        DrvUpdateCount.Foreground = toUpdate > 0 ? Palette.Warning : Palette.Ok;
        DrvOldCount.Text = _oldPackages.Count.ToString();
        DrvOldCount.Foreground = _oldPackages.Count > 0 ? Palette.Info : Palette.Ok;
        DrvOldSize.Text = _oldPackages.Count > 0 ? Loc.T($"{SystemInfo.FormatBytes(oldSize)} a libertar") : Loc.T("nada a limpar");

        DeviceEmpty.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DriverUpdateEmpty.Visibility = _driverUpdates.Count == 0 && !_driversBusy ? Visibility.Visible : Visibility.Collapsed;
        DriverUpdateEmpty.Text = _wuError != null
            ? Loc.T("Não foi possível falar com o Windows Update: ") + _wuError
            : Loc.T("✓ Nada em falta.");
        DriverUpdateEmpty.Foreground = _wuError != null ? Palette.Warning : Palette.Ok;
        DriverUpdateBar.Visibility = _driverUpdates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        OldPackageEmpty.Visibility = _oldPackages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OldPackageBar.Visibility = _oldPackages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_driversBusy) return;
        var manual = _keyDrivers.Count(k => !k.HasOffer && k.Driver.Status is KeyDriverStatus.Update or KeyDriverStatus.Missing);
        var auto = broken + _driverUpdates.Count + downloads;
        var total = broken + _driverUpdates.Count + toUpdate;
        DriversSummary.Text = total == 0 ? Loc.T("✓ Os teus drivers estão em ordem") : Loc.T($"{total} coisa(s) para tratar nos drivers");
        DriversSubtitle.Text = manual > 0 ? Loc.T($"{manual} só pelo site do fabricante.") : "";
    }

    // ------------------------------------------------------------------ dispositivos

    async void DeviceReinstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DeviceVm vm } || !EnsureAdmin()) return;
        if (!Confirm(Loc.T($"Reinstalar o driver de \"{vm.Name}\"?\n\nO dispositivo é removido e o Windows deteta-o outra vez e instala o melhor driver que tiver. ") +
                     Loc.T("Pode desligar-se por uns segundos (ex.: o ecrã pisca, a internet cai um momento)."))) return;
        vm.Busy = true;
        vm.Message = "A reinstalar…";
        try { vm.Message = await DriverManager.ReinstallDeviceAsync(vm.Device); }
        catch (Exception ex) { vm.Message = ex.Message; }
        finally { vm.Busy = false; }
    }

    async void DeviceEnable_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DeviceVm vm } || !EnsureAdmin()) return;
        vm.Busy = true;
        try { vm.Message = await DriverManager.EnableDeviceAsync(vm.Device); }
        catch (Exception ex) { vm.Message = ex.Message; }
        finally { vm.Busy = false; }
    }

    void DeviceSearch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DeviceVm vm }) Shell.Open(DriverManager.SearchUrl(vm.Device));
    }

    // ------------------------------------------------------------------ Windows Update

    void OptionalUpdates_Click(object sender, RoutedEventArgs e) => Shell.Open("ms-settings:windowsupdate-optionalupdates");

    void SelectAllDriverUpdates_Click(object sender, RoutedEventArgs e)
    {
        var on = SelectAllDriverUpdates.IsChecked == true;
        foreach (var u in _driverUpdates) u.IsSelected = on;
    }

    async void InstallDrivers_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _driverUpdates.Where(u => u.IsSelected).ToList();
        if (chosen.Count == 0)
        {
            MessageBox.Show(this, Loc.T("Marca os drivers que queres instalar."), "Rigwise");
            return;
        }
        if (!EnsureAdmin()) return;
        var list = string.Join("\n", chosen.Take(10).Select(u => "• " + u.Title));
        if (!Confirm(Loc.T($"Instalar {chosen.Count} driver(s) do Windows Update?\n\n{list}\n\n") +
                     Loc.T("Antes disso é criado um ponto de restauro, para poderes voltar atrás se algo correr mal."))) return;

        SetDriversBusy(true, Loc.T("A instalar drivers…"), Loc.T("A criar um ponto de restauro…"));
        try
        {
            await TweakEngine.CreateRestorePointAsync(Loc.T("Rigwise: antes de instalar drivers"));
            DriversSubtitle.Text = Loc.T("A descarregar e instalar pelo Windows Update (não desligues o PC)…");
            foreach (var u in chosen) { u.Busy = true; u.Message = Loc.T("A instalar…"); }
            var r = await DriverManager.InstallFromWindowsUpdateAsync(chosen.Select(u => u.Update).ToList());
            foreach (var u in chosen) { u.Busy = false; u.Message = r.Message; }
            await DriverManager.ScanForHardwareAsync();
            AskReboot(r);
        }
        finally
        {
            SetDriversBusy(false);
        }
        await ScanDriversAsync();
    }

    void AskReboot(DriverInstallResult r)
    {
        if (r.RebootRequired &&
            MessageBox.Show(this, r.Message + Loc.T("\n\nQueres reiniciar o PC agora? (Guarda o que estiveres a fazer.)"), "Rigwise",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            Shell.Open("shutdown.exe", "/r /t 5");
    }

    // ------------------------------------------------------------------ descarregar das fontes oficiais

    async void OfferInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DriverRowVm { Offer: DriverOffer offer } vm } || !EnsureAdmin()) return;
        if (_driversBusy)
        {
            MessageBox.Show(this, Loc.T("Espera que acabe o que está a decorrer (\"Tratar de tudo\" ou a análise)."), "Rigwise");
            return;
        }
        var size = offer.SizeBytes > 0 ? $" ({DriverRowVm.Size(offer.SizeBytes)})" : "";
        var extra = offer.Kind == OfferKind.Installer
            ? Loc.T("\n\nO instalador corre em silêncio. O ecrã pode piscar ou a internet cair uns segundos. No fim, reinicia o PC.")
            : "";
        if (!Confirm(Loc.T($"Descarregar e instalar {offer.Title}{size}?\n\nFonte: {offer.Source}\n") +
                     Loc.T("Antes disso é criado um ponto de restauro, para poderes voltar atrás.") + extra)) return;

        vm.Busy = true;
        vm.Message = Loc.T("A criar um ponto de restauro…");
        await TweakEngine.CreateRestorePointAsync(Loc.T("Rigwise: antes de instalar ") + offer.Title);
        var r = await InstallOfferAsync(vm, offer);
        if (r.Reboot) AskReboot(new DriverInstallResult(1, 0, true, r.Message));
    }

    /// <summary>Instala <paramref name="offer"/> (recebida à parte: a linha pode mudar enquanto espera pela vez).</summary>
    async Task<(bool Ok, bool Reboot, string Message)> InstallOfferAsync(DriverRowVm vm, DriverOffer offer)
    {
        vm.Busy = true;
        vm.Message = Loc.T("À espera que acabe a outra instalação…");
        await _installGate.WaitAsync();
        try
        {
            vm.Downloading = true;
            vm.Progress = 0;
            vm.Message = Loc.T("A começar o download…");
            var progress = new Progress<(double Percent, string Text)>(p => { vm.Progress = p.Percent; vm.Message = p.Text; });
            var r = await DriverDownloads.DownloadAndInstallAsync(offer, progress, CancellationToken.None);
            // o Progress<T> pode entregar a última atualização depois do fim: dar-lhe tempo antes de escrever o resultado
            await Task.Delay(200);
            vm.Message = r.Message;
            if (r.Ok && ReferenceEquals(vm.Offer, offer)) vm.Offer = null;
            return r;
        }
        catch (Exception ex)
        {
            vm.Message = Ui.Error(ex, "Falhou: ");
            return (false, false, vm.Message);
        }
        finally
        {
            vm.Downloading = false;
            vm.Busy = false;
            _installGate.Release();
            UpdateDriversView();
        }
    }

    // ------------------------------------------------------------------ drivers importantes

    void KeyDriverOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: KeyDriverVm { Driver.Url: string url } }) Shell.Open(url);
    }

    // ------------------------------------------------------------------ versões antigas

    async void DeleteOld_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _oldPackages.Where(p => p.IsSelected).ToList();
        if (chosen.Count == 0) return;
        if (!EnsureAdmin()) return;
        var size = SystemInfo.FormatBytes(chosen.Sum(p => p.Package.SizeBytes));
        var summary = Loc.T($"{chosen.Count} versão(ões) antiga(s) de drivers, {size}.\n") +
                      Loc.T("Os drivers que estás a usar ficam como estão; o Windows recusa apagar um driver em uso.");
        if (!SafetyConfirm.Ask(this, DeleteTier.Normal, Loc.T("Remover versões antigas de drivers"), summary, Array.Empty<string>(), "")) return;

        SetDriversBusy(true, Loc.T("A remover versões antigas…"));
        try { await DeletePackagesAsync(chosen); }
        finally { SetDriversBusy(false); }
        await ScanDriversAsync();
    }

    async Task<int> DeletePackagesAsync(IEnumerable<OldPackageVm> packages)
    {
        var ok = 0;
        foreach (var p in packages)
        {
            p.Busy = true;
            try
            {
                p.Message = await DriverManager.DeletePackageAsync(p.Package);
                if (p.Message.StartsWith('✓')) ok++;
            }
            catch (Exception ex) { p.Message = ex.Message; }
            finally { p.Busy = false; }
        }
        return ok;
    }

    // ------------------------------------------------------------------ tratar de tudo

    async void DriversFixAll_Click(object sender, RoutedEventArgs e)
    {
        var updates = _driverUpdates.ToList();
        var broken = _devices.Where(d => d.CanReinstall).ToList();
        var old = _oldPackages.ToList();
        var manual = _keyDrivers.Where(k => !k.HasOffer && k.Driver.Status is KeyDriverStatus.Update or KeyDriverStatus.Missing).ToList();
        // cópia de cada oferta agora: o ciclo não pode depender de a linha ainda ter a oferta quando chegar a vez dela
        var offers = _keyDrivers.Where(k => k.HasOffer).Cast<DriverRowVm>().Concat(_devices.Where(d => d.HasOffer))
            .Select(vm => (Vm: vm, Offer: vm.Offer!)).ToList();

        if (updates.Count + broken.Count + old.Count + offers.Count == 0)
        {
            if (manual.Count == 0)
            {
                if (Confirm(Loc.T("Os drivers já estão em ordem ✓\n\nQueres ir à Otimização para deixar o Windows no máximo para o que usas o PC?")))
                    Tabs.SelectedItem = OptimizeTab;
                return;
            }
            OfferManual(manual, Loc.T("Não há nada que a app consiga instalar sozinha."));
            return;
        }
        if (!EnsureAdmin()) return;

        var plan = new List<string> { Loc.T("• Criar um ponto de restauro (para poderes voltar atrás)") };
        if (updates.Count > 0) plan.Add(Loc.T($"• Instalar {updates.Count} driver(s) do Windows Update"));
        foreach (var o in offers)
            plan.Add(Loc.T($"• Descarregar e instalar {o.Offer.Title}") + (o.Offer.SizeBytes > 0 ? $" ({DriverRowVm.Size(o.Offer.SizeBytes)})" : "") + $" — {o.Offer.Source}");
        if (broken.Count > 0) plan.Add(Loc.T($"• Reinstalar {broken.Count} dispositivo(s) sem driver / com erro"));
        if (old.Count > 0) plan.Add(Loc.T($"• Remover {old.Count} versão(ões) antiga(s) de drivers ({SystemInfo.FormatBytes(old.Sum(p => p.Package.SizeBytes))})"));
        if (!Confirm(Loc.T("A app vai:\n\n") + string.Join("\n", plan) +
                     Loc.T("\n\nAlguns dispositivos podem desligar-se por uns segundos. Continuar?"), Loc.T("Rigwise — tratar dos drivers"))) return;

        SetDriversBusy(true, Loc.T("A tratar dos drivers…"), Loc.T("A criar um ponto de restauro…"));
        var report = new List<string>();
        var reboot = false;
        try
        {
            report.Add(await TweakEngine.CreateRestorePointAsync(Loc.T("Rigwise: antes de tratar dos drivers")));

            if (updates.Count > 0)
            {
                DriversSubtitle.Text = Loc.T($"A instalar {updates.Count} driver(s) pelo Windows Update (não desligues o PC)…");
                var r = await DriverManager.InstallFromWindowsUpdateAsync(updates.Select(u => u.Update).ToList());
                report.Add(Loc.T("Windows Update: ") + r.Message);
                reboot |= r.RebootRequired;
            }

            // os que estão na fila ficam bloqueados (sem botões) até chegar a vez deles
            foreach (var o in offers)
            {
                o.Vm.Busy = true;
                o.Vm.Message = Loc.T("Na fila…");
            }
            foreach (var o in offers)
            {
                var title = o.Offer.Title;
                DriversSubtitle.Text = Loc.T($"A descarregar e instalar {title}…");
                var r = await InstallOfferAsync(o.Vm, o.Offer);
                report.Add($"{title}: {r.Message}");
                reboot |= r.Reboot;
            }

            // os que ficaram resolvidos pelo driver descarregado não precisam de ser reinstalados
            foreach (var d in broken.Where(d => d.Message?.StartsWith('✓') != true))
            {
                DriversSubtitle.Text = $"A reinstalar {d.Name}…";
                d.Busy = true;
                try { d.Message = await DriverManager.ReinstallDeviceAsync(d.Device); }
                catch (Exception ex) { d.Message = ex.Message; }
                d.Busy = false;
                report.Add($"{d.Name}: {d.Message}");
            }

            if (old.Count > 0)
            {
                DriversSubtitle.Text = Loc.T("A remover versões antigas…");
                var ok = await DeletePackagesAsync(old);
                report.Add(Loc.T($"Versões antigas: {ok} de {old.Count} removidas."));
            }

            DriversSubtitle.Text = Loc.T("A procurar hardware novo…");
            await DriverManager.ScanForHardwareAsync();
        }
        finally
        {
            SetDriversBusy(false);
        }
        await ScanDriversAsync();

        var text = "Feito:\n\n" + string.Join("\n", report.Select(r => "• " + r));
        if (reboot) text += Loc.T("\n\n⚠ Reinicia o PC para acabar de instalar.");
        if (manual.Count > 0) OfferManual(manual, text);
        else if (MessageBox.Show(this, text + Loc.T("\n\nQueres ir à Otimização para deixar o Windows no máximo?"), "Rigwise",
                     MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            Tabs.SelectedItem = OptimizeTab;
    }

    /// <summary>Drivers que só o fabricante tem (ex.: chipset AMD): a app abre os sites oficiais.</summary>
    void OfferManual(List<KeyDriverVm> manual, string intro)
    {
        var list = string.Join("\n", manual.Select(k => $"• {k.Driver.Category}: {k.Device} → {k.Driver.UrlLabel}"));
        var urls = manual.Select(k => k.Driver.Url).OfType<string>().Distinct().ToList();
        if (MessageBox.Show(this, intro + Loc.T("\n\nFalta atualizar à mão (o Windows Update não os tem):\n\n") + list +
                                  (urls.Count > 0 ? Loc.T($"\n\nAbrir {(urls.Count == 1 ? Loc.T("o site oficial") : Loc.T($"os {urls.Count} sites oficiais"))} no browser?") : ""),
                "Rigwise", urls.Count > 0 ? MessageBoxButton.YesNo : MessageBoxButton.OK, MessageBoxImage.Information) == MessageBoxResult.Yes)
            foreach (var u in urls) Shell.Open(u);
    }
}
