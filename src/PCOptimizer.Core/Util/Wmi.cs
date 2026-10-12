using System.Management;

namespace PCOptimizer.Core.Util;

public static class Wmi
{
    /// <summary>Corre uma consulta WMI (root\CIMV2) e chama <paramref name="each"/> por resultado. Erros são ignorados.</summary>
    public static void Each(string query, Action<ManagementBaseObject> each)
    {
        try
        {
            using var s = new ManagementObjectSearcher(query);
            foreach (var o in s.Get())
            {
                using (o) each(o);
            }
        }
        catch { }
    }
}
