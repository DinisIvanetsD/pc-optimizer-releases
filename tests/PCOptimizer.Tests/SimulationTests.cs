using PCOptimizer.Core.History;
using PCOptimizer.Core.Optimization;
using PCOptimizer.Core.Storage;
using PCOptimizer.Core.Util;
using Xunit;

namespace PCOptimizer.Tests;

/// <summary>Em modo simulação nada pode mudar no PC — nem um ficheiro.</summary>
[Collection("history")]
public class SimulationTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pcopt-sim-" + Guid.NewGuid().ToString("N")[..8]);

    public SimulationTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "importante.txt"), "não apagar");
        Simulation.Enabled = true;
    }

    public void Dispose()
    {
        Simulation.Enabled = false;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Apagar_pastas_nao_apaga_nada()
    {
        var ex = Assert.Throws<SimulatedException>(() => FolderAdvisor.RecycleContents(_dir));
        Assert.Contains("Reciclagem", ex.Message);
        Assert.True(File.Exists(Path.Combine(_dir, "importante.txt")));
    }

    [Fact]
    public async Task Ajustes_nao_sao_aplicados()
    {
        var tweak = TweakCatalog.Profiles.SelectMany(p => p.Tweaks).First();
        await Assert.ThrowsAsync<SimulatedException>(() => TweakEngine.ApplyAsync(tweak));
    }

    [Fact]
    public void Arranque_nao_e_alterado()
    {
        var item = new StartupItem { Name = "Teste", Command = "x", Location = "y" };
        Assert.Throws<SimulatedException>(() => StartupManager.SetEnabled(item, false));
    }

    [Fact]
    public async Task Ponto_de_restauro_nao_e_criado()
    {
        Assert.Contains("Simulação", await TweakEngine.CreateRestorePointAsync("teste"));
    }

    [Fact]
    public async Task Ferramentas_de_reparacao_so_dizem_o_que_fariam()
    {
        Assert.Contains("SFC", await PCOptimizer.Core.Diagnostics.Tools.SystemFileCheck());
    }

    [Fact]
    public void Fechar_programas_nao_fecha_nada()
    {
        Assert.Throws<SimulatedException>(() => ProcessSampler.Kill("notepad"));
    }
}
