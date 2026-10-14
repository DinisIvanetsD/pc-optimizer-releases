using System.Text.Json;
using Microsoft.Win32;

namespace PCOptimizer.Core.Settings;

/// <summary>Preferências do utilizador, em %LOCALAPPDATA%\PCOptimizer\settings.json.</summary>
public sealed class AppSettings
{
    // geral
    /// <summary>"auto" (língua do Windows), "pt" ou "en". Null = definições de antes da tradução (ver Load).</summary>
    public string? Language { get; set; }
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool Notifications { get; set; } = true;

    // manutenção automática
    public bool AutoMaintenance { get; set; } = true;
    public int MaintenanceDays { get; set; } = 7;
    public bool MaintainDiagnostics { get; set; } = true;
    public bool MaintainDrivers { get; set; } = true;
    public bool MaintainDisk { get; set; } = true;
    public DateTime? LastMaintenance { get; set; }
    /// <summary>"Começar do zero": ecrãs azuis, desligares e crashes só contam a partir daqui (null = últimos 30 dias).</summary>
    public DateTime? StabilitySince { get; set; }
    /// <summary>Último ecrã azul já avisado com notificação.</summary>
    public DateTime? LastCrashNotified { get; set; }

    /// <summary>O que já foi avisado (para não repetir a mesma notificação).</summary>
    public HashSet<string> NotifiedKeys { get; set; } = new();

    // janela
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public bool FirstRunDone { get; set; }

    // segurança

    // modo jogo
    public bool GameMode { get; set; } = true;
    /// <summary>Resposta lembrada por jogo (exe → "always" | "never").</summary>
    public Dictionary<string, string> GameChoices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Jogos acrescentados pelo utilizador (nome do executável).</summary>
    public List<string> CustomGames { get; set; } = new();
    public bool PauseUpdatesWhilePlaying { get; set; } = true;
    public bool OverlayEnabled { get; set; } = true;
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }
    /// <summary>Quando a medição de FPS foi ativada (utilizador no grupo "Performance Log Users"); null = não ativada.</summary>
    public DateTime? FpsEnabledAt { get; set; }

    // atualizações
    public bool CheckUpdates { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    public string? SkippedVersion { get; set; }

    // ------------------------------------------------------------------

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static AppSettings? _current;

    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCOptimizer");
    static string FilePath => Environment.GetEnvironmentVariable("PCOPT_SETTINGS_FILE") ?? Path.Combine(DataFolder, "settings.json");

    public static AppSettings Current => _current ??= Load();

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
                // quem já usava a app (em português) continua em português, mesmo com o Windows noutra língua
                s.Language ??= s.FirstRunDone ? "pt" : "auto";
                return s;
            }
        }
        catch { }
        return new AppSettings { Language = "auto" };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }
}

/// <summary>Arrancar com o Windows (chave Run do utilizador; não precisa de administrador).</summary>
public static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "PCOptimizer";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue(Name, false);
    }
}
