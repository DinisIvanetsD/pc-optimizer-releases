using System.Text.RegularExpressions;
using PCOptimizer.Core.Util;
using Xunit;

namespace PCOptimizer.Tests;

[CollectionDefinition("loc", DisableParallelization = true)]
public class LocCollection { }

[Collection("loc")] // mexe na língua global: corre sozinho, sem outros testes ao mesmo tempo
public class LocTests : IDisposable
{
    readonly string _before = Loc.Language;
    public void Dispose() => Loc.Language = _before;

    // o número e o alinhamento de cada valor têm de ser iguais; o formato pode ter texto traduzido ('às' → 'at')
    static IEnumerable<string> Holes(string s) =>
        Regex.Matches(s.Replace("{{", "").Replace("}}", ""), @"\{(\d+)(,-?\d+)?(:[^}]*)?\}").Select(m => m.Groups[1].Value + m.Groups[2].Value).OrderBy(x => x);

    [Fact]
    public void Cada_traducao_tem_os_mesmos_valores_que_o_original()
    {
        var bad = Loc.EnglishTable.Where(kv => !Holes(kv.Key).SequenceEqual(Holes(kv.Value))).Select(kv => kv.Key).ToList();
        Assert.True(bad.Count == 0, "Traduções com {n} diferentes:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Nenhuma_traducao_esta_vazia() =>
        Assert.DoesNotContain(Loc.EnglishTable, kv => string.IsNullOrWhiteSpace(kv.Value));

    [Fact]
    public void Modelo_do_texto_com_valores()
    {
        Loc.Language = "pt";
        var n = 1234.5;
        Assert.Equal($"Tens {n:N0} ficheiros em {"C:"}", Loc.T($"Tens {n:N0} ficheiros em {"C:"}"));
    }

    [Fact]
    public void Em_ingles_usa_a_traducao_e_sem_ela_fica_o_original()
    {
        Loc.Language = "en";
        var first = Loc.EnglishTable.FirstOrDefault(kv => !kv.Key.Contains('{'));
        if (first.Key != null) Assert.Equal(first.Value, Loc.T(first.Key));
        Assert.Equal("texto sem tradução nenhuma 123", Loc.T("texto sem tradução nenhuma 123"));
    }
}
