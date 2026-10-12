using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Memory;

public enum StabilityVerdict
{
    /// <summary>Sem erros.</summary>
    Stable,
    /// <summary>Erros sempre nas mesmas páginas físicas: dá para as isolar no Windows.</summary>
    Localized,
    /// <summary>Erros espalhados/a mudar de sítio: instabilidade (XMP, tensão, temperatura). Não dá para isolar.</summary>
    Unstable,
}

public sealed record PhysicalTestResult(long BytesTested, int PassesCompleted, long Errors, TimeSpan Elapsed, bool Cancelled,
    IReadOnlyDictionary<ulong, int> BadPages, StabilityVerdict Verdict)
{
    /// <summary>Total de memória que seria isolada (páginas de 4 KB).</summary>
    public long BadBytes => BadPages.Count * 4096L;
}

/// <summary>
/// Testa a RAM pela sua posição FÍSICA. Usa o AWE do Windows (AllocateUserPhysicalPages), que
/// devolve o número de cada página física (PFN) que nos é dada. Assim, quando há um erro, sabemos
/// exatamente que página da RAM está avariada e podemos dizer ao Windows para nunca mais a usar
/// (lista "badmemory" do arranque).
///
/// Requisitos: administrador e o direito "Bloquear páginas na memória" (SeLockMemoryPrivilege),
/// que a app pode atribuir (é preciso terminar sessão depois).
/// </summary>
public sealed unsafe class PhysicalRamTester
{
    const int PageSize = 4096;
    const int ChunkPages = 16384; // 64 MB mapeados de cada vez por cada núcleo
    const int MaxBadPagesToIsolate = 512; // 2 MB: mais do que isto já não é "um chip com defeito"

    readonly object _lock = new();
    readonly Dictionary<ulong, HashSet<int>> _badPages = new(); // PFN → passagens em que falhou
    long _errors;
    long _bytesProcessed;
    volatile string _currentTest = Loc.T("A reservar memória física");

    public event Action<RamTestProgress>? Progress;

    // ---------------- Privilégio ----------------

    /// <summary>true se este processo tem (e conseguiu ativar) o direito de bloquear páginas na memória.</summary>
    public static bool TryEnableLockPagesPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, "SeLockMemoryPrivilege", out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() != ErrorNotAllAssigned;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// Dá ao utilizador atual o direito "Bloquear páginas na memória" (o mesmo que se faz em
    /// secpol.msc). Só tem efeito depois de terminar sessão e voltar a entrar.
    /// </summary>
    public static void GrantLockPagesRight()
    {
        Simulation.Guard(Loc.T("dar a permissão \"Bloquear páginas na memória\" à tua conta"));
        Util.Admin.Require();
        var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException(Loc.T("Não foi possível identificar o utilizador."));
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);

        var attrs = new LSA_OBJECT_ATTRIBUTES();
        var status = LsaOpenPolicy(IntPtr.Zero, ref attrs, PolicyCreateAccount | PolicyLookupNames, out var policy);
        if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
        try
        {
            const string right = "SeLockMemoryPrivilege";
            fixed (char* r = right)
            fixed (byte* s = sidBytes)
            {
                var str = new LSA_UNICODE_STRING
                {
                    Length = (ushort)(right.Length * 2),
                    MaximumLength = (ushort)((right.Length + 1) * 2),
                    Buffer = (IntPtr)r,
                };
                status = LsaAddAccountRights(policy, (IntPtr)s, ref str, 1);
                if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
            }
        }
        finally
        {
            LsaClose(policy);
        }
    }

    // ---------------- Teste ----------------

    public Task<PhysicalTestResult> RunAsync(long bytes, int passes, int threads, CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(bytes, passes, threads, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    PhysicalTestResult Run(long bytes, int passes, int threads, CancellationToken ct)
    {
        if (!TryEnableLockPagesPrivilege())
            throw new InvalidOperationException(Loc.T("Falta o acesso à memória física. Carrega em \"Ativar acesso à memória física\" e termina sessão."));

        var sw = Stopwatch.StartNew();
        var process = GetCurrentProcess();
        var previousState = SetThreadExecutionState(EsContinuous | EsSystemRequired);
        var pages = new List<nuint[]>(); // pedimos em lotes para aceitar o que houver disponível
        long totalPages = 0;
        var passesDone = 0;
        var cancelled = false;

        try
        {
            // ---- reservar páginas físicas (em lotes de 256 MB) ----
            var wanted = bytes / PageSize;
            while (totalPages < wanted)
            {
                ct.ThrowIfCancellationRequested();
                var batch = (nuint)Math.Min(65536, wanted - totalPages);
                var arr = new nuint[batch];
                var got = batch;
                fixed (nuint* a = arr)
                {
                    if (!AllocateUserPhysicalPages(process, ref got, a) || got == 0) break;
                }
                if (got < batch) Array.Resize(ref arr, (int)got);
                pages.Add(arr);
                totalPages += (long)got;
                if (got < batch) break;
            }
            if (totalPages == 0) throw new OutOfMemoryException(Loc.T("O Windows não deu memória física para testar. Fecha apps e tenta outra vez."));

            // lista plana de "pedaços" de até 64 MB
            var chunks = new List<(nuint[] Arr, int Start, int Count)>();
            foreach (var arr in pages)
                for (var s = 0; s < arr.Length; s += ChunkPages)
                    chunks.Add((arr, s, Math.Min(ChunkPages, arr.Length - s)));

            var testedBytes = totalPages * PageSize;
            var perPass = (double)testedBytes * RamPatterns.SweepsPerPass;
            using var timer = new Timer(_ =>
            {
                var done = Interlocked.Read(ref _bytesProcessed);
                var pass = (int)(done / perPass);
                Progress?.Invoke(new RamTestProgress(Math.Min(pass + 1, passes), passes, _currentTest,
                    100.0 * (done - pass * perPass) / perPass, done / 1e9 / Math.Max(sw.Elapsed.TotalSeconds, 0.001),
                    Interlocked.Read(ref _errors), sw.Elapsed, testedBytes, true));
            }, null, 0, 500);

            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = ct };
            for (var pass = 1; pass <= passes; pass++)
            {
                _currentTest = Loc.T("Memória física: todos os padrões");
                var p = pass;
                Parallel.For(0, chunks.Count, options,
                    // cada núcleo tem a sua "janela" de 64 MB onde mapeia as páginas físicas
                    () => VirtualAlloc(IntPtr.Zero, (nuint)(ChunkPages * PageSize), MemReserve | MemPhysical, PageReadWrite),
                    (i, _, window) =>
                    {
                        if (window == IntPtr.Zero) throw new OutOfMemoryException(Loc.T("Não foi possível reservar a janela de memória."));
                        var (arr, start, count) = chunks[i];
                        fixed (nuint* a = &arr[start])
                        {
                            if (!MapUserPhysicalPages(window, (nuint)count, a))
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                        }
                        var sink = new ChunkSink(this, arr, start, p);
                        foreach (var (_, _, run) in RamPatterns.All)
                            run((ulong*)window, count * (PageSize / 8), (ulong)(i * 1000 + p), sink);
                        MapUserPhysicalPages(window, (nuint)count, null);
                        return window;
                    },
                    window => { if (window != IntPtr.Zero) VirtualFree(window, 0, MemRelease); });
                passesDone = pass;
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            foreach (var arr in pages)
            {
                var n = (nuint)arr.Length;
                fixed (nuint* a = arr) FreeUserPhysicalPages(process, ref n, a);
            }
            SetThreadExecutionState(previousState == 0 ? EsContinuous : previousState);
        }

        lock (_lock)
        {
            var bad = _badPages.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
            return new PhysicalTestResult(totalPages * PageSize, passesDone, _errors, sw.Elapsed, cancelled, bad,
                Classify(passesDone));
        }
    }

    StabilityVerdict Classify(int passesDone)
    {
        if (_badPages.Count == 0) return StabilityVerdict.Stable;
        if (_badPages.Count > MaxBadPagesToIsolate) return StabilityVerdict.Unstable;
        if (passesDone < 2) return StabilityVerdict.Localized;
        // um defeito físico falha sempre no mesmo sítio; instabilidade falha em sítios diferentes de cada vez
        var repeated = _badPages.Count(kv => kv.Value.Count >= 2);
        return repeated >= _badPages.Count * 0.7 ? StabilityVerdict.Localized : StabilityVerdict.Unstable;
    }

    sealed class ChunkSink(PhysicalRamTester owner, nuint[] arr, int start, int pass) : IRamSink
    {
        public void Done(long words) => Interlocked.Add(ref owner._bytesProcessed, words * 8);

        public void Report(string test, long index, ulong expected, ulong actual)
        {
            Interlocked.Increment(ref owner._errors);
            var pfn = (ulong)arr[start + (int)(index * 8 / PageSize)];
            lock (owner._lock)
            {
                if (!owner._badPages.TryGetValue(pfn, out var set)) owner._badPages[pfn] = set = new HashSet<int>();
                set.Add(pass);
            }
        }
    }

    // ---------------- Win32 ----------------

    const uint MemReserve = 0x2000, MemRelease = 0x8000, MemPhysical = 0x400000, PageReadWrite = 0x04;
    const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;
    const uint TokenAdjustPrivileges = 0x20, TokenQuery = 0x8, SePrivilegeEnabled = 0x2;
    const int ErrorNotAllAssigned = 1300;
    const uint PolicyCreateAccount = 0x10, PolicyLookupNames = 0x800;

    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    struct LSA_UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    struct LSA_OBJECT_ATTRIBUTES
    {
        public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes;
        public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AllocateUserPhysicalPages(IntPtr process, ref nuint numberOfPages, nuint* pageArray);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeUserPhysicalPages(IntPtr process, ref nuint numberOfPages, nuint* pageArray);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool MapUserPhysicalPages(IntPtr virtualAddress, nuint numberOfPages, nuint* pageArray);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAlloc(IntPtr address, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualFree(IntPtr address, nuint size, uint type);
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, uint len, IntPtr prev, IntPtr retLen);
    [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr systemName, ref LSA_OBJECT_ATTRIBUTES attrs, uint access, out IntPtr policy);
    [DllImport("advapi32.dll")] static extern uint LsaAddAccountRights(IntPtr policy, IntPtr sid, ref LSA_UNICODE_STRING rights, uint count);
    [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr policy);
    [DllImport("advapi32.dll")] static extern int LsaNtStatusToWinError(uint status);
}
