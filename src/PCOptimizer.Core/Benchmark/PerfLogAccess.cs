using System.Security.Principal;
using System.Text;
using PCOptimizer.Core.History;
using PCOptimizer.Core.Settings;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Benchmark;

/// <summary>Ready = já dá para medir; NeedsEnable = falta ativar; NeedsSignOut = ativado, mas o Windows só aplica o grupo ao terminar sessão.</summary>
public enum FpsAccess { Ready, NeedsEnable, NeedsSignOut }

/// <summary>
/// Medir FPS (ETW) pede administrador ou o grupo "Performance Log Users". Aqui o utilizador entra nesse grupo
/// uma vez (um pedido de administrador) e sai quando desfaz no Histórico. Sempre por SID, que não muda com a língua.
/// </summary>
public static class PerfLogAccess
{
    public const string GroupSid = "S-1-5-32-559";

    /// <summary>O token da sessão atual já traz o grupo (só muda depois de terminar sessão).</summary>
    public static bool IsMember
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.Groups?.Any(g => g.Value == GroupSid) ?? false;
        }
    }

    public static FpsAccess State(bool elevated, bool member, DateTime? enabledAt) =>
        elevated || member ? FpsAccess.Ready : enabledAt != null ? FpsAccess.NeedsSignOut : FpsAccess.NeedsEnable;

    public static FpsAccess Current => State(Admin.IsElevated, IsMember, AppSettings.Current.FpsEnabledAt);

    // Juntar duas vezes (ou tirar quem não está) dá erro: esse erro em concreto é ignorado, qualquer outro falha.
    // Sem Get-LocalGroupMember: falha em grupos com SIDs órfãos ou do Azure AD.
    public static string AddScript(string userSid) => Script(userSid, "Add", "MemberExistsException");

    public static string RemoveScript(string userSid) => Script(userSid, "Remove", "MemberNotFoundException");

    static string Script(string userSid, string verb, string alreadyDone)
    {
        // corre elevado e o SID pode vir do history.json (editável): só passa um SID de verdade, já normalizado
        userSid = new SecurityIdentifier(userSid).Value;
        return $"$ErrorActionPreference='Stop'; try {{ {verb}-LocalGroupMember -SID {Shell.Quote(GroupSid)} -Member {Shell.Quote(userSid)} }} " +
               $"catch [Microsoft.PowerShell.Commands.{alreadyDone}] {{ }}";
    }

    public static async Task EnableAsync()
    {
        Simulation.Guard(Loc.T("ativar a medição de FPS"));
        using var id = WindowsIdentity.GetCurrent();
        var sid = id.User!.Value;
        if (!await RunAsAdminAsync(AddScript(sid)))
            throw new InvalidOperationException(Loc.T("Não foi possível ativar a medição de FPS."));
        ChangeLog.Record(ChangeKind.Tweak, Loc.T("Medição de FPS ativada"),
            Loc.T("O teu utilizador entrou no grupo \"Performance Log Users\" do Windows."),
            UndoKind.PerfLogUsers, new() { ["user"] = sid });
        AppSettings.Current.FpsEnabledAt = DateTime.Now;
        AppSettings.Current.Save();
    }

    public static async Task DisableAsync(string userSid)
    {
        Simulation.Guard(Loc.T("desativar a medição de FPS"));
        if (!await RunAsAdminAsync(RemoveScript(userSid)))
            throw new InvalidOperationException(Loc.T("Não foi possível desativar a medição de FPS."));
        ChangeLog.MarkUndone(UndoKind.PerfLogUsers, "user", userSid);
        AppSettings.Current.FpsEnabledAt = null;
        AppSettings.Current.Save();
    }

    /// <summary>Com administrador corre o script já; sem ele, pede a elevação (como o Winget.UninstallAsync).</summary>
    static async Task<bool> RunAsAdminAsync(string script)
    {
        if (Admin.IsElevated) return (await Shell.PowerShellAsync(script)).Ok;
        var args = "-NoProfile -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        // tempo longo como no Winget.UninstallAsync: se acabasse com o pedido de administrador ainda aberto, aprovar
        // mais tarde mudava o grupo sem entrada no Histórico
        var r = await Shell.PowerShellAsync(
            $"(Start-Process powershell -ArgumentList {Shell.Quote(args)} -Verb RunAs -Wait -PassThru -WindowStyle Hidden).ExitCode",
            30 * 60_000);
        return r.Ok && r.StdOut.Trim() == "0";
    }
}
