using System.Text.Json;
using PCOptimizer.Core.Apps;
using PCOptimizer.Core.Benchmark;
using PCOptimizer.Core.Memory;
using PCOptimizer.Core.Network;
using PCOptimizer.Core.Optimization;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.History;

public enum ChangeKind { Tweak, Startup, Network, Memory, Files, Uninstall, Driver, Cleanup, Install, Recording }

/// <summary>Como desfazer uma alteração. None = não dá (ex.: desinstalar, esvaziar a Reciclagem).</summary>
public enum UndoKind { None, Tweak, Startup, Dns, Qos, BadMemory, RecycleBin, RestorePoint, Nic, AppInstall, Hosts, PerfLogUsers }

public sealed class ChangeEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime When { get; init; } = DateTime.Now;
    public ChangeKind Kind { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public UndoKind Undo { get; init; }
    public Dictionary<string, string> Data { get; init; } = new();
    public bool Undone { get; set; }
    public DateTime? UndoneAt { get; set; }

    public bool CanUndo => Undo != UndoKind.None && !Undone;
}

/// <summary>
/// Histórico de tudo o que a app mudou no PC, guardado em %LOCALAPPDATA%\PCOptimizer\history.json.
/// Cada entrada sabe como se desfaz (quando é possível).
/// </summary>
public static class ChangeLog
{
    static readonly object Gate = new();
    static List<ChangeEntry>? _entries;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>PCOPT_HISTORY_FILE permite testar com um histórico à parte (não mexe no verdadeiro).</summary>
    public static string FilePath => Environment.GetEnvironmentVariable("PCOPT_HISTORY_FILE") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCOptimizer", "history.json");

    /// <summary>Avisa a interface quando há uma entrada nova ou desfeita.</summary>
    public static event Action? Changed;

    /// <summary>Em modo simulação nada é gravado (nem executado).</summary>
    public static bool Disabled { get; set; }

    public static IReadOnlyList<ChangeEntry> All
    {
        get { lock (Gate) return Load().OrderByDescending(e => e.When).ToList(); }
    }

    public static ChangeEntry Record(ChangeKind kind, string title, string detail = "", UndoKind undo = UndoKind.None,
        Dictionary<string, string>? data = null)
    {
        var e = new ChangeEntry { Kind = kind, Title = title, Detail = detail, Undo = undo, Data = data ?? new() };
        if (Disabled) return e;
        lock (Gate)
        {
            Load().Add(e);
            Save();
        }
        Changed?.Invoke();
        return e;
    }

    /// <summary>Marca como desfeitas as entradas antigas do mesmo ajuste (quando é revertido por outro caminho).</summary>
    public static void MarkUndone(UndoKind undo, string key, string value)
    {
        lock (Gate)
        {
            var any = false;
            foreach (var e in Load().Where(e => e.Undo == undo && !e.Undone && e.Data.GetValueOrDefault(key) == value))
            {
                e.Undone = true;
                e.UndoneAt = DateTime.Now;
                any = true;
            }
            if (!any) return;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Desfaz uma alteração. Devolve a mensagem para o utilizador.</summary>
    public static async Task<string> UndoAsync(ChangeEntry e)
    {
        Simulation.Guard($"desfazer \"{e.Title}\"");
        if (!e.CanUndo) return Loc.T("Isto não dá para desfazer.");
        string msg;
        switch (e.Undo)
        {
            case UndoKind.Tweak:
                var tweak = TweakCatalog.Get(e.Data["id"]);
                msg = await TweakEngine.RevertAsync(tweak) ? $"✓ Revertido: {tweak.Name}" : Loc.T("Já não havia backup deste ajuste (talvez já tenha sido revertido).");
                break;

            case UndoKind.Startup:
                var item = new StartupItem
                {
                    Name = e.Data["name"], Command = "", Location = "",
                    ApprovedKey = e.Data["key"], ValueName = e.Data["value"], RequiresAdmin = e.Data["admin"] == "1",
                };
                var wasEnabled = e.Data["before"] == "1";
                StartupManager.SetEnabled(item, wasEnabled, record: false);
                msg = Loc.T($"✓ {item.Name} voltou a {(wasEnabled ? "arrancar" : Loc.T("não arrancar"))} com o Windows.");
                break;

            case UndoKind.Dns:
                msg = await DnsChanger.RevertAsync() ? Loc.T("✓ DNS reposto como estava.") : Loc.T("Já não havia backup do DNS.");
                break;

            case UndoKind.Qos:
                await GameQos.RemoveAsync(e.Data["exe"]);
                msg = Loc.T($"✓ Prioridade de rede removida de {e.Data["exe"]}.");
                break;

            case UndoKind.Nic:
                msg = await NicTuning.RevertAsync(e.Data["guid"]) ? Loc.T("✓ Placa de rede reposta como estava.") : Loc.T("Já não havia backup da placa de rede.");
                break;

            case UndoKind.AppInstall:
                await Winget.UninstallAsync(e.Data["id"]);
                msg = Loc.T($"✓ {e.Data["name"]} desinstalada.");
                break;

            case UndoKind.Hosts:
                msg = await HostsBlocker.DisableAsync() ? Loc.T("✓ Bloqueio de anúncios desligado.") : Loc.T("O bloqueio já estava desligado.");
                break;

            case UndoKind.PerfLogUsers:
                await PerfLogAccess.DisableAsync(e.Data["user"]);
                msg = Loc.T("✓ Medição de FPS desativada. Termina sessão no Windows para ter efeito.");
                break;

            case UndoKind.BadMemory:
                msg = await BadMemoryList.RevertAsync() ? Loc.T("✓ Lista de memória isolada reposta. Reinicia o PC.") : Loc.T("Já não havia backup.");
                break;

            case UndoKind.RecycleBin:
                var since = DateTime.Parse(e.Data["since"], null, System.Globalization.DateTimeStyles.RoundtripKind);
                var (restored, missing) = RecycleBin.Restore(e.Data["folder"], since);
                msg = restored > 0
                    ? Loc.T($"✓ {restored} item(ns) recuperado(s) da Reciclagem.") + (missing > 0 ? Loc.T($" {missing} já não estavam lá.") : "")
                    : Loc.T("Não encontrei estes ficheiros na Reciclagem (talvez já tenha sido esvaziada).");
                if (restored == 0) return msg; // não marca como desfeito
                break;

            case UndoKind.RestorePoint:
                Shell.Open("rstrui.exe");
                return Loc.T("Abri o Restauro do Sistema: escolhe o ponto \"Rigwise\" criado antes desta alteração.");

            default:
                return Loc.T("Isto não dá para desfazer.");
        }

        lock (Gate)
        {
            e.Undone = true;
            e.UndoneAt = DateTime.Now;
            var stored = Load().FirstOrDefault(x => x.Id == e.Id);
            if (stored != null && !ReferenceEquals(stored, e)) { stored.Undone = true; stored.UndoneAt = e.UndoneAt; }
            Save();
        }
        Changed?.Invoke();
        return msg;
    }

    static List<ChangeEntry> Load()
    {
        if (_entries != null) return _entries;
        try
        {
            _entries = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<ChangeEntry>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch
        {
            _entries = new();
        }
        return _entries;
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // guarda os últimos 2000 registos
            var keep = _entries!.OrderByDescending(e => e.When).Take(2000).OrderBy(e => e.When).ToList();
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(keep, Json));
            File.Move(tmp, FilePath, true);
        }
        catch { /* o histórico nunca pode impedir a ação */ }
    }
}
