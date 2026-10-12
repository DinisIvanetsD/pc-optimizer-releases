using System.Text.Json;
using System.Text.RegularExpressions;
using PCOptimizer.Core.History;
using PCOptimizer.Core.Optimization;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Network;

/// <summary>Uma opção da placa de rede que atrasa os pacotes para poupar energia ou CPU.</summary>
public sealed record NicOption(string Keyword, string Label, string Why, string Recommended);

public sealed record NicChange(string Keyword, string Label, string Why, string From, string To);

public sealed record NicAdapter(string Name, string Description, string Guid, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Afina a placa de rede para latência: desliga a poupança de energia e a moderação de interrupções,
/// e o algoritmo de Nagle (agrupa pacotes TCP pequenos). Usa os nomes internos das opções
/// (iguais em qualquer língua do Windows). Guarda os valores originais para Desfazer.
/// </summary>
public static class NicTuning
{
    /// <summary>Nagle/ACK atrasado: valores no registo da interface, não da placa.</summary>
    public const string NagleKey = "TcpAckFrequency+TCPNoDelay";

    public static readonly IReadOnlyList<NicOption> Options = new NicOption[]
    {
        new("*InterruptModeration", Loc.T("Moderação de interrupções"),
            Loc.T("A placa junta vários pacotes antes de avisar o processador: poupa CPU mas atrasa cada pacote (até ~1 ms)."), "0"),
        new("*EEE", Loc.T("Ethernet de baixo consumo (EEE)"),
            Loc.T("A placa adormece entre pacotes e demora a acordar, o que dá picos de atraso."), "0"),
        new("EnableGreenEthernet", Loc.T("Ethernet verde"),
            Loc.T("Poupança de energia da Realtek: reduz a potência do sinal e pode causar pequenas quebras."), "0"),
        new("AdvancedEEE", Loc.T("EEE avançado"), Loc.T("Mais um modo de poupança da Realtek com o mesmo efeito do EEE."), "0"),
        new("PowerSavingMode", Loc.T("Modo de poupança de energia"), Loc.T("A placa entra em poupança quando há pouco tráfego, como entre rondas de um jogo."), "0"),
        new("GigaLite", "Gigabit Lite", Loc.T("Liga a 500 Mbps em vez de 1 Gbps para poupar energia."), "0"),
        new("*SelectiveSuspend", Loc.T("Suspensão seletiva (USB)"), Loc.T("Em placas USB, o Windows desliga a placa quando acha que não está a ser usada."), "0"),
        new(NagleKey, "Nagle (TCP)",
            Loc.T("O Windows junta pacotes TCP pequenos e espera pela confirmação antes de enviar. Atrasa jogos que usam TCP (ex.: League of Legends, WoW)."), "1"),
    };

    /// <summary>O que mudar nesta placa: só as opções que ela tem e que não estão já no valor recomendado.</summary>
    public static List<NicChange> Plan(IReadOnlyDictionary<string, string> current) =>
        Options.Where(o => current.TryGetValue(o.Keyword, out var v) && v != o.Recommended)
               .Select(o => new NicChange(o.Keyword, o.Label, o.Why, Describe(o.Keyword, current[o.Keyword]), Describe(o.Keyword, o.Recommended)))
               .ToList();

    static string Describe(string keyword, string v) => keyword == NagleKey
        ? (v == "1" ? Loc.T("Desligado") : Loc.T("Ligado"))
        : v == "0" ? Loc.T("Desligado") : Loc.T("Ligado");

    static string SnapshotKey(string guid) => "nic:" + guid;
    public static bool HasBackup(string guid) => SnapshotStore.Has(SnapshotKey(guid));

    /// <summary>A placa que leva o tráfego para a internet (a da rota por defeito).</summary>
    public static async Task<NicAdapter?> ReadAsync()
    {
        var keywords = string.Join(",", Options.Where(o => o.Keyword != NagleKey).Select(o => Shell.Quote(o.Keyword)));
        var r = await Shell.PowerShellAsync($$"""
            $route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object { $_.RouteMetric + $_.InterfaceMetric } | Select-Object -First 1
            if (-not $route) { return }
            $a = Get-NetAdapter -InterfaceIndex $route.InterfaceIndex
            $v = @{}
            foreach ($k in @({{keywords}})) {
              $p = Get-NetAdapterAdvancedProperty -Name $a.Name -RegistryKeyword $k -ErrorAction SilentlyContinue
              if ($p -and $p.RegistryValue) { $v[$k] = [string]$p.RegistryValue[0] }
            }
            $ik = "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$($a.InterfaceGuid)"
            $ack = (Get-ItemProperty $ik -Name TcpAckFrequency -ErrorAction SilentlyContinue).TcpAckFrequency
            $nd = (Get-ItemProperty $ik -Name TCPNoDelay -ErrorAction SilentlyContinue).TCPNoDelay
            $v['{{NagleKey}}'] = if ($ack -eq 1 -and $nd -eq 1) { '1' } else { '0' }
            [pscustomobject]@{ Name = $a.Name; Description = $a.InterfaceDescription; Guid = [string]$a.InterfaceGuid; Values = $v } | ConvertTo-Json -Compress
            """);
        if (!r.Ok || string.IsNullOrWhiteSpace(r.StdOut)) return null;
        using var doc = JsonDocument.Parse(r.StdOut.Trim());
        var root = doc.RootElement;
        var values = new Dictionary<string, string>();
        foreach (var p in root.GetProperty("Values").EnumerateObject()) values[p.Name] = p.Value.GetString() ?? "";
        return new NicAdapter(root.GetProperty("Name").GetString()!, root.GetProperty("Description").GetString() ?? "",
            root.GetProperty("Guid").GetString()!, values);
    }

    /// <summary>Aplica o plano. A placa reinicia no fim (a internet cai uns 5 segundos).</summary>
    public static async Task ApplyAsync(NicAdapter nic, IReadOnlyList<NicChange> changes)
    {
        Simulation.Guard(Loc.T($"afinar a placa de rede {nic.Description}"));
        Admin.Require();
        if (changes.Count == 0) return;
        if (!Regex.IsMatch(nic.Guid, @"^\{?[0-9a-fA-F-]{36}\}?$")) throw new InvalidOperationException(Loc.T("Placa de rede inválida."));

        // guarda o estado original (só da primeira vez: é sempre o do PC antes da app)
        SnapshotStore.PutIfMissing(SnapshotKey(nic.Guid), changes.ToDictionary(c => c.Keyword, c => (string?)nic.Values[c.Keyword]));
        await SetAsync(nic, changes.ToDictionary(c => c.Keyword, c => Options.First(o => o.Keyword == c.Keyword).Recommended));
        ChangeLog.Record(ChangeKind.Network, Loc.T($"Placa de rede afinada ({nic.Description})"),
            string.Join(", ", changes.Select(c => c.Label)), UndoKind.Nic, new() { ["guid"] = nic.Guid });
    }

    public static async Task<bool> RevertAsync(string guid)
    {
        Simulation.Guard(Loc.T("repor a placa de rede"));
        Admin.Require();
        if (SnapshotStore.Get(SnapshotKey(guid)) is not { } original) return false;
        var nic = await ReadAsync();
        if (nic is null || !nic.Guid.Equals(guid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.T("A placa de rede afinada já não é a que está ligada à internet. Liga-a e tenta outra vez."));
        await SetAsync(nic, original.ToDictionary(kv => kv.Key, kv => kv.Value ?? "0"));
        SnapshotStore.Remove(SnapshotKey(guid));
        ChangeLog.MarkUndone(UndoKind.Nic, "guid", guid);
        return true;
    }

    static async Task SetAsync(NicAdapter nic, Dictionary<string, string> values)
    {
        var lines = new List<string> { "$ErrorActionPreference = 'Stop'" };
        foreach (var (k, v) in values)
        {
            if (!Regex.IsMatch(v, @"^\d+$")) continue;
            if (k == NagleKey)
            {
                var ik = $@"HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{nic.Guid}";
                lines.Add(v == "1"
                    ? $"Set-ItemProperty {Shell.Quote(ik)} -Name TcpAckFrequency -Value 1 -Type DWord; Set-ItemProperty {Shell.Quote(ik)} -Name TCPNoDelay -Value 1 -Type DWord"
                    : $"Remove-ItemProperty {Shell.Quote(ik)} -Name TcpAckFrequency,TCPNoDelay -ErrorAction SilentlyContinue");
            }
            else if (Options.Any(o => o.Keyword == k))
                lines.Add($"Set-NetAdapterAdvancedProperty -Name {Shell.Quote(nic.Name)} -RegistryKeyword {Shell.Quote(k)} -RegistryValue {v} -NoRestart");
        }
        if (values.Keys.Any(k => k != NagleKey))
        {
            lines.Add($"Restart-NetAdapter -Name {Shell.Quote(nic.Name)} -Confirm:$false");
            // espera que a placa volte e tenha rota para a internet (até ~30 s), senão a leitura seguinte não a encontra
            lines.Add($"for ($i = 0; $i -lt 60; $i++) {{ if ((Get-NetAdapter -Name {Shell.Quote(nic.Name)}).Status -eq 'Up' -and (Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue)) {{ break }}; Start-Sleep -Milliseconds 500 }}");
        }
        var r = await Shell.PowerShellAsync(string.Join("\n", lines), 120_000);
        if (!r.Ok) throw new InvalidOperationException(Shell.CleanError(r));
    }
}
