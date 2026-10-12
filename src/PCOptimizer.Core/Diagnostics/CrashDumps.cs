using System.Diagnostics;
using System.Text;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Diagnostics;

/// <summary>Um ecrã azul lido de um minidump (C:\Windows\Minidump).</summary>
public sealed record CrashDump(
    string File, DateTime When, uint Code, ulong[] Parameters,
    string? CausedBy, string CausedByHow, IReadOnlyList<string> StackDrivers)
{
    public string CodeName => CrashDumps.CodeName(Code);
    public string CodeText => $"0x{CrashDumps.BaseCode(Code):X} {CodeName}";
}

/// <summary>Veredicto sobre o conjunto dos ecrãs azuis.</summary>
public sealed record CrashVerdict(string Title, string Explanation, bool PointsToRam, string? GuiltyDriver);

/// <summary>
/// Lê minidumps do kernel (formato PAGEDU64 / triage) sem precisar do WinDbg: código do erro, parâmetros,
/// drivers carregados e a pilha de chamadas — e descobre que driver estava a correr quando o PC crashou.
/// </summary>
public static class CrashDumps
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");

    // módulos do próprio Windows: aparecem em quase todas as pilhas e raramente são a causa
    static readonly HashSet<string> CoreModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "ntoskrnl.exe", "ntkrnlmp.exe", "ntkrnlpa.exe", "ntkrpamp.exe", "hal.dll", "halmacpi.dll", "kdcom.dll", "kd.dll",
        "ci.dll", "clfs.sys", "tm.sys", "pshed.dll", "werkernel.sys", "fltmgr.sys", "msrpc.sys", "ksecdd.sys", "cng.sys",
    };

    static readonly Dictionary<string, bool> MicrosoftCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Driver do próprio Windows (núcleo ou assinado como Microsoft)? Estes raramente são a causa: são as vítimas.</summary>
    public static bool IsWindowsModule(string module)
    {
        if (CoreModules.Contains(module)) return true;
        lock (MicrosoftCache)
        {
            if (MicrosoftCache.TryGetValue(module, out var cached)) return cached;
            var company = "";
            try
            {
                var path = Path.Combine(Environment.SystemDirectory, "drivers", module);
                if (!File.Exists(path)) path = Path.Combine(Environment.SystemDirectory, module);
                if (File.Exists(path)) company = FileVersionInfo.GetVersionInfo(path).CompanyName ?? "";
            }
            catch { }
            return MicrosoftCache[module] = company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>O Windows marca alguns códigos com 0x10000000 (ex.: 0x1000007E = 0x7E).</summary>
    public static uint BaseCode(uint code) => code & 0x0FFFFFFF;

    public static string CodeName(uint code) =>
        EventLogCheck.BugChecks.TryGetValue(BaseCode(code), out var i) ? i.Name : Loc.T("Erro desconhecido");

    public static bool CanRead()
    {
        try
        {
            var f = Directory.EnumerateFiles(Folder, "*.dmp").FirstOrDefault();
            if (f is null) return true;
            using var s = File.OpenRead(f);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Quantos minidumps há (desde <paramref name="since"/>). Ver a data do ficheiro não precisa de administrador.</summary>
    public static int Count(DateTime? since = null)
    {
        try { return Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.dmp").Count(f => since is not DateTime s || File.GetLastWriteTime(f) >= s) : 0; }
        catch { return 0; }
    }

    public static List<CrashDump> ReadAll(string? folder = null)
    {
        var list = new List<CrashDump>();
        folder ??= Folder;
        if (!Directory.Exists(folder)) return list;
        foreach (var f in Directory.EnumerateFiles(folder, "*.dmp"))
        {
            try
            {
                if (Read(f) is CrashDump d) list.Add(d);
            }
            catch (UnauthorizedAccessException) { throw; }
            catch { /* ficheiro corrompido ou formato antigo: ignora */ }
        }
        return list.OrderByDescending(d => d.When).ToList();
    }

    sealed record Module(string Name, ulong Base, uint Size)
    {
        public bool Contains(ulong addr) => addr >= Base && addr < Base + Size;
    }

    public static CrashDump? Read(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 0x2100 || Encoding.ASCII.GetString(b, 0, 8) != "PAGEDU64") return null;

        var code = BitConverter.ToUInt32(b, 0x38);
        var p = new[] { U64(b, 0x40), U64(b, 0x48), U64(b, 0x50), U64(b, 0x58) };
        var when = DateTime.FromFileTime((long)U64(b, 0xFA8));
        if (when.Year < 2000) when = File.GetLastWriteTime(path);

        var modules = new List<Module>();
        var stackDrivers = new List<string>();
        string? causedBy = null;
        var how = "";

        // só os minidumps "triage" (tipo 4) têm a lista de drivers e a pilha
        if (BitConverter.ToUInt32(b, 0xF98) == 4)
        {
            const int t = 0x2000;
            var callStackOffset = (int)U32(b, t + 0x28);
            var callStackSize = (int)U32(b, t + 0x2C);
            var driverListOffset = (int)U32(b, t + 0x30);
            var driverCount = (int)U32(b, t + 0x34);
            var stringPoolOffset = (int)U32(b, t + 0x38);

            if (driverCount > 0 && driverCount < 4096)
            {
                var entrySize = (stringPoolOffset - driverListOffset) / driverCount; // 0x90 no Windows 10/11
                for (var i = 0; i < driverCount; i++)
                {
                    var e = driverListOffset + i * entrySize;
                    if (e + 0x50 > b.Length) break;
                    var nameOffset = (int)U32(b, e);
                    var dllBase = U64(b, e + 0x38);
                    var size = U32(b, e + 0x48);
                    var name = ReadDumpString(b, nameOffset);
                    if (name != null && dllBase != 0) modules.Add(new Module(Path.GetFileName(name), dllBase, size));
                }
            }

            // 1) o endereço onde o erro aconteceu, quando o código o indica
            ulong? faultAddress = BaseCode(code) switch
            {
                0x7E or 0x8E or 0x1E => p[1],   // endereço da exceção
                0xD1 or 0x0A => p[3],           // endereço da instrução
                0x50 => p[2],                   // endereço da instrução que fez o acesso
                0x3B => p[1],
                _ => null,
            };
            if (faultAddress is ulong fa && modules.FirstOrDefault(m => m.Contains(fa)) is Module fm)
            {
                causedBy = fm.Name;
                how = Loc.T("endereço do erro");
            }

            // 2) a pilha de chamadas: que drivers estavam a correr
            if (callStackOffset > 0 && callStackSize > 0 && callStackOffset + callStackSize <= b.Length)
            {
                for (var o = callStackOffset; o + 8 <= callStackOffset + callStackSize; o += 8)
                {
                    var v = U64(b, o);
                    if (v < 0xFFFF800000000000) continue;
                    var m = modules.FirstOrDefault(x => x.Contains(v));
                    if (m != null && !stackDrivers.Contains(m.Name, StringComparer.OrdinalIgnoreCase)) stackDrivers.Add(m.Name);
                }
            }
            if (causedBy is null || IsWindowsModule(causedBy))
            {
                var third = stackDrivers.FirstOrDefault(d => !IsWindowsModule(d));
                if (third != null)
                {
                    causedBy = third;
                    how = Loc.T("primeiro driver na pilha");
                }
                else if (causedBy is null && stackDrivers.Count > 0)
                {
                    causedBy = stackDrivers[0];
                    how = Loc.T("só o Windows na pilha");
                }
            }
        }

        return new CrashDump(Path.GetFileName(path), when, code, p, causedBy, how, stackDrivers.Take(8).ToList());
    }

    static ulong U64(byte[] b, int o) => o + 8 <= b.Length ? BitConverter.ToUInt64(b, o) : 0;
    static uint U32(byte[] b, int o) => o + 4 <= b.Length ? BitConverter.ToUInt32(b, o) : 0;

    /// <summary>DUMP_STRING: ULONG comprimento (em caracteres) + texto UTF-16.</summary>
    static string? ReadDumpString(byte[] b, int o)
    {
        if (o <= 0 || o + 4 > b.Length) return null;
        var len = (int)U32(b, o);
        if (len <= 0 || len > 512 || o + 4 + len * 2 > b.Length) return null;
        return Encoding.Unicode.GetString(b, o + 4, len * 2);
    }

    // ------------------------------------------------------------------ explicar

    /// <summary>Nome legível do driver (ex.: nvlddmkm.sys → "NVIDIA Windows Kernel Mode Driver").</summary>
    public static string Describe(string module)
    {
        if (CoreModules.Contains(module)) return Loc.T("núcleo do Windows");
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "drivers", module);
            if (!File.Exists(path)) path = Path.Combine(Environment.SystemDirectory, module);
            if (!File.Exists(path)) return "";
            var v = FileVersionInfo.GetVersionInfo(path);
            var desc = string.IsNullOrWhiteSpace(v.FileDescription) ? "" : v.FileDescription.Trim();
            var company = string.IsNullOrWhiteSpace(v.CompanyName) ? "" : v.CompanyName.Trim();
            return string.Join(" · ", new[] { desc, company }.Where(s => s.Length > 0).Distinct());
        }
        catch { return ""; }
    }

    /// <summary>Explicação do parâmetro 1 dos erros mais comuns.</summary>
    public static string? ParameterHint(CrashDump d) => BaseCode(d.Code) switch
    {
        0x1A => d.Parameters[0] switch
        {
            0x41792 or 0x41793 => Loc.T("uma tabela de páginas da memória apareceu corrompida — típico de RAM instável"),
            0x41790 or 0x41791 => Loc.T("a contagem de páginas da memória não bate certo — RAM ou driver"),
            0x5003 or 0x5100 or 0x5200 => Loc.T("a lista de memória livre está corrompida — muitas vezes RAM"),
            0x61941 => Loc.T("um driver acedeu a memória inválida"),
            0x403 => Loc.T("as tabelas de memória estão corrompidas — RAM ou driver"),
            _ => null,
        },
        0x4E => d.Parameters[0] switch
        {
            0x99 => Loc.T("uma entrada da tabela de memória (PTE/PFN) apareceu corrompida — quase sempre RAM instável (XMP/EXPO), mais raramente um driver"),
            0x9A => Loc.T("um driver libertou memória que ainda estava bloqueada"),
            0x07 => Loc.T("um driver desbloqueou uma página de memória mais vezes do que a bloqueou"),
            _ => Loc.T("a lista de páginas da memória está corrompida — RAM instável ou um driver"),
        },
        0x7A => (d.Parameters[1] & 0xFFFFFFFF) switch
        {
            0xC000009C or 0xC000016A => Loc.T("o disco devolveu setores ilegíveis"),
            0xC0000185 => Loc.T("falha de comunicação com o disco (cabo ou controlador)"),
            0xC000009D => Loc.T("o disco desligou-se por momentos"),
            0xC0000005 => Loc.T("acesso inválido ao ler memória do disco — muitas vezes RAM"),
            _ => Loc.T("falha a ler dados da memória virtual — disco, cabo ou RAM"),
        },
        0x7E or 0x1E or 0x3B => (d.Parameters[0] & 0xFFFFFFFF) == 0xC0000005 ? Loc.T("um driver tentou aceder a memória que não podia") : null,
        0x124 => Loc.T("o processador reportou um erro de hardware (CPU, RAM, overclock ou temperatura)"),
        _ => null,
    };

    /// <summary>Conclusão sobre todos os crashes juntos.</summary>
    public static CrashVerdict Verdict(IReadOnlyList<CrashDump> dumps)
    {
        if (dumps.Count == 0)
            return new CrashVerdict(Loc.T("Sem relatórios de ecrã azul"), Loc.T("O Windows não guardou nenhum minidump."), false, null);

        var memoryCodes = dumps.Count(d => EventLogCheck.BugChecks.TryGetValue(BaseCode(d.Code), out var i) && i.Memory);
        var thirdParty = dumps.Where(d => d.CausedBy != null && !IsWindowsModule(d.CausedBy))
                              .GroupBy(d => d.CausedBy!, StringComparer.OrdinalIgnoreCase)
                              .OrderByDescending(g => g.Count()).FirstOrDefault();
        var distinctCodes = dumps.Select(d => BaseCode(d.Code)).Distinct().Count();

        // o mesmo driver de terceiros em metade ou mais dos crashes: é ele
        if (thirdParty != null && thirdParty.Count() * 2 >= dumps.Count && thirdParty.Count() >= 2)
        {
            var desc = Describe(thirdParty.Key);
            return new CrashVerdict(
                Loc.T($"O driver {thirdParty.Key} está em {thirdParty.Count()} de {dumps.Count} ecrãs azuis"),
                Loc.T($"{thirdParty.Key}{(desc.Length > 0 ? $" ({desc})" : "")} estava a correr quando o PC crashou. ") +
                Loc.T("Atualiza ou reinstala este driver (aba Drivers). Se for de um programa (antivírus, RGB, overclock…), desinstala-o para testar."),
                false, thirdParty.Key);
        }

        // códigos diferentes, quase todos de memória, sem um driver culpado: aponta para hardware
        if (memoryCodes * 2 >= dumps.Count)
        {
            return new CrashVerdict(
                Loc.T("Os ecrãs azuis apontam para a RAM"),
                Loc.T($"{memoryCodes} de {dumps.Count} são erros de memória") + (distinctCodes > 1 ? Loc.T($", com {distinctCodes} códigos diferentes") : "") +
                Loc.T(", e nenhum driver aparece repetido. Quando os erros mudam de crash para crash sem um culpado fixo, a causa costuma ser hardware: ") +
                Loc.T("RAM instável (muitas vezes por causa do XMP/EXPO), overclock, ou o disco. Faz o teste de RAM e experimenta desligar o XMP na BIOS."),
                true, null);
        }

        return new CrashVerdict(
            Loc.T($"{dumps.Count} ecrãs azuis sem um culpado claro"),
            Loc.T("Os crashes não apontam sempre para o mesmo driver. Atualiza os drivers principais (gráfica, chipset, rede), corre o SFC e o teste de RAM."),
            false, null);
    }
}
