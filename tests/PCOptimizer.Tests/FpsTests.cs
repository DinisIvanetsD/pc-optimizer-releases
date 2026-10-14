using PCOptimizer.Core.Benchmark;
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

    [Fact]
    public void Estatisticas_de_frames_conhecidos()
    {
        var ms = Enumerable.Repeat(10.0, 980).Concat(Enumerable.Repeat(40.0, 20)).ToList();
        var s = FrameStats.From(ms)!;
        Assert.Equal(1000, s.Frames);
        Assert.Equal(10.6, s.Seconds, 3);
        Assert.Equal(94.34, s.AvgFps, 2);   // 1000 frames / 10,6 s
        Assert.Equal(25, s.Low1Fps, 3);     // percentil 99 dos tempos = 40 ms
        Assert.Equal(25, s.Low01Fps, 3);
        Assert.Equal(20, s.Stutters);       // 40 ms > 2,5 × mediana (10 ms)
    }

    [Fact]
    public void Sem_frames_nao_ha_estatisticas() => Assert.Null(FrameStats.From(Array.Empty<double>()));

    [Fact]
    public void Le_a_captura_real_do_PresentMon()
    {
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "presentmon-2.6.0.csv"));
        var reader = lines.Select(FrameCsvReader.TryCreate).First(r => r != null)!;
        var frames = lines.Select(reader.ReadFrameMs).OfType<double>().ToList();
        Assert.True(frames.Count >= 20);
        Assert.All(frames, ms => Assert.InRange(ms, 0.1, 1000));
    }

    [Theory]
    [InlineData("Application,ProcessID,FrameTime,DisplayedTime", "game.exe,10,16.7,NA", 16.7)]
    [InlineData("Application,ProcessID,MsBetweenPresents", "game.exe,10,8.3", 8.3)]
    [InlineData("Application,ProcessID,FrameTime", "game.exe,10,NA", null)]
    [InlineData("Application,ProcessID,FrameTime", "game.exe,10", null)]          // linha cortada
    [InlineData("Application,ProcessID,FrameTime", "game.exe,10,0", null)]        // tempo inválido
    public void Le_as_duas_versoes_do_cabecalho(string header, string line, double? expected) =>
        Assert.Equal(expected, FrameCsvReader.TryCreate(header)!.ReadFrameMs(line));

    [Fact]
    public void Cabecalho_sem_coluna_de_tempo_nao_serve() => Assert.Null(FrameCsvReader.TryCreate("Application,ProcessID"));

    [Fact]
    public void Janela_guarda_so_os_ultimos_segundos()
    {
        var w = new FrameWindow(seconds: 1);
        for (var i = 0; i < 200; i++) w.Add(10);       // 2 s de frames
        Assert.Equal(100, w.Stats()!.Frames);           // fica só o último segundo
    }

    [Fact]
    public void Argumentos_do_PresentMon() =>
        Assert.Equal("--process_id 4242 --output_stdout --no_console_stats --session_name Rigwise --stop_existing_session --terminate_on_proc_exit",
            FrameCapture.Arguments(4242));

    [Fact]
    public void Descartar_sem_ter_comecado_ou_duas_vezes_nao_rebenta()
    {
        var c = new FrameCapture(pid: 1);
        c.Dispose();
        c.Dispose();
    }

    [Fact]
    public void Codigo_6_do_PresentMon_e_acesso_negado()
    {
        var msg = FrameCapture.ExitMessage(6, null);
        Assert.Contains("Acesso negado", msg);
        Assert.Contains("Performance Log Users", msg);
    }

    [Theory]
    [InlineData(0, null, "O PresentMon terminou (código 0).")]
    [InlineData(1, "  erro qualquer ", "O PresentMon terminou (código 1): erro qualquer")]
    public void Outros_codigos_levam_o_codigo_e_a_primeira_linha_do_stderr(int code, string? stderr, string expected) =>
        Assert.Equal(expected, FrameCapture.ExitMessage(code, stderr));
}
