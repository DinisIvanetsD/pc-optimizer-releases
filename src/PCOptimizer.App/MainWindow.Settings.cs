using System.Windows;
using System.Windows.Controls;
using PCOptimizer.Core.Settings;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

public partial class MainWindow
{
    static AppSettings S => AppSettings.Current;
    bool _loadingSettings;

    // ------------------------------------------------------------------ duas colunas em ecrãs largos

    List<UIElement>? _settingsCards;
    bool _settingsTwoColumns;

    void SettingsScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var two = e.NewSize.Width >= 1200;
        if (two == _settingsTwoColumns) return;
        _settingsTwoColumns = two;
        _settingsCards ??= SettingsCol1.Children.Cast<UIElement>().ToList();
        // alturas medidas com tudo numa coluna (a largura de cada cartão não muda)
        var heights = _settingsCards.ToDictionary(c => c, c => c is FrameworkElement f ? f.ActualHeight : 0);
        if (heights.Values.Sum() == 0) // a aba ainda não foi mostrada: divide pelo número de cartões
            foreach (var c in _settingsCards) heights[c] = c.Visibility == Visibility.Visible ? 1 : 0;
        SettingsCol1.Children.Clear();
        SettingsCol2.Children.Clear();
        var half = heights.Values.Sum() / 2;
        double left = 0;
        foreach (var card in _settingsCards)
        {
            // pela ordem de leitura: enche a coluna da esquerda até meio e o resto vai para a direita
            var toLeft = !two || left < half;
            if (toLeft) left += heights[card];
            (toLeft ? SettingsCol1 : SettingsCol2).Children.Add(card);
        }
        SettingsCol2Def.Width = two ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    /// <summary>Aplica as definições guardadas ao arrancar (antes de a janela aparecer).</summary>
    void ApplySettingsAtStartup()
    {
        NotifyCheck.IsChecked = S.Notifications;
        BackgroundCheck.IsChecked = S.CloseToTray;
        // a chave Run pode ter sido apagada por outro programa: o registo manda
        if (S.StartWithWindows != AutoStart.IsEnabled)
        {
            S.StartWithWindows = AutoStart.IsEnabled;
            S.Save();
        }
    }

    void LoadSettingsPage()
    {
        _loadingSettings = true;
        SetStartWithWindows.IsChecked = AutoStart.IsEnabled;
        SetCloseToTray.IsChecked = S.CloseToTray;
        SetNotifications.IsChecked = S.Notifications;
        SetAutoMaintenance.IsChecked = S.AutoMaintenance;
        SetMaintainDiagnostics.IsChecked = S.MaintainDiagnostics;
        SetMaintainDrivers.IsChecked = S.MaintainDrivers;
        SetMaintainDisk.IsChecked = S.MaintainDisk;
        SetCheckUpdates.IsChecked = S.CheckUpdates;
        SetLanguage.SelectedItem = SetLanguage.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (S.Language ?? "auto"));
        SetGameMode.IsChecked = S.GameMode;
        SetPauseUpdates.IsChecked = S.PauseUpdatesWhilePlaying;
        LoadGameSettings();
        SetMaintenanceDays.SelectedItem = SetMaintenanceDays.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string)i.Tag == S.MaintenanceDays.ToString()) ?? SetMaintenanceDays.Items[2];
        MaintenanceOptions.IsEnabled = S.AutoMaintenance;
        MaintenanceStatus.Text = S.LastMaintenance is DateTime t
            ? Loc.T($"Última verificação: {t:dd/MM/yyyy HH:mm}. Próxima: {t.AddDays(S.MaintenanceDays):dd/MM/yyyy}.")
            : Loc.T("Ainda não foi feita nenhuma verificação automática.");
        UpdateStatus.Text = Loc.T($"Versão instalada: {AppVersion}") +
                            (S.LastUpdateCheck is DateTime u ? Loc.T($" · última procura: {u:dd/MM HH:mm}") : "");
        AboutText.Text = Loc.T($"Rigwise {AppVersion} · .NET {Environment.Version.ToString(2)} · Windows build {Environment.OSVersion.Version.Build}\n") +
                         Loc.T("Versões: github.com/DinisIvanetsD/rigwise");
        DataFolderText.Text = Loc.T($"Histórico, definições, backups dos ajustes e registos de erros ficam em {AppSettings.DataFolder}");
        _loadingSettings = false;
    }

    public static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        try
        {
            if (sender == SetStartWithWindows)
            {
                AutoStart.Set(SetStartWithWindows.IsChecked == true);
                S.StartWithWindows = AutoStart.IsEnabled;
            }
            S.CloseToTray = SetCloseToTray.IsChecked == true;
            S.Notifications = SetNotifications.IsChecked == true;
            S.AutoMaintenance = SetAutoMaintenance.IsChecked == true;
            S.MaintainDiagnostics = SetMaintainDiagnostics.IsChecked == true;
            S.MaintainDrivers = SetMaintainDrivers.IsChecked == true;
            S.MaintainDisk = SetMaintainDisk.IsChecked == true;
            S.CheckUpdates = SetCheckUpdates.IsChecked == true;
            S.PauseUpdatesWhilePlaying = SetPauseUpdates.IsChecked == true;
            if (sender == SetGameMode)
            {
                S.GameMode = SetGameMode.IsChecked == true;
                ApplyGameMode(S.GameMode);
            }

            S.Save();

            // os controlos antigos da aba Monitor seguem as definições
            NotifyCheck.IsChecked = S.Notifications;
            BackgroundCheck.IsChecked = S.CloseToTray;
            MaintenanceOptions.IsEnabled = S.AutoMaintenance;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("Não foi possível guardar: ") + ex.Message, "Rigwise");
            LoadSettingsPage();
        }
    }

    void SetLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || SetLanguage.SelectedItem is not ComboBoxItem { Tag: string lang } || lang == S.Language) return;
        S.Language = lang;
        S.Save();
        var next = lang == "auto" ? Loc.Detect() : lang;
        if (next == Loc.Language) return;
        // a mensagem vai nas duas línguas: a nova ainda não está ativa
        if (MessageBox.Show(this, "A língua muda quando a app reabrir. Reabrir agora?\n\nThe language changes when the app restarts. Restart now?",
                "Rigwise", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ((App)Application.Current).ReleaseSingleInstance();
        Shell.Open(Environment.ProcessPath!);
        _exiting = true;
        Close();
    }

    void SettingCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || SetMaintenanceDays.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        S.MaintenanceDays = int.Parse(tag);
        S.Save();
        LoadSettingsPage();
    }

    /// <summary>Os controlos de alertas da aba Monitor também gravam nas definições.</summary>
    void MonitorAlertSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        S.Notifications = NotifyCheck.IsChecked == true;
        S.CloseToTray = BackgroundCheck.IsChecked == true;
        S.Save();
    }

    void UpdateBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Tabs.SelectedItem = SettingsTab;

    void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(AppSettings.DataFolder);
        Shell.Open(AppSettings.DataFolder);
    }
}
