using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Apps;

/// <summary>Uma app que se instala pelo winget (Id = identificador exato no repositório do winget).</summary>
public sealed record AppEntry(string Id, string Name, string Category);

/// <summary>Apps populares, por categoria, para instalar de uma vez (como o Ninite/Optimizer).</summary>
public static class AppCatalog
{
    static readonly string Browsers = Loc.T("Browsers");
    static readonly string Games = Loc.T("Jogos");
    static readonly string Chat = Loc.T("Comunicação");
    static readonly string Media = Loc.T("Multimédia");
    static readonly string Tools = Loc.T("Ferramentas");
    static readonly string Dev = Loc.T("Programação");

    public static IReadOnlyList<AppEntry> All { get; } = new AppEntry[]
    {
        new("Google.Chrome", "Google Chrome", Browsers),
        new("Mozilla.Firefox", "Firefox", Browsers),
        new("Brave.Brave", "Brave", Browsers),
        new("Opera.OperaGX", "Opera GX", Browsers),
        new("Opera.Opera", "Opera", Browsers),
        new("Vivaldi.Vivaldi", "Vivaldi", Browsers),
        new("LibreWolf.LibreWolf", "LibreWolf", Browsers),

        new("Valve.Steam", "Steam", Games),
        new("EpicGames.EpicGamesLauncher", "Epic Games", Games),
        new("ElectronicArts.EADesktop", "EA app", Games),
        new("Ubisoft.Connect", "Ubisoft Connect", Games),
        new("GOG.Galaxy", "GOG Galaxy", Games),
        new("Blizzard.BattleNet", "Battle.net", Games),
        new("Playnite.Playnite", "Playnite", Games),
        new("Guru3D.Afterburner", "MSI Afterburner", Games),

        new("Discord.Discord", "Discord", Chat),
        new("Telegram.TelegramDesktop", "Telegram", Chat),
        new("OpenWhisperSystems.Signal", "Signal", Chat),
        new("Zoom.Zoom", "Zoom", Chat),
        new("TeamSpeakSystems.TeamSpeakClient", "TeamSpeak", Chat),

        new("VideoLAN.VLC", "VLC", Media),
        new("Spotify.Spotify", "Spotify", Media),
        new("OBSProject.OBSStudio", "OBS Studio", Media),
        new("Audacity.Audacity", "Audacity", Media),
        new("GIMP.GIMP.3", "GIMP", Media),
        new("HandBrake.HandBrake", "HandBrake", Media),
        new("ShareX.ShareX", "ShareX", Media),
        new("Daum.PotPlayer", "PotPlayer", Media),

        new("7zip.7zip", "7-Zip", Tools),
        new("RARLab.WinRAR", "WinRAR", Tools),
        new("Notepad++.Notepad++", "Notepad++", Tools),
        new("voidtools.Everything", "Everything", Tools),
        new("AntibodySoftware.WizTree", "WizTree", Tools),
        new("Microsoft.PowerToys", "PowerToys", Tools),
        new("CPUID.CPU-Z", "CPU-Z", Tools),
        new("TechPowerUp.GPU-Z", "GPU-Z", Tools),
        new("CrystalDewWorld.CrystalDiskInfo", "CrystalDiskInfo", Tools),
        new("REALiX.HWiNFO", "HWiNFO", Tools),
        new("Malwarebytes.Malwarebytes", "Malwarebytes", Tools),
        new("Bitwarden.Bitwarden", "Bitwarden", Tools),
        new("qBittorrent.qBittorrent", "qBittorrent", Tools),
        new("SumatraPDF.SumatraPDF", "SumatraPDF", Tools),
        new("TheDocumentFoundation.LibreOffice", "LibreOffice", Tools),
        new("Google.GoogleDrive", "Google Drive", Tools),

        new("Microsoft.VisualStudioCode", "VS Code", Dev),
        new("Git.Git", "Git", Dev),
        new("Python.Python.3.13", "Python 3", Dev),
        new("OpenJS.NodeJS.LTS", "Node.js LTS", Dev),
        new("Microsoft.WindowsTerminal", "Windows Terminal", Dev),
        new("GitHub.GitHubDesktop", "GitHub Desktop", Dev),
        new("Docker.DockerDesktop", "Docker Desktop", Dev),
        new("PuTTY.PuTTY", "PuTTY", Dev),
        new("WinSCP.WinSCP", "WinSCP", Dev),
    };
}
