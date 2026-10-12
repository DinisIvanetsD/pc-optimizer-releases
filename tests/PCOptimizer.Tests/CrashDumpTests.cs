using System.Text;
using PCOptimizer.Core.Diagnostics;
using Xunit;

namespace PCOptimizer.Tests;

/// <summary>Minidumps sintéticos (o formato real, mas sem dados do PC de ninguém).</summary>
public class CrashDumpTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "pcopt-dumps-" + Guid.NewGuid().ToString("N")[..8]);

    public CrashDumpTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    const ulong NtBase = 0xFFFFF80400000000, DrvBase = 0xFFFFF80450000000;

    /// <summary>Cria um minidump triage com dois módulos (ntoskrnl + um driver) e uma pilha.</summary>
    string MakeDump(string name, uint code, ulong[] p, string driver, params ulong[] stack)
    {
        var b = new byte[0x6000];
        Encoding.ASCII.GetBytes("PAGEDU64").CopyTo(b, 0);
        BitConverter.GetBytes(code).CopyTo(b, 0x38);
        for (var i = 0; i < 4; i++) BitConverter.GetBytes(p[i]).CopyTo(b, 0x40 + i * 8);
        BitConverter.GetBytes(4u).CopyTo(b, 0xF98); // triage
        BitConverter.GetBytes(new DateTime(2026, 10, 6, 18, 15, 0).ToFileTime()).CopyTo(b, 0xFA8);

        const int t = 0x2000, stackOff = 0x3000, drvOff = 0x4000, entry = 0x90, pool = drvOff + 2 * entry;
        BitConverter.GetBytes((uint)stackOff).CopyTo(b, t + 0x28);
        BitConverter.GetBytes((uint)(stack.Length * 8)).CopyTo(b, t + 0x2C);
        BitConverter.GetBytes((uint)drvOff).CopyTo(b, t + 0x30);
        BitConverter.GetBytes(2u).CopyTo(b, t + 0x34);
        BitConverter.GetBytes((uint)pool).CopyTo(b, t + 0x38);
        for (var i = 0; i < stack.Length; i++) BitConverter.GetBytes(stack[i]).CopyTo(b, stackOff + i * 8);

        var names = new[] { "ntoskrnl.exe", driver };
        var bases = new[] { NtBase, DrvBase };
        var strOff = pool;
        for (var i = 0; i < 2; i++)
        {
            var e = drvOff + i * entry;
            BitConverter.GetBytes((uint)strOff).CopyTo(b, e);
            BitConverter.GetBytes(bases[i]).CopyTo(b, e + 0x38);
            BitConverter.GetBytes(0x100000u).CopyTo(b, e + 0x48);
            BitConverter.GetBytes((uint)names[i].Length).CopyTo(b, strOff);
            Encoding.Unicode.GetBytes(names[i]).CopyTo(b, strOff + 4);
            strOff += 4 + names[i].Length * 2 + 2;
        }
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, b);
        return path;
    }

    [Fact]
    public void Le_codigo_parametros_e_data()
    {
        var d = CrashDumps.Read(MakeDump("a.dmp", 0x1A, new ulong[] { 0x41792, 1, 0x100000, 0 }, "teste.sys", NtBase + 0x10))!;
        Assert.Equal(0x1Au, d.Code);
        Assert.Equal("MEMORY_MANAGEMENT", d.CodeName);
        Assert.Equal(0x41792UL, d.Parameters[0]);
        Assert.Equal(new DateTime(2026, 10, 6, 18, 15, 0), d.When);
        Assert.Contains("RAM", CrashDumps.ParameterHint(d));
    }

    [Fact]
    public void Evento_sem_minidump_explica_PFN_LIST_CORRUPT()
    {
        // o que BlueScreenEvents constrói a partir do Visualizador de Eventos (sem administrador)
        var d = new CrashDump("", DateTime.Now, 0x4E, new ulong[] { 0x99, 0x66431a, 2, 0 }, null, "", Array.Empty<string>());
        Assert.Equal("PFN_LIST_CORRUPT", d.CodeName);
        Assert.Contains("RAM", CrashDumps.ParameterHint(d));
        Assert.StartsWith(PCOptimizer.Core.Util.Loc.T("hoje"), EventLogCheck.WhenText(DateTime.Now));
    }

    [Fact]
    public void Titulo_do_ecra_azul_usa_data_fixa_que_nao_muda_com_os_dias()
    {
        // a manutenção usa o título para não repetir avisos: "hoje"/"ontem" fariam o mesmo crash parecer novo
        var t = new DateTime(2026, 10, 11, 19, 2, 0);
        Assert.Equal(EventLogCheck.WhenFixed(t), EventLogCheck.WhenFixed(t));
        Assert.DoesNotContain(PCOptimizer.Core.Util.Loc.T("hoje"), EventLogCheck.WhenFixed(t));
        Assert.Contains("11/10", EventLogCheck.WhenFixed(t));
        Assert.StartsWith(PCOptimizer.Core.Util.Loc.T("ontem"), EventLogCheck.WhenText(DateTime.Now.AddDays(-1)));
    }

    [Fact]
    public void Count_so_conta_minidumps_a_partir_da_data()
    {
        Assert.Equal(0, CrashDumps.Count(DateTime.Now.AddYears(10)));
    }

    [Fact]
    public void Driver_de_terceiros_na_pilha_e_o_culpado()
    {
        var d = CrashDumps.Read(MakeDump("b.dmp", 0xD1, new ulong[] { 0, 2, 0, 0 }, "malandro.sys", NtBase + 0x50, DrvBase + 0x1234))!;
        Assert.Equal("malandro.sys", d.CausedBy);
        Assert.Contains("ntoskrnl.exe", d.StackDrivers);
    }

    [Fact]
    public void Endereco_do_erro_tem_prioridade_sobre_a_pilha()
    {
        // 0x7E: o parâmetro 2 é o endereço onde rebentou
        var d = CrashDumps.Read(MakeDump("c.dmp", 0x1000007E, new ulong[] { 0xC0000005, DrvBase + 0x99, 0, 0 }, "culpado.sys", NtBase + 1))!;
        Assert.Equal("culpado.sys", d.CausedBy);
        Assert.Equal("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", d.CodeName); // 0x1000007E = 0x7E
    }

    [Fact]
    public void Mesmo_driver_em_varios_crashes_da_veredicto_de_driver()
    {
        MakeDump("1.dmp", 0xD1, new ulong[4], "malandro.sys", DrvBase + 1);
        MakeDump("2.dmp", 0x0A, new ulong[4], "malandro.sys", DrvBase + 2);
        MakeDump("3.dmp", 0x3B, new ulong[4], "malandro.sys", DrvBase + 3);
        var v = CrashDumps.Verdict(CrashDumps.ReadAll(_dir));
        Assert.Equal("malandro.sys", v.GuiltyDriver);
        Assert.False(v.PointsToRam);
    }

    [Fact]
    public void Erros_de_memoria_variados_sem_culpado_apontam_para_a_RAM()
    {
        MakeDump("1.dmp", 0x1A, new ulong[] { 0x41792, 0, 0, 0 }, "x.sys", NtBase + 1);
        MakeDump("2.dmp", 0x7A, new ulong[] { 1, 0xC0000005, 0, 0 }, "x.sys", NtBase + 2);
        MakeDump("3.dmp", 0x50, new ulong[4], "y.sys", NtBase + 3);
        var v = CrashDumps.Verdict(CrashDumps.ReadAll(_dir));
        Assert.True(v.PointsToRam);
        Assert.Null(v.GuiltyDriver);
    }

    [Fact]
    public void Ficheiro_que_nao_e_dump_e_ignorado()
    {
        File.WriteAllText(Path.Combine(_dir, "lixo.dmp"), "isto não é um dump");
        Assert.Empty(CrashDumps.ReadAll(_dir));
    }
}
