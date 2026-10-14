using Xunit;

namespace PCOptimizer.Tests;

// Formato real do PresentMon 2.6.0 (captura em Fixtures/presentmon-2.6.0.csv: cs2.exe, DXGI, 30 frames):
// - Coluna do tempo entre frames: MsBetweenPresents (não existe FrameTime). Também há MsBetweenDisplayChange;
//   DisplayedTime não existe (há MsUntilDisplayed).
// - Linhas antes do cabeçalho: não. A 1.ª linha do stdout é o cabeçalho (28 colunas, separador ','), e as
//   linhas de dados têm o mesmo número de colunas. Avisos e erros vão para o stderr.
// - Valores: ponto decimal ('.', 14 casas), e 'NA' onde não há valor (por exemplo MsBetweenSimulationStart).
// - Aplicação: nome do exe com extensão na coluna Application; várias aplicações (dwm.exe, o jogo) vêm misturadas.
// - Pelo pipe (Process.StandardOutput) o texto é ASCII com CRLF e sem BOM. Redirecionado para um ficheiro por
//   'cmd /c ... > ficheiro' saiu UTF-16 LE com BOM: a fixture foi guardada em UTF-8 sem BOM (o stdout real).
// - Numa tentativa com --timed 3 e o stdout num pipe o processo não terminou sozinho (parou de escrever mas
//   ficou vivo): a app não deve depender de --timed para o terminar.
// - Sem administrador e sem estar no grupo "Performance Log Users", sai com código 6: "access denied".

public class FpsTests
{
    static string Tool(string name) => Path.Combine(AppContext.BaseDirectory, "tools", name);

    [Fact]
    public void PresentMon_incluido_e_o_oficial()
    {
        using var f = File.OpenRead(Tool("PresentMon-2.6.0-x64.exe"));
        Assert.Equal("b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f)).ToLowerInvariant());
        Assert.True(File.Exists(Tool("PresentMon-LICENSE.txt")));
    }
}
