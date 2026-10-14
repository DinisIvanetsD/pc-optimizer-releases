using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCOptimizer.Core.Settings;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Gaming;

/// <summary>O que o modo jogo mudou, para repor no fim (fica em disco: sobrevive a um crash ou a um reinício).</summary>
public sealed class GameSessionState
{
    public string Game { get; set; } = "";
    public string Exe { get; set; } = "";
    public DateTime Started { get; set; }
    public string? PreviousPowerScheme { get; set; }
    public bool QosAdded { get; set; }
    public List<string> StoppedServices { get; set; } = new();
    /// <summary>Valores da pausa do Windows Update antes da sessão (null = não existia). Null = não mexemos.</summary>
    public Dictionary<string, string?>? PreviousPause { get; set; }
    public List<string> ClosedApps { get; set; } = new(); // caminhos completos, para voltar a abrir
    /// <summary>Torrents suspensos: PID + hora de arranque (para não retomar outro processo com o mesmo PID).</summary>
    public List<SuspendedProcess> SuspendedApps { get; set; } = new();
    public bool GamePriorityRaised { get; set; }
}

public sealed record SuspendedProcess(int Pid, long StartTicks);

/// <summary>
/// Otimizações só enquanto se joga: prioridade de rede para o jogo, plano de energia máximo,
/// Windows Update/Otimização da Entrega em pausa e apps de sincronização fechadas.
/// Tudo é reposto quando o jogo fecha. (O Steam já não descarrega enquanto jogas, por defeito.)
/// </summary>
public static class GameSession
{
    const string QosPrefix = "PCOptSession-";
    const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    // BITS e Otimização da Entrega fazem os downloads; o wuauserv não vale a pena parar (reinicia-se sozinho)
    static readonly string[] UpdateServices = { "BITS", "DoSvc" };
    const string PauseKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    static readonly string[] PauseValues =
    {
        "PauseUpdatesStartTime", "PauseUpdatesExpiryTime", "PauseFeatureUpdatesStartTime",
        "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime",
    };
    static readonly (string Process, string Label)[] SyncApps =
    {
        ("OneDrive", "OneDrive"), ("Dropbox", "Dropbox"), ("GoogleDriveFS", "Google Drive"), ("MEGAsync", "MEGA"),
    };
    // torrents enchem a upload e a download: são a causa nº 1 de ping alto em casa. Suspensos, não fechados.
    static readonly (string Process, string Label)[] TorrentApps =
    {
        ("qbittorrent", "qBittorrent"), ("utorrent", "µTorrent"), ("utweb", "µTorrent Web"), ("BitTorrent", "BitTorrent"),
        ("deluge", "Deluge"), ("transmission-qt", "Transmission"), ("tixati", "Tixati"), ("fdm", "Free Download Manager"),
    };
    // planos que já são "máximo": o Desempenho Máximo do Windows e a cópia que a app cria
    static readonly string[] MaxSchemes = { HighPerformance, "e9a42b02-d5df-448d-aa00-03f14749eb61", "3ff9831b-6f80-4830-8178-736cd4229e7b" };

    [System.Runtime.InteropServices.DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr handle);
    [System.Runtime.InteropServices.DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr handle);

    static string StateFile => Environment.GetEnvironmentVariable("PCOPT_GAMESESSION_FILE")
                               ?? Path.Combine(AppSettings.DataFolder, "game-session.json");

    public static GameSessionState? Active
    {
        get
        {
            try { return File.Exists(StateFile) ? JsonSerializer.Deserialize<GameSessionState>(File.ReadAllText(StateFile)) : null; }
            catch { return null; }
        }
    }

    static void Save(GameSessionState s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(s));
    }

    /// <summary>Aplica as otimizações. Devolve o que foi feito, em texto, para mostrar.</summary>
    public static async Task<List<string>> StartAsync(RunningGame game, bool pauseUpdates)
    {
        Simulation.Guard(Loc.T($"otimizar o PC para {game.Game.Name}"));
        if (Active != null) await RestoreAsync(); // sessão anterior que ficou por repor
        var state = new GameSessionState { Game = game.Game.Name, Exe = game.Game.Exe, Started = DateTime.Now };
        var done = new List<string>();
        Save(state); // grava logo: se algo falhar a meio, o que já mudou é reposto

        // plano de energia máximo (não precisa de administrador)
        var current = await ActiveSchemeAsync();
        if (current != null && !MaxSchemes.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            var r = await Shell.RunAsync("powercfg.exe", "/setactive SCHEME_MIN");
            if (r.ExitCode == 0)
            {
                state.PreviousPowerScheme = current;
                done.Add(Loc.T("Plano de energia: desempenho máximo"));
            }
        }
        Save(state);

        // prioridade alta de CPU para o jogo: a thread de rede do jogo não espera por apps de fundo
        try
        {
            using var gp = Process.GetProcessById(game.Pid);
            if (gp.PriorityClass is ProcessPriorityClass.Normal or ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Idle)
            {
                gp.PriorityClass = ProcessPriorityClass.High;
                state.GamePriorityRaised = true;
                done.Add(Loc.T("Jogo com prioridade alta"));
            }
        }
        catch { } // jogos com anti-cheat podem recusar
        Save(state);

        // torrents/gestores de downloads em pausa: libertam a ligação toda para o jogo
        var paused = new List<string>();
        foreach (var (process, label) in TorrentApps)
            foreach (var p in Process.GetProcessesByName(process))
                using (p)
                {
                    try
                    {
                        var entry = new SuspendedProcess(p.Id, p.StartTime.Ticks);
                        if (NtSuspendProcess(p.Handle) != 0) continue;
                        state.SuspendedApps.Add(entry);
                        Save(state);
                        if (!paused.Contains(label)) paused.Add(label);
                    }
                    catch { }
                }
        if (paused.Count > 0) done.Add(Loc.T($"Downloads em pausa ({string.Join(", ", paused)})"));

        if (Admin.IsElevated)
        {
            // prioridade de rede (QoS/DSCP 46) só para o executável do jogo
            var qos = await Shell.PowerShellAsync($"""
                $ErrorActionPreference = 'Stop'
                Remove-NetQosPolicy -Name {Shell.Quote(QosPrefix + game.Game.Exe)} -Confirm:$false -ErrorAction SilentlyContinue
                New-NetQosPolicy -Name {Shell.Quote(QosPrefix + game.Game.Exe)} -AppPathNameMatchCondition {Shell.Quote(game.Game.Exe)} -DSCPAction 46 -NetworkProfile All | Out-Null
                New-Item -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\QoS' -Force | Out-Null
                Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\QoS' -Name 'Do not use NLA' -Value '1'
                """);
            if (qos.Ok)
            {
                state.QosAdded = true;
                done.Add(Loc.T("Prioridade de rede para o jogo"));
            }
            Save(state);

            if (pauseUpdates)
            {
                // a pausa oficial do Windows Update (a mesma de Definições › Windows Update › Pausar),
                // com fim em 4 h: mesmo que a app ou o PC vão abaixo, a pausa acaba sozinha
                try
                {
                    using var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(PauseKey);
                    state.PreviousPause = PauseValues.ToDictionary(v => v, v => k.GetValue(v) as string);
                    Save(state);
                    static string Iso(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
                    var (start, end) = (Iso(DateTime.Now), Iso(DateTime.Now.AddHours(4)));
                    foreach (var v in PauseValues) k.SetValue(v, v.Contains("Start") ? start : end);
                }
                catch { state.PreviousPause = null; }
                foreach (var svc in UpdateServices)
                {
                    var running = await Shell.PowerShellAsync($"(Get-Service {svc} -ErrorAction SilentlyContinue).Status");
                    if (running.StdOut.Trim() != "Running") continue;
                    var stop = await Shell.PowerShellAsync($"Stop-Service {svc} -Force -ErrorAction Stop");
                    if (stop.Ok) state.StoppedServices.Add(svc);
                }
                if (state.StoppedServices.Count > 0 || state.PreviousPause != null) done.Add(Loc.T("Windows Update e downloads do sistema em pausa"));
                Save(state);
            }
        }

        if (pauseUpdates)
        {
            var closed = new List<string>();
            foreach (var (process, label) in SyncApps)
                foreach (var p in Process.GetProcessesByName(process))
                    using (p)
                    {
                        try
                        {
                            var path = p.MainModule?.FileName;
                            p.Kill();
                            if (path != null && !state.ClosedApps.Contains(path)) state.ClosedApps.Add(path);
                            if (!closed.Contains(label)) closed.Add(label);
                        }
                        catch { }
                    }
            if (closed.Count > 0) done.Add(Loc.T($"Sincronização em pausa ({string.Join(", ", closed)})"));
            Save(state);
        }
        return done;
    }

    /// <summary>Repõe tudo o que a sessão mudou (e apaga o estado). Seguro de chamar várias vezes.</summary>
    public static async Task RestoreAsync()
    {
        if (Active is not GameSessionState s) return;

        if (s.PreviousPowerScheme is string scheme && Regex.IsMatch(scheme, "^[0-9a-fA-F-]{36}$"))
            await Shell.RunAsync("powercfg.exe", $"/setactive {scheme}");

        foreach (var sp in s.SuspendedApps)
            try
            {
                using var p = Process.GetProcessById(sp.Pid);
                if (p.StartTime.Ticks == sp.StartTicks) NtResumeProcess(p.Handle);
            }
            catch { } // já fechou
        s.SuspendedApps.Clear();

        if (s.GamePriorityRaised)
            try
            {
                using var gp = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(s.Exe)).FirstOrDefault();
                if (gp != null) gp.PriorityClass = ProcessPriorityClass.Normal;
            }
            catch { }

        if (Admin.IsElevated)
        {
            if (s.QosAdded)
                await Shell.PowerShellAsync($"Remove-NetQosPolicy -Name {Shell.Quote(QosPrefix + s.Exe)} -Confirm:$false -ErrorAction SilentlyContinue");
            if (s.PreviousPause != null)
                try
                {
                    using var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(PauseKey);
                    foreach (var v in PauseValues)
                        if (s.PreviousPause.GetValueOrDefault(v) is string old) k.SetValue(v, old);
                        else k.DeleteValue(v, false);
                }
                catch { }
            // "wuauserv": sessões gravadas por versões antigas
            foreach (var svc in s.StoppedServices.Where(x => UpdateServices.Contains(x) || x == "wuauserv"))
                await Shell.PowerShellAsync($"Start-Service {svc} -ErrorAction SilentlyContinue");
        }

        foreach (var app in s.ClosedApps.Where(File.Exists))
            try
            {
                var args = Path.GetFileNameWithoutExtension(app).Equals("OneDrive", StringComparison.OrdinalIgnoreCase) ? "/background" : "";
                Process.Start(new ProcessStartInfo(app, args) { UseShellExecute = true })?.Dispose();
            }
            catch { }

        // sem administrador não deu para repor o QoS/serviços: fica o estado para a próxima vez com administrador
        if (!Admin.IsElevated && (s.QosAdded || s.StoppedServices.Count > 0 || s.PreviousPause != null))
        {
            s.PreviousPowerScheme = null;
            s.ClosedApps.Clear();
            Save(s);
            return;
        }
        try { File.Delete(StateFile); } catch { }
    }

    public static bool IsRunning(string exe)
    {
        var ps = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }

    static async Task<string?> ActiveSchemeAsync()
    {
        var r = await Shell.RunAsync("powercfg.exe", "/getactivescheme");
        var m = Regex.Match(r.StdOut, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return m.Success ? m.Value : null;
    }
}
