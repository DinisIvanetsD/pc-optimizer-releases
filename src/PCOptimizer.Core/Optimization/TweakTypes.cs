using System.Text.RegularExpressions;
using Microsoft.Win32;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Optimization;

public sealed record RegValue(string Key, string Name, object Value, RegistryValueKind Kind);

/// <summary>Ajuste que escreve um ou mais valores no registo.</summary>
public sealed class RegistryTweak : Tweak
{
    public required RegValue[] Values { get; init; }

    static string StateKey(RegValue v) => $"{v.Key}\\{v.Name}";

    public override Task<bool?> IsAppliedAsync()
    {
        try
        {
            return Task.FromResult<bool?>(Values.All(v => Same(Reg.Get(v.Key, v.Name), v.Value)));
        }
        catch
        {
            return Task.FromResult<bool?>(null);
        }
    }

    public override Task<Dictionary<string, string?>> CaptureAsync()
    {
        var state = new Dictionary<string, string?>();
        foreach (var v in Values)
            state[StateKey(v)] = Serialize(Reg.Get(v.Key, v.Name), Reg.GetKind(v.Key, v.Name));
        return Task.FromResult(state);
    }

    public override Task ApplyAsync()
    {
        foreach (var v in Values) Reg.Set(v.Key, v.Name, v.Value, v.Kind);
        return Task.CompletedTask;
    }

    public override Task RevertAsync(Dictionary<string, string?> state)
    {
        foreach (var v in Values)
        {
            if (!state.TryGetValue(StateKey(v), out var saved)) continue;
            if (saved is null)
            {
                Reg.Delete(v.Key, v.Name); // o valor não existia antes
            }
            else
            {
                var (value, kind) = Deserialize(saved);
                Reg.Set(v.Key, v.Name, value, kind);
            }
        }
        return Task.CompletedTask;
    }

    static bool Same(object? current, object desired) => (current, desired) switch
    {
        (int a, int b) => a == b,
        (long a, long b) => a == b,
        (string a, string b) => a == b,
        _ => false,
    };

    static string? Serialize(object? v, RegistryValueKind? kind)
    {
        if (v is null || kind is null) return null;
        return kind switch
        {
            RegistryValueKind.DWord => $"DWord|{unchecked((uint)(int)v)}",
            RegistryValueKind.QWord => $"QWord|{(long)v}",
            RegistryValueKind.Binary => $"Binary|{Convert.ToBase64String((byte[])v)}",
            RegistryValueKind.MultiString => $"MultiString|{string.Join('\u0001', (string[])v)}",
            _ => $"{kind}|{v}",
        };
    }

    static (object Value, RegistryValueKind Kind) Deserialize(string s)
    {
        var i = s.IndexOf('|');
        var kind = Enum.Parse<RegistryValueKind>(s[..i]);
        var data = s[(i + 1)..];
        return kind switch
        {
            RegistryValueKind.DWord => (unchecked((int)uint.Parse(data)), kind),
            RegistryValueKind.QWord => (long.Parse(data), kind),
            RegistryValueKind.Binary => (Convert.FromBase64String(data), kind),
            RegistryValueKind.MultiString => (data.Split('\u0001'), kind),
            _ => (data, kind),
        };
    }
}

/// <summary>Ativa um plano de energia (e cria o "Desempenho Máximo" se não existir).</summary>
public sealed class PowerPlanTweak : Tweak
{
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";
    public const string Ultimate = "ultimate";
    const string UltimateTemplate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    const string OwnUltimate = "3ff9831b-6f80-4830-8178-736cd4229e7b"; // a nossa cópia, com GUID fixo

    static readonly Regex GuidRx = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

    public required string Target { get; init; }

    public static async Task<string?> GetActiveAsync()
    {
        var r = await Shell.RunAsync("powercfg", "/getactivescheme");
        var m = GuidRx.Match(r.StdOut);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    static async Task<HashSet<string>> ListAsync()
    {
        var r = await Shell.RunAsync("powercfg", "/list");
        return GuidRx.Matches(r.StdOut).Select(m => m.Value.ToLowerInvariant()).ToHashSet();
    }

    async Task<string?> ResolveAsync(bool create)
    {
        if (Target != Ultimate) return Target;
        var list = await ListAsync();
        if (list.Contains(OwnUltimate)) return OwnUltimate;
        if (create)
        {
            foreach (var template in new[] { UltimateTemplate, HighPerformance })
            {
                await Shell.RunAsync("powercfg", $"/duplicatescheme {template} {OwnUltimate}");
                if ((await ListAsync()).Contains(OwnUltimate)) return OwnUltimate;
            }
        }
        return list.Contains(HighPerformance) ? HighPerformance : null;
    }

    public override async Task<bool?> IsAppliedAsync()
    {
        var active = await GetActiveAsync();
        var target = await ResolveAsync(create: false);
        return target != null && active == target;
    }

    public override async Task<Dictionary<string, string?>> CaptureAsync() =>
        new() { ["active"] = await GetActiveAsync() };

    public override async Task ApplyAsync()
    {
        var target = await ResolveAsync(create: true)
                     ?? throw new InvalidOperationException(Loc.T("Este PC não permite criar um plano de alto desempenho."));
        var r = await Shell.RunAsync("powercfg", $"/setactive {target}");
        if (!r.Ok) throw new InvalidOperationException(Shell.CleanError(r));
    }

    public override async Task RevertAsync(Dictionary<string, string?> state)
    {
        if (state.GetValueOrDefault("active") is not { } previous) return;
        var r = await Shell.RunAsync("powercfg", $"/setactive {previous}");
        if (!r.Ok) throw new InvalidOperationException(Shell.CleanError(r));
    }
}

/// <summary>Muda uma definição do plano de energia ativo (valor "ligado à corrente").</summary>
public sealed class PowerSettingTweak : Tweak
{
    public required string Subgroup { get; init; }
    public required string Setting { get; init; }
    public required uint AcValue { get; init; }

    static readonly Regex HexRx = new("0x([0-9a-fA-F]{8})");

    async Task<uint?> ReadAcAsync()
    {
        // /qh também mostra definições escondidas (ex.: core parking)
        var r = await Shell.RunAsync("powercfg", $"/qh SCHEME_CURRENT {Subgroup} {Setting}");
        var hex = HexRx.Matches(r.StdOut).Select(m => m.Groups[1].Value).ToList();
        // os dois últimos valores hexadecimais são sempre "AC atual" e "DC atual"
        return hex.Count >= 2 ? Convert.ToUInt32(hex[^2], 16) : null;
    }

    public override async Task<bool?> IsAppliedAsync()
    {
        var ac = await ReadAcAsync();
        return ac is null ? null : ac == AcValue;
    }

    public override async Task<Dictionary<string, string?>> CaptureAsync() =>
        new() { ["ac"] = (await ReadAcAsync())?.ToString() };

    async Task WriteAsync(uint value)
    {
        await Run($"/setacvalueindex SCHEME_CURRENT {Subgroup} {Setting} {value}");
        await Run("/setactive SCHEME_CURRENT");
    }

    static async Task Run(string args)
    {
        var r = await Shell.RunAsync("powercfg", args);
        if (!r.Ok) throw new InvalidOperationException(Shell.CleanError(r));
    }

    public override Task ApplyAsync() => WriteAsync(AcValue);

    public override Task RevertAsync(Dictionary<string, string?> state) =>
        uint.TryParse(state.GetValueOrDefault("ac"), out var v) ? WriteAsync(v) : Task.CompletedTask;
}

/// <summary>
/// Ajuste feito com PowerShell. O script de verificação deve escrever True, False ou NA (não se aplica).
/// O estado capturado fica disponível no script de reversão como $state.
/// </summary>
public sealed class PowerShellTweak : Tweak
{
    public string Prelude { get; init; } = "";
    public required string Check { get; init; }
    public required string Capture { get; init; }
    public required string Apply { get; init; }
    public required string Revert { get; init; }

    public override async Task<bool?> IsAppliedAsync()
    {
        var r = await Shell.PowerShellAsync(Prelude + "\n" + Check);
        var last = r.StdOut.Trim().Split('\n').LastOrDefault()?.Trim();
        return last switch { "True" => true, "False" => false, _ => null };
    }

    public override async Task<Dictionary<string, string?>> CaptureAsync()
    {
        var r = await Shell.PowerShellAsync(Prelude + "\n" + Capture);
        return new() { ["state"] = r.StdOut.Trim() };
    }

    public override async Task ApplyAsync()
    {
        var r = await Shell.PowerShellAsync("$ErrorActionPreference='Stop'\n" + Prelude + "\n" + Apply);
        if (!r.Ok) throw new InvalidOperationException(Shell.CleanError(r));
    }

    public override async Task RevertAsync(Dictionary<string, string?> state)
    {
        var s = state.GetValueOrDefault("state") ?? "";
        var r = await Shell.PowerShellAsync($"$ErrorActionPreference='Stop'\n$state = {Shell.Quote(s)}\n{Prelude}\n{Revert}");
        if (!r.Ok) throw new InvalidOperationException(Shell.CleanError(r));
    }
}
