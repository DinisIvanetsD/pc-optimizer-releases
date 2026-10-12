using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Network;

public sealed record PingStats(string Target, string Host, int Sent, int Received, double Min, double Avg, double Max, double Jitter, string Method)
{
    public double LossPercent => Sent == 0 ? 100 : 100.0 * (Sent - Received) / Sent;
    public bool Reachable => Received > 0;

    public override string ToString() => !Reachable
        ? Loc.T("Sem resposta")
        : Loc.T($"{Avg:N0} ms  ·  jitter {Jitter:N1} ms  ·  {LossPercent:N0}% perda") + (Method == "TCP" ? "  (TCP)" : "");
}

public static class PingTester
{
    /// <summary>
    /// Faz <paramref name="count"/> pings. Se o servidor não responder a ICMP (muitos servidores de jogos
    /// não respondem), mede o tempo de ligação TCP à porta 443, que dá praticamente o mesmo valor.
    /// </summary>
    public static async Task<PingStats> PingAsync(string target, string host, int count = 30, int intervalMs = 150,
        bool tcpFallback = true, CancellationToken ct = default)
    {
        IPAddress ip;
        if (!IPAddress.TryParse(host, out ip!))
        {
            try
            {
                var all = await Dns.GetHostAddressesAsync(host, ct);
                ip = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.First();
            }
            catch
            {
                return new PingStats(target, host, count, 0, 0, 0, 0, 0, "DNS");
            }
        }

        var method = "ICMP";
        var rtts = new List<double>(count);
        using var ping = new Ping();

        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            double? rtt;
            if (method == "ICMP")
            {
                rtt = await IcmpAsync(ping, ip);
                if (rtt is null && i == 2 && rtts.Count == 0 && tcpFallback)
                {
                    method = "TCP"; // 3 falhas seguidas: o servidor ignora ICMP
                    rtts.Clear();
                    i = -1;
                    continue;
                }
            }
            else
            {
                rtt = await TcpConnectAsync(ip, 443, 1500);
            }
            if (rtt is double v) rtts.Add(v);
            await Task.Delay(intervalMs, ct);
        }

        if (rtts.Count == 0) return new PingStats(target, host, count, 0, 0, 0, 0, 0, method);
        double jitter = 0;
        for (var i = 1; i < rtts.Count; i++) jitter += Math.Abs(rtts[i] - rtts[i - 1]);
        if (rtts.Count > 1) jitter /= rtts.Count - 1;
        return new PingStats(target, host, count, rtts.Count, rtts.Min(), rtts.Average(), rtts.Max(), jitter, method);
    }

    static async Task<double?> IcmpAsync(Ping ping, IPAddress ip)
    {
        try
        {
            var r = await ping.SendPingAsync(ip, 1000);
            return r.Status == IPStatus.Success ? r.RoundtripTime : null;
        }
        catch
        {
            return null;
        }
    }

    static async Task<double?> TcpConnectAsync(IPAddress ip, int port, int timeoutMs)
    {
        using var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var cts = new CancellationTokenSource(timeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            await s.ConnectAsync(ip, port, cts.Token);
            return sw.Elapsed.TotalMilliseconds;
        }
        catch
        {
            return null;
        }
    }
}
