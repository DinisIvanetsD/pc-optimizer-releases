using PCOptimizer.App;
using PCOptimizer.Core.Benchmark;
using Xunit;

namespace PCOptimizer.Tests;

public class FpsCardTests
{
    static readonly DateTime T0 = new(2026, 10, 14, 20, 0, 0);

    static FpsRun Run(FpsRole role, double avg, double low, DateTime when) =>
        new("Counter-Strike 2", "cs2.exe", when, role, 60, avg, low, low * 0.8, 0);

    [Fact]
    public void Linha_do_jogo_com_par_melhor_mostra_a_percentagem_e_o_visto()
    {
        var vm = FpsGameVm.From(new("Counter-Strike 2", "cs2.exe", Run(FpsRole.Session, 120, 80, T0), 118,
            Run(FpsRole.Before, 100, 50, T0), Run(FpsRole.After, 111, 57.5, T0.AddMinutes(5))));
        Assert.Equal("Counter-Strike 2", vm.Name);
        Assert.Equal("Última: 120 FPS (1% 80)", vm.LastText);
        Assert.Equal("Média de 5: 118", vm.AvgText);
        Assert.Equal("Antes 100 → Depois 111 (+11%) ✓", vm.PairText);
    }

    [Fact]
    public void Linha_do_jogo_com_par_pior_nao_leva_visto()
    {
        var vm = FpsGameVm.From(new("Counter-Strike 2", "cs2.exe", null, null,
            Run(FpsRole.Before, 100, 50, T0), Run(FpsRole.After, 93, 50, T0.AddMinutes(5))));
        Assert.Equal("Antes 100 → Depois 93 (−7%)", vm.PairText);
    }

    [Fact]
    public void Linha_do_jogo_sem_sessoes_nem_par_fica_vazia()
    {
        var vm = FpsGameVm.From(new("Counter-Strike 2", "cs2.exe", null, null, null, null));
        Assert.Equal("", vm.LastText);
        Assert.Equal("", vm.AvgText);
        Assert.Equal("", vm.PairText);
    }
}
