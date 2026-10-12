using System.Diagnostics;
using System.Text.RegularExpressions;
using PCOptimizer.Core.Settings;

namespace PCOptimizer.Core.Gaming;

/// <summary>Um jogo online conhecido (ou acrescentado pelo utilizador), identificado pelo executável.</summary>
public sealed record GameInfo(string Name, string Exe);

/// <summary>Um jogo que está a correr agora.</summary>
public sealed record RunningGame(GameInfo Game, int Pid, string? Path);

/// <summary>
/// Os jogos online mais jogados no PC, pelo nome do executável do jogo propriamente dito
/// (não o do launcher). O utilizador pode acrescentar outros nas Definições.
/// </summary>
public static class GameCatalog
{
    public static readonly IReadOnlyList<GameInfo> Known = new GameInfo[]
    {
        new("Counter-Strike 2", "cs2.exe"),
        new("VALORANT", "VALORANT-Win64-Shipping.exe"),
        new("Fortnite", "FortniteClient-Win64-Shipping.exe"),
        new("League of Legends", "League of Legends.exe"),
        new("Dota 2", "dota2.exe"),
        new("Apex Legends", "r5apex.exe"),
        new("Apex Legends", "r5apex_dx12.exe"),
        new("Call of Duty / Warzone", "cod.exe"),
        new("Overwatch 2", "Overwatch.exe"),
        new("Rainbow Six Siege", "RainbowSix.exe"),
        new("Rainbow Six Siege", "RainbowSix_Vulkan.exe"),
        new("Rocket League", "RocketLeague.exe"),
        new("PUBG: Battlegrounds", "TslGame.exe"),
        new("Grand Theft Auto V", "GTA5.exe"),
        new("Grand Theft Auto V Enhanced", "GTA5_Enhanced.exe"),
        new("Roblox", "RobloxPlayerBeta.exe"),
        new("Minecraft (Bedrock)", "Minecraft.Windows.exe"),
        new("Rust", "RustClient.exe"),
        new("Escape from Tarkov", "EscapeFromTarkov.exe"),
        new("Destiny 2", "destiny2.exe"),
        new("Dead by Daylight", "DeadByDaylight-Win64-Shipping.exe"),
        new("Marvel Rivals", "Marvel-Win64-Shipping.exe"),
        new("THE FINALS", "Discovery.exe"),
        new("Deadlock", "project8.exe"),
        new("Helldivers 2", "helldivers2.exe"),
        new("World of Warcraft", "Wow.exe"),
        new("Lost Ark", "LOSTARK.exe"),
        new("Path of Exile", "PathOfExile.exe"),
        new("Path of Exile", "PathOfExileSteam.exe"),
        new("Warframe", "Warframe.x64.exe"),
        new("Battlefield 2042", "BF2042.exe"),
        new("Genshin Impact", "GenshinImpact.exe"),
        new("Hearthstone", "Hearthstone.exe"),
        new("Team Fortress 2", "tf_win64.exe"),
        new("Forza Horizon 5", "ForzaHorizon5.exe"),
    };

    public static bool IsValidExe(string exe) => Regex.IsMatch(exe, @"^[\w\-. ]+\.exe$", RegexOptions.IgnoreCase);

    /// <summary>Conhecidos + os acrescentados pelo utilizador.</summary>
    public static IEnumerable<GameInfo> All(AppSettings s) =>
        Known.Concat(s.CustomGames.Where(IsValidExe).Select(e => new GameInfo(Path.GetFileNameWithoutExtension(e), e)));

    /// <summary>Encontra na lista de processos o primeiro jogo conhecido.</summary>
    public static RunningGame? Find(IEnumerable<(int Pid, string Name, string? Path)> processes, IEnumerable<GameInfo> games)
    {
        var byProcess = games
            .GroupBy(g => Path.GetFileNameWithoutExtension(g.Exe), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, name, path) in processes)
            if (byProcess.TryGetValue(name, out var game))
                return new RunningGame(game, pid, path);
        return null;
    }

    /// <summary>Todos os jogos conhecidos que estão a correr.</summary>
    public static List<RunningGame> FindAll(IEnumerable<(int Pid, string Name, string? Path)> processes, IEnumerable<GameInfo> games)
    {
        var byProcess = games
            .GroupBy(g => Path.GetFileNameWithoutExtension(g.Exe), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return processes.Where(p => byProcess.ContainsKey(p.Name)).Select(p => new RunningGame(byProcess[p.Name], p.Pid, p.Path)).ToList();
    }

    /// <summary>
    /// Com vários jogos abertos (ex.: o Roblox esquecido no menu), o que conta é o que está em primeiro plano;
    /// se nenhum estiver, o que foi aberto mais recentemente.
    /// </summary>
    public static RunningGame? Choose(IReadOnlyList<RunningGame> running, int foregroundPid, Func<int, DateTime> started) =>
        running.FirstOrDefault(g => g.Pid == foregroundPid) ?? running.MaxBy(g => started(g.Pid));

    /// <summary>O jogo aberto agora (null = nenhum).</summary>
    public static RunningGame? FindRunning(AppSettings s)
    {
        var list = new List<(int, string, string?)>();
        foreach (var p in Process.GetProcesses())
            using (p) list.Add((p.Id, p.ProcessName, null));
        var running = FindAll(list, All(s));
        if (running.Count == 0) return null;
        GetWindowThreadProcessId(GetForegroundWindow(), out var fg);
        if (Choose(running, (int)fg, StartTime) is not RunningGame found) return null;
        // o caminho só para o jogo encontrado (ler o de todos os processos é lento)
        try
        {
            using var p = Process.GetProcessById(found.Pid);
            return found with { Path = p.MainModule?.FileName };
        }
        catch { return found; }
    }

    static DateTime StartTime(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime;
        }
        catch { return DateTime.MinValue; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
