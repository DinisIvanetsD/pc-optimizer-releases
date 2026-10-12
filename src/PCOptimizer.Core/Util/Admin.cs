using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace PCOptimizer.Core.Util;

public static class Admin
{
    public static bool IsElevated { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Volta a abrir esta app como administrador. Devolve false se o utilizador cancelou.</summary>
    public static bool RestartElevated(string arguments = "")
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public static void Require()
    {
        if (!IsElevated)
            throw new UnauthorizedAccessException(Loc.T("Precisa de administrador. Usa \"Abrir como administrador\"."));
    }
}
