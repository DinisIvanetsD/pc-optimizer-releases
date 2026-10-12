using PCOptimizer.Core.History;
using Xunit;

namespace PCOptimizer.Tests;

[Collection("loc")]
public class TimelineTests
{
    static readonly DateTime Day = new(2026, 10, 10, 12, 0, 0);

    static TimelineEvent E(int daysAgo, TimelineKind kind, string title) => new(Day.AddDays(-daysAgo), kind, title, "", "teste");

    [Fact]
    public void Driver_installed_one_day_before_the_first_blue_screen_is_a_suspect()
    {
        var events = new[]
        {
            E(0, TimelineKind.Crash, "Ecrã azul"),
            E(2, TimelineKind.Crash, "Ecrã azul"),          // o primeiro
            E(3, TimelineKind.Driver, "Driver NVIDIA 32.0"), // 1 dia antes do primeiro
            E(12, TimelineKind.Driver, "Driver Realtek 1.0"), // longe demais
        };
        var s = Assert.Single(Timeline.Suspects(events));
        Assert.Equal("Driver NVIDIA 32.0", s.Change.Title);
        Assert.Equal(Day.AddDays(-2), s.FirstCrash.When);
    }

    [Fact]
    public void Changes_after_the_first_crash_are_not_suspects()
    {
        var events = new[] { E(5, TimelineKind.Crash, "Ecrã azul"), E(1, TimelineKind.Update, "KB123") };
        Assert.Empty(Timeline.Suspects(events));
    }

    [Fact]
    public void Drivers_come_before_other_changes()
    {
        var events = new[]
        {
            E(0, TimelineKind.Crash, "Ecrã azul"),
            E(1, TimelineKind.Program, "Instalado: X"),
            E(2, TimelineKind.Driver, "Driver AMD"),
        };
        Assert.Equal(TimelineKind.Driver, Timeline.Suspects(events)[0].Change.Kind);
    }

    [Fact]
    public void Report_includes_recordings_suspects_and_timeline()
    {
        var mark = Day;
        var rec = new PCOptimizer.Core.Monitoring.Recording(mark, null, "1.1.1.1", Enumerable.Range(-30, 46)
            .Select(i => new PCOptimizer.Core.Monitoring.RecSample(mark.AddSeconds(i), 20, 8, i is >= -3 and <= 1 ? 98 : 5, null, null, 20, Array.Empty<string>())).ToList());
        var events = new[] { E(1, TimelineKind.Crash, "Ecrã azul"), E(2, TimelineKind.Driver, "Driver NVIDIA 32.0") };
        var html = PCOptimizer.Core.Reports.ReportBuilder.Build(Array.Empty<PCOptimizer.Core.Finding>(), 50, "0.9.0", events, new[] { rec });
        Assert.Contains("Problemas gravados", html);
        Assert.Contains("<svg", html);
        Assert.Contains("Vale a pena investigar", html);
        Assert.Contains("Driver NVIDIA 32.0", html);
    }
}