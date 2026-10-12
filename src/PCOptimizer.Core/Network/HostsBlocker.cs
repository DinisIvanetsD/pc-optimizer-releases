using System.Text;
using System.Text.RegularExpressions;
using PCOptimizer.Core.History;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Network;

/// <summary>
/// Bloqueia anúncios e rastreadores em todo o PC pelo ficheiro hosts, com as listas AdAway + Peter Lowe (~10 mil domínios).
/// O bloco da app fica entre duas marcas: desligar tira só esse bloco e o resto do ficheiro fica como estava.
/// </summary>
public static class HostsBlocker
{
    // a StevenBlack (70 mil+ domínios) tornava o serviço DNS do Windows lento a abrir apps (Discord, browsers)
    public static readonly string[] ListUrls =
    {
        "https://adaway.org/hosts.txt",
        "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=hosts&showintro=0&mimetype=plaintext",
    };

    /// <summary>Preenchido por EnableAsync quando uma das listas não descarregou (a outra foi aplicada na mesma).</summary>
    public static string? LastWarning { get; private set; }

    /// <summary>Acima disto é a lista antiga: pesada demais para o Windows, convém atualizar.</summary>
    public const int TooMany = 30_000;
    const string Begin = "# >>> PC Optimizer: bloqueio de anúncios (não editar entre estas marcas)";
    const string End = "# <<< PC Optimizer";
    static readonly Regex Entry = new(@"^(?:0\.0\.0\.0|127\.0\.0\.1)\s+([a-z0-9][a-z0-9._-]*\.[a-z0-9-]+)\s*(#.*)?$", RegexOptions.IgnoreCase);

    /// <summary>PCOPT_HOSTS_FILE permite testar sem mexer no hosts verdadeiro.</summary>
    public static string HostsPath => Environment.GetEnvironmentVariable("PCOPT_HOSTS_FILE") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public static bool IsEnabled() => File.Exists(HostsPath) && File.ReadAllText(HostsPath).Contains(Begin);

    /// <summary>Quantos domínios estão bloqueados pela app (0 = desligado).</summary>
    public static int BlockedCount() =>
        !File.Exists(HostsPath) ? 0 : Block(File.ReadAllText(HostsPath)).Count(l => l.StartsWith("0.0.0.0 "));

    static IEnumerable<string> Block(string text)
    {
        var inside = false;
        foreach (var line in text.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l == Begin) { inside = true; continue; }
            if (l == End) { inside = false; continue; }
            if (inside) yield return l;
        }
    }

    /// <summary>O hosts sem o bloco da app (o resto fica igual).</summary>
    public static string Strip(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        var a = lines.IndexOf(Begin);
        if (a < 0) return text;
        var b = lines.IndexOf(End, a);
        lines.RemoveRange(a, (b < 0 ? lines.Count - 1 : b) - a + 1);
        if (a > 0 && lines[a - 1].Length == 0) lines.RemoveAt(a - 1); // a linha vazia que pusemos antes do bloco
        return string.Join("\r\n", lines);
    }

    // nomes locais que as listas trazem no topo: bloqueá-los partia o próprio PC
    static readonly HashSet<string> NotDomains = new(StringComparer.OrdinalIgnoreCase) { "0.0.0.0", "localhost.localdomain", "local", "broadcasthost", "ip6-localhost", "ip6-loopback" };

    /// <summary>Só as entradas "0.0.0.0 domínio" da lista (sem localhost nem linhas estranhas).</summary>
    public static List<string> ParseList(string list) =>
        list.Split('\n').Select(l => Entry.Match(l.Trim()))
            .Where(m => m.Success && !NotDomains.Contains(m.Groups[1].Value))
            .Select(m => "0.0.0.0 " + m.Groups[1].Value.ToLowerInvariant())
            .Distinct().ToList();

    public static string AddBlock(string hosts, IReadOnlyCollection<string> entries)
    {
        var sb = new StringBuilder(Strip(hosts).TrimEnd('\r', '\n'));
        sb.Append("\r\n\r\n").Append(Begin).Append("\r\n");
        foreach (var e in entries) sb.Append(e).Append("\r\n");
        sb.Append(End).Append("\r\n");
        return sb.ToString();
    }

    public static async Task<int> EnableAsync()
    {
        Simulation.Guard(Loc.T("bloquear anúncios no ficheiro hosts"));
        Admin.Require();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        // uma lista em baixo não estraga a outra (cada uma chega para bloquear o essencial)
        var lists = await Task.WhenAll(ListUrls.Select(async u =>
        {
            try { return await http.GetStringAsync(u); }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return ""; }
        }));
        LastWarning = lists.Any(l => l.Length == 0) ? Loc.T("Só uma das listas descarregou, por isso o bloqueio ficou incompleto. Carrega em Atualizar lista mais tarde.") : null;
        var entries = ParseList(string.Join("\n", lists));
        if (entries.Count < 1000) throw new InvalidOperationException(Loc.T("A lista de bloqueio veio vazia ou incompleta. Tenta mais tarde."));
        await WriteAsync(AddBlock(await File.ReadAllTextAsync(HostsPath), entries));
        ChangeLog.MarkUndone(UndoKind.Hosts, "hosts", "1"); // trocar de lista substitui a entrada antiga no histórico
        ChangeLog.Record(ChangeKind.Network, Loc.T($"Anúncios bloqueados ({entries.Count:N0} domínios)"), Loc.T("Ficheiro hosts · listas AdAway + Peter Lowe"), UndoKind.Hosts,
            new() { ["hosts"] = "1" });
        return entries.Count;
    }

    public static async Task<bool> DisableAsync()
    {
        Simulation.Guard(Loc.T("desligar o bloqueio de anúncios"));
        Admin.Require();
        if (!IsEnabled()) return false;
        await WriteAsync(Strip(await File.ReadAllTextAsync(HostsPath)).TrimEnd('\r', '\n') + "\r\n");
        ChangeLog.MarkUndone(UndoKind.Hosts, "hosts", "1");
        return true;
    }

    static async Task WriteAsync(string text)
    {
        // o antivírus às vezes tem o ficheiro aberto por um instante
        for (var i = 0; ; i++)
        {
            try { await File.WriteAllTextAsync(HostsPath, text, new UTF8Encoding(false)); break; }
            catch (IOException) when (i < 4) { await Task.Delay(500); }
        }
        if (Environment.GetEnvironmentVariable("PCOPT_HOSTS_FILE") is null) await Shell.RunAsync("ipconfig", "/flushdns", 15_000);
    }
}
