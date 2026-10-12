using System.Text.Json;
using PCOptimizer.Core.Util;
using PCOptimizer.Core.History;

namespace PCOptimizer.Core.Optimization;

public enum Risk { Low, Medium, High }

/// <summary>
/// Um ajuste ao sistema. Todos os ajustes sabem: verificar se estão aplicados, guardar o estado
/// atual, aplicar-se, e voltar ao estado guardado.
/// </summary>
public abstract class Tweak
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public Risk Risk { get; init; } = Risk.Low;
    public bool RequiresAdmin { get; init; } = true;
    public bool RequiresReboot { get; init; }
    public bool DefaultSelected { get; init; } = true;

    /// <summary>Ajustes que mexem na mesma coisa (ex.: planos de energia) partilham o mesmo backup.</summary>
    public string? SharedSnapshotKey { get; init; }
    public string SnapshotKey => SharedSnapshotKey ?? Id;

    /// <summary>true = aplicado, false = não aplicado, null = não se aplica a este PC.</summary>
    public abstract Task<bool?> IsAppliedAsync();
    public abstract Task<Dictionary<string, string?>> CaptureAsync();
    public abstract Task ApplyAsync();
    public abstract Task RevertAsync(Dictionary<string, string?> state);
}

/// <summary>Guarda em disco o estado original de cada ajuste antes de o aplicar.</summary>
public static class SnapshotStore
{
    public sealed class Entry
    {
        public DateTime AppliedAt { get; set; }
        public Dictionary<string, string?> State { get; set; } = new();
    }

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCOptimizer", "snapshots.json");
    static readonly object Lock = new();
    static Dictionary<string, Entry>? _cache;

    static Dictionary<string, Entry> Load()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(FilePath))
                _cache = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath));
        }
        catch { }
        return _cache ??= new Dictionary<string, Entry>();
    }

    static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool Has(string key)
    {
        lock (Lock) return Load().ContainsKey(key);
    }

    public static Dictionary<string, string?>? Get(string key)
    {
        lock (Lock) return Load().TryGetValue(key, out var e) ? e.State : null;
    }

    /// <summary>Só guarda se ainda não existir: assim mantemos sempre o estado ORIGINAL do PC.</summary>
    public static void PutIfMissing(string key, Dictionary<string, string?> state)
    {
        lock (Lock)
        {
            var d = Load();
            if (d.ContainsKey(key)) return;
            d[key] = new Entry { AppliedAt = DateTime.Now, State = state };
            Save();
        }
    }

    public static void Remove(string key)
    {
        lock (Lock)
        {
            if (Load().Remove(key)) Save();
        }
    }

    public static IReadOnlyList<string> Keys
    {
        get { lock (Lock) return Load().Keys.ToList(); }
    }
}

public static class TweakEngine
{
    public static bool CanRevert(Tweak t) => SnapshotStore.Has(t.SnapshotKey);

    public static async Task ApplyAsync(Tweak t)
    {
        Simulation.Guard($"aplicar o ajuste \"{t.Name}\"");
        if (t.RequiresAdmin) Admin.Require();
        if (!SnapshotStore.Has(t.SnapshotKey))
            SnapshotStore.PutIfMissing(t.SnapshotKey, await t.CaptureAsync());
        await t.ApplyAsync();
        ChangeLog.Record(ChangeKind.Tweak, Loc.T($"Ajuste aplicado: {t.Name}"), t.Description, UndoKind.Tweak,
            new() { ["id"] = t.Id });
    }

    public static async Task<bool> RevertAsync(Tweak t)
    {
        Simulation.Guard($"reverter o ajuste \"{t.Name}\"");
        var state = SnapshotStore.Get(t.SnapshotKey);
        if (state is null) return false;
        if (t.RequiresAdmin) Admin.Require();
        await t.RevertAsync(state);
        SnapshotStore.Remove(t.SnapshotKey);
        ChangeLog.MarkUndone(UndoKind.Tweak, "id", t.Id);
        return true;
    }

    /// <summary>Reverte todos os ajustes que têm backup, pela ordem inversa do catálogo.</summary>
    public static async Task<(int Ok, List<string> Errors)> RevertAllAsync()
    {
        var ok = 0;
        var errors = new List<string>();
        var done = new HashSet<string>();
        foreach (var t in TweakCatalog.All.Reverse())
        {
            if (!CanRevert(t) || !done.Add(t.SnapshotKey)) continue;
            try
            {
                if (await RevertAsync(t)) ok++;
            }
            catch (Exception ex)
            {
                errors.Add($"{t.Name}: {ex.Message}");
            }
        }
        return (ok, errors);
    }

    public static async Task<string> CreateRestorePointAsync(string description)
    {
        if (Simulation.Enabled) return Loc.T("🧪 Simulação: o ponto de restauro não foi criado.");
        var r = await Shell.PowerShellAsync(
            $"Checkpoint-Computer -Description {Shell.Quote(description)} -RestorePointType MODIFY_SETTINGS -ErrorAction Stop", 300_000);
        return r.Ok
            ? Loc.T("Ponto de restauro criado.")
            : Loc.T("Não foi possível criar o ponto de restauro (o Restauro do Sistema pode estar desligado, ou já foi criado um nas últimas 24 h). Os ajustes continuam reversíveis pela app.");
    }
}
