using System.Diagnostics;
using System.Text.RegularExpressions;
using PCOptimizer.Core.Settings;

namespace PCOptimizer.Core.Gaming;

/// <summary>Um jogo online conhecido (ou acrescentado pelo utilizador), identificado pelo executável.</summary>
public sealed record GameInfo(string Name, string Exe);

/// <summary>Um jogo que está a correr agora. Detected = não é um jogo da lista: apanhado por ocupar o ecrã inteiro.</summary>
public sealed record RunningGame(GameInfo Game, int Pid, string? Path, bool Detected = false);

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

    /// <summary>Programas que ocupam o ecrã todo mas não são jogos (browsers, leitores de vídeo, Office, o próprio sistema).</summary>
    public static readonly IReadOnlySet<string> NotGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "explorer",
        "vlc", "mpc-hc64", "mpc-be64", "PotPlayerMini64", "wmplayer", "Video.UI", "Microsoft.Media.Player", "mpv",
        "WINWORD", "EXCEL", "POWERPNT", "Acrobat", "AcroRd32", "PCOptimizer", "obs64", "Discord", "Spotify",
        "ApplicationFrameHost", "LockApp", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost",
    };

    /// <summary>Um programa em primeiro plano que ocupa o monitor todo e não é um dos de <see cref="NotGames"/>.</summary>
    public static bool IsCandidate(string processName, bool coversMonitor) => coversMonitor && !NotGames.Contains(processName);

    /// <summary>
    /// Um jogo conhecido ganha sempre; depois, um novo candidato em ecrã inteiro. Sem nenhum dos dois (ex.: Alt+Tab para
    /// o browser) o jogo detetado antes fica até o processo fechar.
    /// </summary>
    public static RunningGame? Pick(RunningGame? known, RunningGame? foreground, RunningGame? current, Func<int, bool> alive) =>
        known ?? foreground ?? (current is { Detected: true } && alive(current.Pid) ? current : null);

    /// <summary>Sem barra de título (WS_CAPTION = WS_BORDER | WS_DLGFRAME): ecrã inteiro ou sem bordas, não uma janela maximizada.</summary>
    public static bool IsBorderless(long style) => (style & 0x00C00000) != 0x00C00000;

    /// <summary>O jogo aberto agora (null = nenhum). <paramref name="current"/> é o que já se sabia, para o Alt+Tab não o perder.</summary>
    public static RunningGame? FindRunning(AppSettings s, RunningGame? current = null)
    {
        var known = FindKnown(s);
        return Pick(known, known is null ? FindForeground() : null, current, Alive);
    }

    static RunningGame? FindKnown(AppSettings s)
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

    /// <summary>O programa em primeiro plano, se ocupar o monitor todo (jogo em ecrã inteiro ou sem bordas).</summary>
    static RunningGame? FindForeground()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out var pid) == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            var name = p.ProcessName;
            if (!IsCandidate(name, CoversMonitor(hwnd))) return null;
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { } // processos protegidos não deixam ler o caminho
            return new RunningGame(new GameInfo(name, name + ".exe"), p.Id, path, Detected: true);
        }
        catch { return null; }
    }

    static bool CoversMonitor(IntPtr hwnd)
    {
        var mi = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        if (!IsBorderless((uint)GetWindowLong(hwnd, -16))) return false; // -16 = GWL_STYLE; uma janela maximizada com a barra escondida também ocupa o monitor
        if (!GetWindowRect(hwnd, out var w) || !GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi)) return false; // 2 = o monitor mais próximo
        var m = mi.Monitor;
        return w.Left <= m.Left && w.Top <= m.Top && w.Right >= m.Right && w.Bottom >= m.Bottom;
    }

    static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; } // já não existe
        catch { return true; }                       // existe, mas não deixa ler o estado (processo protegido)
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
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    struct Rect { public int Left, Top, Right, Bottom; }
    struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
}
