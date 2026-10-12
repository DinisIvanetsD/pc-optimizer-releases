using PCOptimizer.Core.Util;
namespace PCOptimizer.Core.Memory;

/// <summary>Quem corre um padrão recebe o progresso e os erros por aqui.</summary>
internal interface IRamSink
{
    void Done(long words);
    void Report(string test, long index, ulong expected, ulong actual);
}

internal unsafe delegate void RamPattern(ulong* p, long n, ulong seed, IRamSink sink);

/// <summary>
/// Os padrões de teste de memória, partilhados pelo teste normal (RamTester) e pelo teste de
/// memória física do estabilizador (PhysicalRamTester). Cada um escreve um bloco inteiro e depois
/// lê-o, comparando com o esperado; `seed` muda entre blocos e passagens.
/// </summary>
internal static unsafe class RamPatterns
{
    public static readonly (string Name, int Sweeps, RamPattern Run)[] All =
    {
        (Loc.T("Endereço próprio"), 2, OwnAddress),
        (Loc.T("Padrões fixos (inversões móveis)"), 12, MovingInversions),
        ("Bits a andar", 2, WalkingBits),
        (Loc.T("Aleatório"), 2, RandomPattern),
    };

    public static int SweepsPerPass => All.Sum(t => t.Sweeps);

    /// <summary>Cada posição guarda o seu próprio endereço: apanha erros de endereçamento.</summary>
    static void OwnAddress(ulong* p, long n, ulong seed, IRamSink sink)
    {
        var salt = seed * 0x9E3779B97F4A7C15UL;
        for (long i = 0; i < n; i++) p[i] = (ulong)(p + i) ^ salt;
        sink.Done(n);
        for (long i = 0; i < n; i++)
        {
            var expected = (ulong)(p + i) ^ salt;
            if (p[i] != expected) sink.Report(Loc.T("Endereço próprio"), i, expected, p[i]);
        }
        sink.Done(n);
    }

    /// <summary>Inversões móveis (como no MemTest86) com 0x00, 0xAA e 0x55.</summary>
    static void MovingInversions(ulong* p, long n, ulong seed, IRamSink sink)
    {
        foreach (var pattern in new[] { 0UL, 0xAAAAAAAAAAAAAAAAUL, 0x5555555555555555UL })
        {
            var inv = ~pattern;
            for (long i = 0; i < n; i++) p[i] = pattern;
            sink.Done(n);
            // para cima: verifica o padrão e escreve o inverso
            for (long i = 0; i < n; i++)
            {
                if (p[i] != pattern) sink.Report(Loc.T("Padrões fixos"), i, pattern, p[i]);
                p[i] = inv;
            }
            sink.Done(n);
            // para baixo: verifica o inverso e volta a escrever o padrão
            for (var i = n - 1; i >= 0; i--)
            {
                if (p[i] != inv) sink.Report(Loc.T("Padrões fixos"), i, inv, p[i]);
                p[i] = pattern;
            }
            sink.Done(n);
            for (long i = 0; i < n; i++)
            {
                if (p[i] != pattern) sink.Report(Loc.T("Padrões fixos"), i, pattern, p[i]);
            }
            sink.Done(n);
        }
    }

    /// <summary>Um bit 1 entre zeros (e um 0 entre uns) que muda de posição.</summary>
    static void WalkingBits(ulong* p, long n, ulong seed, IRamSink sink)
    {
        var shift = (long)(seed & 63);
        for (long i = 0; i < n; i++)
        {
            var v = 1UL << (int)((i + shift) & 63);
            p[i] = (i & 1) == 0 ? v : ~v;
        }
        sink.Done(n);
        for (long i = 0; i < n; i++)
        {
            var v = 1UL << (int)((i + shift) & 63);
            var expected = (i & 1) == 0 ? v : ~v;
            if (p[i] != expected) sink.Report("Bits a andar", i, expected, p[i]);
        }
        sink.Done(n);
    }

    /// <summary>Sequência pseudo-aleatória (xorshift) escrita e depois regenerada para comparar.</summary>
    static void RandomPattern(ulong* p, long n, ulong seed, IRamSink sink)
    {
        var start = (0x2545F4914F6CDD1DUL * (seed + 1)) | 1;
        var x = start;
        for (long i = 0; i < n; i++)
        {
            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            p[i] = x;
        }
        sink.Done(n);
        x = start;
        for (long i = 0; i < n; i++)
        {
            x ^= x << 13; x ^= x >> 7; x ^= x << 17;
            if (p[i] != x) sink.Report(Loc.T("Aleatório"), i, x, p[i]);
        }
        sink.Done(n);
    }
}
