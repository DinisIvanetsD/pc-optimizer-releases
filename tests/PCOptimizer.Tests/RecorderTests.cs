using PCOptimizer.Core.Monitoring;
using Xunit;

namespace PCOptimizer.Tests;

[Collection("loc")]
public class RecorderTests
{
    static readonly DateTime Mark = new(2026, 10, 10, 20, 0, 0);

    /// <summary>45 s de medições calmas; "spike" muda as dos segundos perto da marca.</summary>
    static Recording Rec(Func<RecSample, RecSample>? spike = null)
    {
        var samples = Enumerable.Range(-30, 46).Select(i =>
        {
            var s = new RecSample(Mark.AddSeconds(i), 20, 8, 5, 40, 60, 20, new[] { "game 15%" });
            return spike != null && i >= -3 && i <= 1 ? spike(s) : s;
        }).ToList();
        return new Recording(Mark, "game", "1.1.1.1", samples);
    }

    [Fact]
    public void Calm_recording_says_nothing_unusual()
    {
        var o = Assert.Single(RecordingAnalyzer.Analyze(Rec()));
        Assert.Equal(ObservationKind.Nothing, o.Kind);
    }

    [Fact]
    public void Disk_spike_is_reported()
    {
        var obs = RecordingAnalyzer.Analyze(Rec(s => s with { Disk = 98 }));
        Assert.Contains(obs, o => o.Kind == ObservationKind.Disk);
        Assert.DoesNotContain(obs, o => o.Kind == ObservationKind.Network);
    }

    [Fact]
    public void Cpu_spike_names_the_app_that_appeared()
    {
        var obs = RecordingAnalyzer.Analyze(Rec(s => s with { Cpu = 99, TopApps = new[] { "MsMpEng 60%", "game 15%" } }));
        var cpu = Assert.Single(obs, o => o.Kind == ObservationKind.Cpu);
        Assert.Contains("MsMpEng", cpu.Text);
    }

    [Fact]
    public void Lost_pings_point_to_the_network()
    {
        var obs = RecordingAnalyzer.Analyze(Rec(s => s with { PingMs = null }));
        Assert.Contains(obs, o => o.Kind == ObservationKind.Network);
    }

    [Fact]
    public void Too_short_recording_is_not_analysed()
    {
        var r = new Recording(Mark, null, null, new[] { new RecSample(Mark, 99, 8, 99, null, null, null, Array.Empty<string>()) });
        Assert.Equal(ObservationKind.Nothing, Assert.Single(RecordingAnalyzer.Analyze(r)).Kind);
    }
}
