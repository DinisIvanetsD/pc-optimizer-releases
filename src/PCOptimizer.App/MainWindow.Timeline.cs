using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCOptimizer.Core.History;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

public sealed record TimelineRowVm(TimelineEvent Event)
{
    public string Time => Event.When.ToString("HH:mm");
    public string Title => Event.Title;
    public string Detail => Event.Detail;
    public bool HasDetail => Event.Detail.Length > 0;
    public string Source => Event.Source;
    public string Icon => Event.Kind switch
    {
        TimelineKind.Crash => "💥",
        TimelineKind.AppCrash => "⚠",
        TimelineKind.Driver => "🔧",
        TimelineKind.Update => "⬆",
        TimelineKind.Program or TimelineKind.StoreApp => "📦",
        _ => "⚡",
    };
    public SolidColorBrush TitleBrush => Event.Kind == TimelineKind.Crash ? Palette.Critical : (SolidColorBrush)Application.Current.FindResource("TextBrush");
}

public sealed record TimelineDayVm(string Day, List<TimelineRowVm> Items);
public sealed record SuspectVm(string Title, string Why);

/// <summary>Histórico › O que mudou?</summary>
public partial class MainWindow
{
    List<TimelineEvent>? _timeline;
    string _timelineFilter = "";

    async void HistMode_Changed(object sender, RoutedEventArgs e)
    {
        if (HistoryAppPanel is null) return; // ainda a construir a janela
        var timeline = HistModeTimeline.IsChecked == true;
        HistoryAppPanel.Visibility = timeline ? Visibility.Collapsed : Visibility.Visible;
        TimelinePanel.Visibility = timeline ? Visibility.Visible : Visibility.Collapsed;
        if (timeline && _timeline is null) await LoadTimelineAsync();
    }

    async Task LoadTimelineAsync()
    {
        TimelineStatus.Text = Loc.T("A ler os registos do Windows…");
        try
        {
            _timeline = await Task.Run(() => Timeline.Build());
            var suspects = Timeline.Suspects(_timeline).Take(5)
                .Select(s => new SuspectVm($"{s.Change.When:dd/MM HH:mm} · {s.Change.Title}", s.Why)).ToList();
            SuspectsList.ItemsSource = suspects;
            SuspectsCard.Visibility = suspects.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowTimeline();
        }
        catch (Exception ex) { TimelineStatus.Text = Ui.Error(ex); }
    }

    void TimelineFilter_Changed(object sender, RoutedEventArgs e)
    {
        _timelineFilter = (sender as RadioButton)?.Tag as string ?? "";
        if (_timeline != null) ShowTimeline();
    }

    void ShowTimeline()
    {
        var shown = _timeline!.Where(e => _timelineFilter switch
        {
            "Crash" => e.Kind is TimelineKind.Crash or TimelineKind.AppCrash,
            "Driver" => e.Kind == TimelineKind.Driver,
            "Update" => e.Kind == TimelineKind.Update,
            "Program" => e.Kind is TimelineKind.Program or TimelineKind.StoreApp,
            "Rigwise" => e.Kind == TimelineKind.Rigwise,
            _ => true,
        }).ToList();
        var culture = Loc.Language == "en" ? CultureInfo.GetCultureInfo("en-GB") : CultureInfo.GetCultureInfo("pt-PT");
        TimelineDays.ItemsSource = shown.GroupBy(e => e.When.Date)
            .Select(g => new TimelineDayVm(g.Key.ToString("dddd, d MMMM", culture), g.Select(e => new TimelineRowVm(e)).ToList())).ToList();
        TimelineStatus.Text = Loc.T($"{shown.Count} acontecimento(s) nos últimos 30 dias");
    }
}
