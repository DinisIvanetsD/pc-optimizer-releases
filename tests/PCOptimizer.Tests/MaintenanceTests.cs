using PCOptimizer.Core.Maintenance;
using PCOptimizer.Core.Settings;
using Xunit;

namespace PCOptimizer.Tests;

/// <summary>Manutenção automática a sério (só lê o PC), com definições numa pasta de teste.</summary>
[Collection("history")]
public class MaintenanceTests : IDisposable
{
    readonly string _file = Path.Combine(Path.GetTempPath(), "pcopt-maint-" + Guid.NewGuid().ToString("N")[..8] + ".json");

    public MaintenanceTests() => Environment.SetEnvironmentVariable("PCOPT_SETTINGS_FILE", _file);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PCOPT_SETTINGS_FILE", null);
        try { File.Delete(_file); } catch { }
    }

    [Fact]
    public void So_corre_quando_esta_na_altura()
    {
        var s = new AppSettings { AutoMaintenance = true, MaintenanceDays = 7 };
        Assert.True(MaintenanceRunner.IsDue(s));                 // nunca correu
        s.LastMaintenance = DateTime.Now.AddDays(-2);
        Assert.False(MaintenanceRunner.IsDue(s));
        s.LastMaintenance = DateTime.Now.AddDays(-8);
        Assert.True(MaintenanceRunner.IsDue(s));
        s.AutoMaintenance = false;
        Assert.False(MaintenanceRunner.IsDue(s));                // desligada
    }

    [Fact]
    public async Task Nao_repete_o_mesmo_aviso()
    {
        var s = new AppSettings { AutoMaintenance = true, MaintainDiagnostics = false, MaintainDrivers = false, MaintainDisk = true };
        var first = await MaintenanceRunner.RunAsync(s);
        var second = await MaintenanceRunner.RunAsync(s);

        Assert.Empty(second);                                    // o que já foi avisado não volta a aparecer
        Assert.All(first, n => Assert.Equal("storage", n.Tab));
        Assert.NotNull(s.LastMaintenance);
        Assert.True(File.Exists(_file));                         // gravou nas definições de teste, não nas verdadeiras
    }
}
