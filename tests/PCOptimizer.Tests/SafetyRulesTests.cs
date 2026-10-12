using PCOptimizer.App;
using PCOptimizer.Core.Storage;
using Xunit;

namespace PCOptimizer.Tests;

/// <summary>
/// As regras que impedem a app de apagar coisas importantes. Se algum destes testes falhar,
/// a app pode deixar o utilizador apagar o Windows ou programas instalados.
/// </summary>
public class SafetyRulesTests
{
    static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static readonly string Root = Path.GetPathRoot(Win)!;
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static readonly string Pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    static DirNode Node(string path, long size = 100 << 20)
    {
        // constrói a cadeia de pais (o conselheiro olha para os pais)
        DirNode? parent = null;
        var parts = path.TrimEnd('\\').Split('\\');
        var acc = parts[0] + "\\";
        parent = new DirNode { Name = acc, FullPath = acc, Size = size };
        foreach (var p in parts.Skip(1))
        {
            acc = Path.Combine(acc, p);
            var n = new DirNode { Name = p, FullPath = acc, Parent = parent, Size = size };
            parent.Children.Add(n);
            parent = n;
        }
        return parent;
    }

    static FolderAdvice Advise(string path, params InstalledApp[] apps) => new FolderAdvisor(apps).Advise(Node(path));

    [Theory]
    [InlineData("Windows")]
    [InlineData(@"Windows\System32")]
    [InlineData(@"Windows\WinSxS")]
    [InlineData("Program Files")]
    [InlineData("Program Files (x86)")]
    public void Windows_e_programas_nunca_sao_apagaveis(string rel)
    {
        var a = Advise(Path.Combine(Root, rel));
        Assert.Equal(FolderSafety.Danger, a.Safety);
        Assert.False(a.CanRecycle);
    }

    [Fact]
    public void Pasta_de_um_programa_instalado_e_Danger()
    {
        var dir = Path.Combine(Pf, "MeuJogo");
        var app = new InstalledApp { Name = "MeuJogo", Source = "Programa", InstallLocation = dir };
        Assert.Equal(FolderSafety.Danger, Advise(dir, app).Safety);
    }

    [Theory]
    [InlineData("Programs")]
    [InlineData("wsl")]
    public void AppData_Local_criticos_sao_Danger(string name)
    {
        Assert.Equal(FolderSafety.Danger, Advise(Path.Combine(Local, name)).Safety);
    }

    [Fact]
    public void Temp_do_utilizador_e_seguro()
    {
        var a = Advise(Path.Combine(Local, "Temp"));
        Assert.Equal(FolderSafety.Safe, a.Safety);
        Assert.True(a.CanRecycle);
    }

    [Fact]
    public void Cache_dentro_de_uma_app_e_segura()
    {
        Assert.True(Advise(Path.Combine(Local, "Google", "Chrome", "User Data", "Default", "Cache")).CanRecycle);
    }

    [Fact]
    public void Pasta_desconhecida_nunca_e_dada_como_segura()
    {
        // um fabricante que a app não conhece: tem de pedir cuidado, nunca "seguro"
        var a = Advise(Path.Combine(Roaming, "FabricanteInventadoXYZ"));
        Assert.False(a.CanRecycle);
    }

    [Fact]
    public void LocalCache_com_dados_reais_de_app_pede_cuidado()
    {
        // Packages\<app>\LocalCache\Roaming = dados de apps da Loja (ex.: Claude), não é cache
        var path = Path.Combine(Local, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache");
        var node = Node(path);
        node.Children.Add(new DirNode { Name = "Roaming", FullPath = Path.Combine(path, "Roaming"), Parent = node, Size = 1 });
        var a = new FolderAdvisor(Array.Empty<InstalledApp>()).Advise(node);
        Assert.False(a.CanRecycle);
    }

    [Fact]
    public void LocalCache_sem_dados_de_app_e_cache()
    {
        // mesma app, mas a LocalCache só tem cache: não pode depender do que está instalado no PC
        var path = Path.Combine(Local, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache");
        var node = Node(path);
        node.Children.Add(new DirNode { Name = "Packages", FullPath = Path.Combine(path, "Packages"), Parent = node, Size = 1 });
        var a = new FolderAdvisor(Array.Empty<InstalledApp>()).Advise(node);
        Assert.Equal(FolderSafety.Safe, a.Safety);
    }

    // ---- quantas confirmações a interface pede

    [Theory]
    [InlineData(FolderSafety.Danger, DeleteTier.Blocked)]
    [InlineData(FolderSafety.Caution, DeleteTier.Triple)]
    [InlineData(FolderSafety.Unknown, DeleteTier.Triple)]
    [InlineData(FolderSafety.Likely, DeleteTier.Double)]
    [InlineData(FolderSafety.Safe, DeleteTier.Normal)]
    public void Pastas_pedem_confirmacoes_conforme_o_risco(FolderSafety safety, DeleteTier expected)
    {
        var vm = new DirVm(Node(Path.Combine(Local, "x")), 1, new FolderAdvice(safety, "", ""), 0);
        Assert.Equal(expected, vm.Tier);
        if (expected == DeleteTier.Blocked)
        {
            vm.IsSelected = true;
            Assert.False(vm.IsSelected); // nem deixa selecionar
            Assert.False(vm.CanDelete);
        }
    }

    [Fact]
    public void Programas_essenciais_nao_se_podem_selecionar()
    {
        var app = new InstalledApp { Name = "Microsoft Visual C++ 2015-2022 Redistributable (x64)", Source = "Programa", UninstallCommand = "x" };
        AppInventory.Reclassify(app);
        var vm = new AppVm(app, new Dictionary<string, string>());
        Assert.Equal(DeleteTier.Blocked, vm.Tier);
        vm.IsSelected = true;
        Assert.False(vm.IsSelected);
    }

    [Fact]
    public void Programa_usado_esta_semana_pede_tres_confirmacoes()
    {
        var app = new InstalledApp { Name = "Editor de Fotos", Source = "Programa", UninstallCommand = "x", LastUsed = DateTime.Now.AddDays(-2) };
        AppInventory.Reclassify(app);
        Assert.Equal(DeleteTier.Triple, new AppVm(app, new Dictionary<string, string>()).Tier);
    }
}
