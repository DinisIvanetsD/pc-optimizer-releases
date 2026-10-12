using System.Diagnostics;
using System.Runtime.InteropServices;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Memory;

public sealed record RamTestOptions(long BytesToTest, int Passes, int Threads)
{
    /// <summary>0 = repetir até o utilizador parar.</summary>
    public bool Infinite => Passes <= 0;
}

public sealed record RamTestError(int Pass, string Test, int Block, long Offset, ulong Expected, ulong Actual)
{
    public ulong FlippedBits => Expected ^ Actual;
    public override string ToString() =>
        $"Passagem {Pass} · {Test} · bloco {Block} + 0x{Offset:X}: esperado {Expected:X16}, lido {Actual:X16} (bits {FlippedBits:X16})";
}

public sealed record RamTestProgress(int Pass, int TotalPasses, string Test, double PassPercent, double GbPerSecond,
    long Errors, TimeSpan Elapsed, long BytesTested, bool AllLocked);

public sealed record RamTestResult(int PassesCompleted, long Errors, long BytesTested, TimeSpan Elapsed,
    bool Cancelled, bool AllLocked, IReadOnlyList<RamTestError> FirstErrors, IReadOnlyDictionary<int, long> BitHistogram)
{
    public bool Passed => Errors == 0;
}

/// <summary>
/// Teste de RAM dentro do Windows (sem reiniciar), ao estilo do TestMem5 / HCI MemTest.
///
/// Reserva uma grande parte da RAM livre, prende-a na memória física (VirtualLock, para o Windows
/// não a passar para o disco) e corre vários padrões de escrita/leitura em todos os núcleos:
///  - Endereço próprio: cada posição guarda o seu endereço (apanha erros de endereçamento)
///  - Inversões móveis com padrões fixos (0x00, 0xFF, 0xAA, 0x55)
///  - Bits a andar: um bit 1 que muda de posição
///  - Aleatório: sequência pseudo-aleatória escrita e depois regenerada para comparar
///
/// Limitação honesta: só testa a RAM que está livre. A que o Windows e as apps usam fica de fora.
/// Para cobrir 100% é preciso um teste com reinício (Diagnóstico de Memória do Windows ou MemTest86).
/// </summary>
public sealed unsafe class RamTester
{
    const long BlockSize = 128L << 20; // 128 MB
    const int MaxStoredErrors = 500;

    readonly object _errLock = new();
    readonly List<RamTestError> _errors = new();
    readonly long[] _bitHistogram = new long[64];
    long _errorCount;
    long _bytesProcessed;

    public event Action<RamTestProgress>? Progress;
    public event Action<RamTestError>? ErrorFound;

    /// <summary>Quanto testar por defeito: a RAM livre menos uma margem para o Windows não ficar sem memória.</summary>
    public static long SuggestedBytes()
    {
        var mem = SystemInfo.GetMemory();
        var margin = Math.Max(1536L << 20, (long)(mem.TotalBytes * 0.08));
        var bytes = (long)mem.AvailableBytes - margin;
        bytes = bytes / BlockSize * BlockSize;
        return Math.Max(BlockSize * 2, bytes);
    }

    public Task<RamTestResult> RunAsync(RamTestOptions options, CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(options, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    RamTestResult Run(RamTestOptions o, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var blocks = new List<IntPtr>();
        var allLocked = true;
        var previousState = SetThreadExecutionState(EsContinuous | EsSystemRequired); // não deixar o PC suspender
        var passesDone = 0;
        var cancelled = false;

        try
        {
            // ---- reservar e prender a memória ----
            var count = (int)(o.BytesToTest / BlockSize);
            var process = GetCurrentProcess();
            var wsMin = (nuint)(count * BlockSize + (64L << 20));
            var wsMax = (nuint)(count * BlockSize + (512L << 20));
            if (!SetProcessWorkingSetSizeEx(process, wsMin, wsMax, 0)) allLocked = false;

            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var p = VirtualAlloc(IntPtr.Zero, (nuint)BlockSize, MemCommit | MemReserve, PageReadWrite);
                if (p == IntPtr.Zero) break; // sem mais memória disponível: testamos o que conseguimos
                if (!VirtualLock(p, (nuint)BlockSize)) allLocked = false;
                blocks.Add(p);
            }
            if (blocks.Count == 0) throw new OutOfMemoryException(Loc.T("Não foi possível reservar memória para testar. Fecha algumas apps e tenta outra vez."));

            var testedBytes = blocks.Count * BlockSize;
            var sweepsPerPass = RamPatterns.SweepsPerPass;

            using var timer = new Timer(_ =>
            {
                var done = Interlocked.Read(ref _bytesProcessed);
                var perPass = (double)testedBytes * sweepsPerPass;
                var pass = (int)(done / perPass);
                Progress?.Invoke(new RamTestProgress(
                    Math.Min(pass + 1, o.Infinite ? int.MaxValue : o.Passes), o.Passes, _currentTest,
                    100.0 * (done - pass * perPass) / perPass,
                    done / 1e9 / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
                    Interlocked.Read(ref _errorCount), sw.Elapsed, testedBytes, allLocked));
            }, null, 0, 500);

            var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.Threads), CancellationToken = ct };
            for (var pass = 1; o.Infinite || pass <= o.Passes; pass++)
            {
                foreach (var (name, _, run) in RamPatterns.All)
                {
                    _currentTest = name;
                    var p = pass;
                    Parallel.For(0, blocks.Count, parallel, b =>
                        run((ulong*)blocks[b], BlockSize / 8, (ulong)(b * 1000 + p), new BlockSink(this, b, p)));
                }
                passesDone = pass;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            foreach (var p in blocks)
            {
                VirtualUnlock(p, (nuint)BlockSize);
                VirtualFree(p, 0, MemRelease);
            }
            SetProcessWorkingSetSizeEx(GetCurrentProcess(), unchecked((nuint)(-1)), unchecked((nuint)(-1)), 0);
            SetThreadExecutionState(previousState == 0 ? EsContinuous : previousState);
        }

        var histogram = new Dictionary<int, long>();
        for (var i = 0; i < 64; i++)
            if (_bitHistogram[i] > 0) histogram[i] = _bitHistogram[i];
        lock (_errLock)
        {
            return new RamTestResult(passesDone, _errorCount, blocks.Count * BlockSize, sw.Elapsed, cancelled,
                allLocked, _errors.ToList(), histogram);
        }
    }

    volatile string _currentTest = "A preparar";

    /// <summary>Liga os padrões partilhados (RamPatterns) a um bloco e passagem concretos.</summary>
    sealed class BlockSink(RamTester owner, int block, int pass) : IRamSink
    {
        public void Done(long words) => owner.Done(words);
        public void Report(string test, long index, ulong expected, ulong actual) =>
            owner.Report(pass, test, block, index, expected, actual);
    }

    void Done(long words) => Interlocked.Add(ref _bytesProcessed, words * 8);

    void Report(int pass, string test, int block, long index, ulong expected, ulong actual)
    {
        Interlocked.Increment(ref _errorCount);
        var flipped = expected ^ actual;
        var e = new RamTestError(pass, test, block, index * 8, expected, actual);
        lock (_errLock)
        {
            for (var b = 0; b < 64; b++)
                if ((flipped >> b & 1) != 0) _bitHistogram[b]++;
            if (_errors.Count >= MaxStoredErrors) return;
            _errors.Add(e);
        }
        ErrorFound?.Invoke(e);
    }

    // ================= Win32 =================

    const uint MemCommit = 0x1000, MemReserve = 0x2000, MemRelease = 0x8000, PageReadWrite = 0x04;
    const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAlloc(IntPtr lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualFree(IntPtr lpAddress, nuint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualLock(IntPtr lpAddress, nuint dwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualUnlock(IntPtr lpAddress, nuint dwSize);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessWorkingSetSizeEx(IntPtr hProcess, nuint min, nuint max, uint flags);

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint esFlags);
}
