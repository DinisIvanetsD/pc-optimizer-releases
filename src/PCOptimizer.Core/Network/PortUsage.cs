using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCOptimizer.Core.Network;

public sealed record PortHolder(int Pid, string Name, int Tcp, int Udp)
{
    public int Total => Tcp + Udp;
}

public sealed record PortSnapshot(int TotalTcp, int TotalUdp, IReadOnlyList<PortHolder> Top)
{
    /// <summary>O Windows tem 16 384 portas dinâmicas por protocolo (por defeito).</summary>
    public const int DefaultDynamicPorts = 16384;
}

/// <summary>
/// Conta as ligações TCP e as portas UDP de cada processo (como o "netstat -ano", mas rápido).
/// Serve para apanhar a app que esgota as portas de rede do Windows.
/// </summary>
public static class PortUsage
{
    public static PortSnapshot Take(int top = 5)
    {
        var tcp = CountByPid(isTcp: true);
        var udp = CountByPid(isTcp: false);
        var pids = tcp.Keys.Union(udp.Keys);
        var holders = pids
            .Select(pid => new PortHolder(pid, ProcessName(pid), tcp.GetValueOrDefault(pid), udp.GetValueOrDefault(pid)))
            .OrderByDescending(h => h.Total)
            .Take(top)
            .ToList();
        return new PortSnapshot(tcp.Values.Sum(), udp.Values.Sum(), holders);
    }

    static string ProcessName(int pid)
    {
        if (pid == 0) return "Idle";
        if (pid == 4) return "System";
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return $"PID {pid}";
        }
    }

    static Dictionary<int, int> CountByPid(bool isTcp)
    {
        var counts = new Dictionary<int, int>();
        foreach (var af in new[] { AfInet, AfInet6 })
        {
            var size = 0;
            Call(isTcp, IntPtr.Zero, ref size, af);
            if (size == 0) continue;
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (Call(isTcp, buf, ref size, af) != 0) continue;
                var n = Marshal.ReadInt32(buf);
                // tamanho de cada linha (com o PID na última coluna)
                var rowSize = (isTcp, af) switch
                {
                    (true, AfInet) => 24,   // MIB_TCPROW_OWNER_PID
                    (true, _) => 56,        // MIB_TCP6ROW_OWNER_PID
                    (false, AfInet) => 12,  // MIB_UDPROW_OWNER_PID
                    (false, _) => 28,       // MIB_UDP6ROW_OWNER_PID
                };
                var first = buf + 4;
                for (var i = 0; i < n; i++)
                {
                    var pid = Marshal.ReadInt32(first + i * rowSize + rowSize - 4);
                    counts[pid] = counts.GetValueOrDefault(pid) + 1;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        return counts;
    }

    /// <summary>Endereços remotos das ligações TCP (IPv4, estabelecidas) de um processo. Não precisa de administrador.</summary>
    public static List<System.Net.IPEndPoint> TcpRemotes(int pid)
    {
        var list = new List<System.Net.IPEndPoint>();
        var size = 0;
        Call(true, IntPtr.Zero, ref size, AfInet);
        if (size == 0) return list;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (Call(true, buf, ref size, AfInet) != 0) return list;
            var n = Marshal.ReadInt32(buf);
            for (var i = 0; i < n; i++)
            {
                // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
                var row = buf + 4 + i * 24;
                if (Marshal.ReadInt32(row + 20) != pid || Marshal.ReadInt32(row) != 5 /* ESTABLISHED */) continue;
                var addr = new System.Net.IPAddress((uint)Marshal.ReadInt32(row + 12));
                var port = (Marshal.ReadByte(row + 16) << 8) | Marshal.ReadByte(row + 17);
                list.Add(new System.Net.IPEndPoint(addr, port));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        return list;
    }

    static uint Call(bool isTcp, IntPtr buf, ref int size, int af) => isTcp
        ? GetExtendedTcpTable(buf, ref size, false, af, TcpTableOwnerPidAll, 0)
        : GetExtendedUdpTable(buf, ref size, false, af, UdpTableOwnerPid, 0);

    const int AfInet = 2, AfInet6 = 23;
    const int TcpTableOwnerPidAll = 5, UdpTableOwnerPid = 1;

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);
}
