using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using PCOptimizer.Core.Optimization;
using PCOptimizer.Core.Util;
using PCOptimizer.Core.History;

namespace PCOptimizer.Core.Network;

public sealed record BufferbloatResult(double IdleMs, double LoadedMs, double DownloadMbps)
{
    public double AddedMs => Math.Max(0, LoadedMs - IdleMs);

    public string Grade => AddedMs switch
    {
        < 5 => "A+",
        < 30 => "A",
        < 60 => "B",
        < 200 => "C",
        < 400 => "D",
        _ => "F",
    };
}

/// <summary>
/// Bufferbloat: quando alguém faz um download, o router enche os buffers e o ping dispara.
/// Mede o ping com a rede parada e durante um download.
/// </summary>
public static class BufferbloatTest
{
    const string DownloadUrl = "https://speed.cloudflare.com/__down?bytes=100000000";

    public static async Task<BufferbloatResult> RunAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(Loc.T("A medir o ping com a rede parada…"));
        var idle = await PingTester.PingAsync("Internet", "1.1.1.1", 20, 150, ct: ct);

        progress?.Report(Loc.T("A medir o ping durante um download (≈10 s, usa até 300 MB)…"));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long bytes = 0;
        var sw = Stopwatch.StartNew();
        var downloads = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            try
            {
                using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, loadCts.Token);
                await using var stream = await resp.Content.ReadAsStreamAsync(loadCts.Token);
                var buf = new byte[81920];
                int n;
                while ((n = await stream.ReadAsync(buf, loadCts.Token)) > 0) Interlocked.Add(ref bytes, n);
            }
            catch { }
        })).ToArray();

        await Task.Delay(1500, ct); // deixar o download ganhar velocidade
        var loaded = await PingTester.PingAsync("Internet", "1.1.1.1", 25, 150, ct: ct);
        var seconds = sw.Elapsed.TotalSeconds;
        loadCts.Cancel();
        await Task.WhenAll(downloads);

        var mbps = Interlocked.Read(ref bytes) * 8 / 1e6 / seconds;
        progress?.Report(Loc.T("Teste concluído."));
        return new BufferbloatResult(idle.Avg, loaded.Avg, mbps);
    }
}

public sealed record DnsResult(string Label, string Server, string? Secondary, double? MedianMs, int Failures, bool IsCurrent)
{
    public string TimeText => MedianMs is double ms ? $"{ms:N0} ms" + (Failures > 0 ? $" ({Failures} falhas)" : "") : Loc.T("Sem resposta");
}

/// <summary>Compara a velocidade de vários servidores DNS com consultas reais.</summary>
public static class DnsBenchmark
{
    static readonly string[] Domains =
    {
        "google.com", "youtube.com", "microsoft.com", "wikipedia.org",
        "steampowered.com", "epicgames.com", "riotgames.com", "discord.com",
    };

    static readonly (string Label, string Ip, string Secondary)[] Public =
    {
        ("Cloudflare", "1.1.1.1", "1.0.0.1"),
        ("Google", "8.8.8.8", "8.8.4.4"),
        ("Quad9", "9.9.9.9", "149.112.112.112"),
    };

    public static async Task<List<DnsResult>> RunAsync(CancellationToken ct = default)
    {
        var servers = new List<(string Label, string Ip, string? Secondary, bool Current)>();
        var conn = NetworkDiagnostics.GetActiveConnection();
        if (conn != null)
        {
            foreach (var dns in conn.Nic.GetIPProperties().DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                servers.Add(("Atual (router/operador)", dns.ToString(), null, true));
        }
        foreach (var p in Public)
        {
            if (servers.Any(s => s.Ip == p.Ip)) continue;
            servers.Add((p.Label, p.Ip, p.Secondary, false));
        }

        var tasks = servers.Select(async s =>
        {
            var times = new List<double>();
            var fails = 0;
            foreach (var domain in Domains)
            {
                var t = await QueryAsync(IPAddress.Parse(s.Ip), domain, 2000, ct);
                if (t is double ms) times.Add(ms); else fails++;
            }
            times.Sort();
            double? median = times.Count == 0 ? null : times[times.Count / 2];
            return new DnsResult(s.Label, s.Ip, s.Secondary, median, fails, s.Current);
        });
        var results = await Task.WhenAll(tasks);
        return results.OrderBy(r => r.MedianMs ?? double.MaxValue).ToList();
    }

    static async Task<double?> QueryAsync(IPAddress server, string domain, int timeoutMs, CancellationToken ct)
    {
        using var udp = new UdpClient(server.AddressFamily);
        var id = (ushort)Random.Shared.Next(ushort.MaxValue);
        var query = BuildQuery(id, domain);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            await udp.SendAsync(query, new IPEndPoint(server, 53), cts.Token);
            while (true)
            {
                var res = await udp.ReceiveAsync(cts.Token);
                if (res.Buffer.Length >= 2 && res.Buffer[0] == (byte)(id >> 8) && res.Buffer[1] == (byte)id)
                    return sw.Elapsed.TotalMilliseconds;
            }
        }
        catch
        {
            return null;
        }
    }

    static byte[] BuildQuery(ushort id, string domain)
    {
        var b = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in domain.Split('.'))
        {
            b.Add((byte)label.Length);
            b.AddRange(Encoding.ASCII.GetBytes(label));
        }
        b.AddRange(new byte[] { 0, 0, 1, 0, 1 }); // fim do nome, tipo A, classe IN
        return b.ToArray();
    }
}

/// <summary>Muda o DNS da placa de rede ativa e guarda o original para poder repor.</summary>
public static class DnsChanger
{
    const string SnapKey = "net.dns";

    public static bool HasBackup => SnapshotStore.Has(SnapKey);

    public static async Task ApplyAsync(string primary, string? secondary)
    {
        Simulation.Guard(Loc.T($"mudar o DNS para {primary}"));
        Admin.Require();
        var conn = NetworkDiagnostics.GetActiveConnection() ?? throw new InvalidOperationException(Loc.T("Sem ligação de rede ativa."));
        var nameServer = Reg.Get($@"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{conn.Nic.Id}", "NameServer") as string;
        SnapshotStore.PutIfMissing(SnapKey, new Dictionary<string, string?>
        {
            ["ifIndex"] = conn.IfIndex.ToString(),
            ["nameServer"] = nameServer ?? "",
        });
        var list = secondary is null ? Shell.Quote(primary) : $"{Shell.Quote(primary)},{Shell.Quote(secondary)}";
        var r = await Shell.PowerShellAsync(
            $"$ErrorActionPreference='Stop'; Set-DnsClientServerAddress -InterfaceIndex {conn.IfIndex} -ServerAddresses @({list}); Clear-DnsClientCache");
        if (!r.Ok) throw new InvalidOperationException(Shell.CleanError(r));
        ChangeLog.Record(ChangeKind.Network, Loc.T($"DNS mudado para {primary}"), Loc.T($"Placa de rede: {conn.Nic.Name}"), UndoKind.Dns,
            new() { ["k"] = "dns" });
    }

    public static async Task<bool> RevertAsync()
    {
        Simulation.Guard(Loc.T("repor o DNS original"));
        var s = SnapshotStore.Get(SnapKey);
        if (s is null) return false;
        Admin.Require();
        var index = s.GetValueOrDefault("ifIndex");
        var ns = s.GetValueOrDefault("nameServer") ?? "";
        var servers = ns.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var script = servers.Length == 0
            ? $"Set-DnsClientServerAddress -InterfaceIndex {index} -ResetServerAddresses"
            : $"Set-DnsClientServerAddress -InterfaceIndex {index} -ServerAddresses @({string.Join(',', servers.Select(Shell.Quote))})";
        var r = await Shell.PowerShellAsync($"$ErrorActionPreference='Stop'; {script}; Clear-DnsClientCache");
        if (!r.Ok) throw new InvalidOperationException(Shell.CleanError(r));
        SnapshotStore.Remove(SnapKey);
        ChangeLog.MarkUndone(UndoKind.Dns, "k", "dns");
        return true;
    }
}

/// <summary>
/// Marca o tráfego de um jogo com prioridade QoS (DSCP 46). Só tem efeito se o router
/// respeitar QoS/DSCP, mas não prejudica nada se não respeitar.
/// </summary>
public static class GameQos
{
    const string Prefix = "PCOptimizer-";

    public static bool IsValidExe(string exe) => Regex.IsMatch(exe, @"^[\w\-. ]+\.exe$", RegexOptions.IgnoreCase);

    public static async Task<List<string>> ListAsync()
    {
        var r = await Shell.PowerShellAsync(
            $"Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like '{Prefix}*' }} | ForEach-Object {{ $_.Name.Substring({Prefix.Length}) }}");
        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public static async Task AddAsync(string exe)
    {
        Simulation.Guard(Loc.T($"dar prioridade de rede (QoS) a {exe}"));
        Admin.Require();
        if (!IsValidExe(exe)) throw new ArgumentException(Loc.T("Escreve só o nome do executável, por exemplo: cs2.exe"));
        var script = $"""
            $ErrorActionPreference = 'Stop'
            New-NetQosPolicy -Name {Shell.Quote(Prefix + exe)} -AppPathNameMatchCondition {Shell.Quote(exe)} -DSCPAction 46 -NetworkProfile All | Out-Null
            New-Item -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\QoS' -Force | Out-Null
            Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\QoS' -Name 'Do not use NLA' -Value '1'
            """;
        var r = await Shell.PowerShellAsync(script);
        if (!r.Ok) throw new InvalidOperationException(Shell.CleanError(r));
        ChangeLog.Record(ChangeKind.Network, Loc.T($"Prioridade de rede (QoS) para {exe}"), "", UndoKind.Qos, new() { ["exe"] = exe });
    }

    public static async Task RemoveAsync(string exe)
    {
        Simulation.Guard(Loc.T($"tirar a prioridade de rede a {exe}"));
        Admin.Require();
        var r = await Shell.PowerShellAsync(
            $"$ErrorActionPreference='Stop'; Remove-NetQosPolicy -Name {Shell.Quote(Prefix + exe)} -Confirm:$false");
        if (!r.Ok) throw new InvalidOperationException(Shell.CleanError(r));
        ChangeLog.MarkUndone(UndoKind.Qos, "exe", exe);
    }
}

/// <summary>Apps de sincronização na nuvem que podem gastar a rede enquanto jogas.</summary>
public static class SyncApps
{
    static readonly (string Process, string Label)[] Known =
    {
        ("OneDrive", "OneDrive"), ("Dropbox", "Dropbox"), ("GoogleDriveFS", "Google Drive"),
        ("MEGAsync", "MEGA"), ("iCloudDrive", "iCloud Drive"), ("pCloud", "pCloud"), ("BoxSync", "Box"),
    };

    public static List<string> Running()
    {
        var list = new List<string>();
        foreach (var (process, label) in Known)
        {
            var ps = Process.GetProcessesByName(process);
            if (ps.Length > 0) list.Add(label);
            foreach (var p in ps) p.Dispose();
        }
        return list;
    }

    public static int CloseAll() => Known.Sum(k => ProcessSampler.Kill(k.Process));
}
