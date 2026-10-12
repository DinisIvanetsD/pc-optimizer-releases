using PCOptimizer.Core.History;
using PCOptimizer.Core.Storage;
using Xunit;

namespace PCOptimizer.Tests;

/// <summary>
/// Testa a sério o "Desfazer" de pastas apagadas: cria ficheiros de teste, envia-os para a Reciclagem
/// e confirma que voltam ao sítio. Usa um histórico à parte (não mexe no verdadeiro).
/// </summary>
[Collection("history")]
public class HistoryTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pcopt-test-" + Guid.NewGuid().ToString("N")[..8]);

    public HistoryTests()
    {
        Environment.SetEnvironmentVariable("PCOPT_HISTORY_FILE", Path.Combine(Path.GetTempPath(), "pcopt-history-test.json"));
        Directory.CreateDirectory(Path.Combine(_dir, "lixo", "sub"));
        File.WriteAllText(Path.Combine(_dir, "lixo", "a.txt"), "A");
        File.WriteAllText(Path.Combine(_dir, "lixo", "sub", "b.txt"), "B");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task Pasta_enviada_para_a_Reciclagem_volta_com_Desfazer()
    {
        var folder = Path.Combine(_dir, "lixo");
        var (_, items, failed) = FolderAdvisor.RecycleContents(folder);
        Assert.Equal(2, items); // a.txt e a pasta sub
        Assert.Equal(0, failed);
        Assert.False(File.Exists(Path.Combine(folder, "a.txt")));

        var entry = ChangeLog.All.First(e => e.Undo == UndoKind.RecycleBin && e.Data["folder"] == folder);
        Assert.True(entry.CanUndo);

        var msg = await ChangeLog.UndoAsync(entry);
        Assert.StartsWith("✓", msg);
        Assert.Equal("A", File.ReadAllText(Path.Combine(folder, "a.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(folder, "sub", "b.txt")));
        Assert.True(ChangeLog.All.First(e => e.Id == entry.Id).Undone);
    }

    [Fact]
    public void Registo_sem_desfazer_fica_marcado_como_tal()
    {
        var e = ChangeLog.Record(ChangeKind.Uninstall, "Desinstalado: Teste " + Guid.NewGuid());
        Assert.False(e.CanUndo);
        Assert.Contains(ChangeLog.All, x => x.Id == e.Id);
    }
}
