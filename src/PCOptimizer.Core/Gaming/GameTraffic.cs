using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using PCOptimizer.Core.Network;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Gaming;

/// <summary>Tráfego entre o jogo e um endereço, num intervalo de tempo.</summary>
public sealed record RemoteTraffic(IPAddress Address, int Port, bool Udp, long Packets, long Bytes);

/// <summary>O servidor a que o jogo está ligado.</summary>
/// <param name="Udp">true = tráfego do jogo propriamente dito (UDP); false = só se viu uma ligação TCP</param>
public sealed record DetectedServer(IPAddress Address, int Port, bool Udp, double PacketsPerSecond);

public static class ServerPicker
{
    /// <summary>Endereço da internet (não da rede de casa, nem loopback, nem multicast).</summary>
    public static bool IsPublic(IPAddress a)
    {
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return false;
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return !(a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || a.GetAddressBytes()[0] is 0xfc or 0xfd);
        var b = a.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] >= 224
                 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                 || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 169 && b[1] == 254)
                 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)); // CGNAT do operador
    }

    /// <summary>
    /// O servidor do jogo é o endereço público com mais pacotes UDP (o jogo em si, ~20–128 por segundo).
    /// Sem UDP (sem administrador), fica a ligação TCP — muitas vezes o servidor de chat/lobby, por isso marcada como tal.
    /// </summary>
    public static DetectedServer? Pick(IEnumerable<RemoteTraffic> traffic, double seconds)
    {
        var pub = traffic.Where(t => IsPublic(t.Address)).ToList();
        var udp = pub.Where(t => t.Udp && t.Packets / Math.Max(1, seconds) >= 5).MaxBy(t => t.Packets);
        if (udp != null) return new DetectedServer(udp.Address, udp.Port, true, udp.Packets / Math.Max(1, seconds));
        var tcp = pub.Where(t => !t.Udp && t.Port != 80).MaxBy(t => t.Packets);
        return tcp is null ? null : new DetectedServer(tcp.Address, tcp.Port, false, tcp.Packets / Math.Max(1, seconds));
    }
}

/// <summary>
/// Vê com quem o jogo fala. Com administrador usa os eventos de rede do Windows (ETW), que mostram
/// também o UDP — o tráfego do jogo propriamente dito. Sem administrador só vê ligações TCP.
/// Não toca nos pacotes: só conta.
/// </summary>
public sealed class GameTrafficWatcher : IDisposable
{
    const string SessionName = "PCOptimizer-GameNet";
    readonly int _pid;
    readonly ConcurrentDictionary<(IPAddress, int, bool), long[]> _counts = new(); // [pacotes, bytes]
    TraceEventSession? _session;
    DateTime _windowStart = DateTime.UtcNow;
    long _bytesUp, _bytesDown;

    public GameTrafficWatcher(int pid) => _pid = pid;

    /// <summary>true = está a ver o UDP (ETW); false = só TCP.</summary>
    public bool SeesUdp { get; private set; }

    public void Start()
    {
        if (!Admin.IsElevated) return;
        try
        {
            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
            var k = _session.Source.Kernel;
            k.UdpIpSend += d => Count(d.ProcessID, d.daddr, d.dport, true, d.size, up: true);
            k.UdpIpRecv += d => Count(d.ProcessID, d.saddr, d.sport, true, d.size, up: false);
            k.UdpIpSendIPV6 += d => Count(d.ProcessID, d.daddr, d.dport, true, d.size, up: true);
            k.UdpIpRecvIPV6 += d => Count(d.ProcessID, d.saddr, d.sport, true, d.size, up: false);
            k.TcpIpSend += d => Count(d.ProcessID, d.daddr, d.dport, false, d.size, up: true);
            k.TcpIpRecv += d => Count(d.ProcessID, d.saddr, d.sport, false, d.size, up: false);
            var source = _session.Source;
            new Thread(() => { try { source.Process(); } catch { } }) { IsBackground = true, Name = "GameNet ETW" }.Start();
            SeesUdp = true;
        }
        catch
        {
            _session?.Dispose();
            _session = null;
        }
    }

    void Count(int pid, IPAddress addr, int port, bool udp, int size, bool up)
    {
        if (pid != _pid) return;
        var c = _counts.GetOrAdd((addr, port, udp), _ => new long[2]);
        Interlocked.Increment(ref c[0]);
        Interlocked.Add(ref c[1], size);
        if (up) Interlocked.Add(ref _bytesUp, size); else Interlocked.Add(ref _bytesDown, size);
    }

    /// <summary>O que se viu desde a última chamada (e recomeça a contar).</summary>
    public (IReadOnlyList<RemoteTraffic> Traffic, double Seconds, double KbUp, double KbDown) Take()
    {
        var now = DateTime.UtcNow;
        var seconds = Math.Max(0.5, (now - _windowStart).TotalSeconds);
        _windowStart = now;
        var list = new List<RemoteTraffic>();
        foreach (var key in _counts.Keys.ToList())
            if (_counts.TryRemove(key, out var c))
                list.Add(new RemoteTraffic(key.Item1, key.Item2, key.Item3, c[0], c[1]));
        if (!SeesUdp)
            // sem ETW: cada ligação TCP conta como 1 (não sabemos o tráfego)
            list.AddRange(PortUsage.TcpRemotes(_pid).Select(e => new RemoteTraffic(e.Address, e.Port, false, 1, 0)));
        var up = Interlocked.Exchange(ref _bytesUp, 0) / 1024.0 / seconds;
        var down = Interlocked.Exchange(ref _bytesDown, 0) / 1024.0 / seconds;
        return (list, seconds, up, down);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
