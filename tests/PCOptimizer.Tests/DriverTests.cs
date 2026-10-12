using PCOptimizer.Core.Drivers;
using Xunit;

namespace PCOptimizer.Tests;

public class DriverTests
{
    [Theory]
    [InlineData("32.0.16.1656", 616.56)]
    [InlineData("32.0.15.6094", 560.94)]
    [InlineData("31.0.15.5222", 552.22)]
    public void Versao_do_Windows_converte_para_versao_NVIDIA(string windows, double nvidia)
    {
        Assert.Equal((decimal)nvidia, DriverDownloads.NvidiaVersion(windows));
    }

    [Fact]
    public void Versao_invalida_nao_rebenta()
    {
        Assert.Null(DriverDownloads.NvidiaVersion("abc"));
    }

    [Fact]
    public void Pesquisa_de_dispositivo_desconhecido_usa_so_VEN_e_DEV()
    {
        var d = new ProblemDevice("Desconhecido", @"PCI\VEN_10EC&DEV_8125&SUBSYS_E0001458&REV_05\01", "Net",
            @"PCI\VEN_10EC&DEV_8125&SUBSYS_E0001458&REV_05", 28, DeviceProblemKind.Missing, "");
        var url = DriverManager.SearchUrl(d);
        Assert.Contains(Uri.EscapeDataString("VEN_10EC&DEV_8125"), url);
        Assert.DoesNotContain("SUBSYS", url);
    }
}
