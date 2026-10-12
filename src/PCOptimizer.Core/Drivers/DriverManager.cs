using System.Management;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCOptimizer.Core.Util;
using PCOptimizer.Core.History;

namespace PCOptimizer.Core.Drivers;

public enum DeviceProblemKind { Missing, Broken, Disabled, NeedsReboot }

/// <summary>Um dispositivo com erro no Gestor de Dispositivos.</summary>
public sealed record ProblemDevice(
    string Name, string InstanceId, string Class, string? HardwareId, uint Code,
    DeviceProblemKind Kind, string Explanation);

/// <summary>Um driver que o Windows Update tem para este PC e ainda não está instalado.</summary>
public sealed record DriverUpdate(string Id, string Title, string Class, string Manufacturer, DateTime? Date, long SizeBytes);

public enum KeyDriverStatus { Ok, Optional, Update, Missing }

/// <summary>Driver de uma peça importante (gráfica, chipset, rede, áudio...) e se vale a pena atualizar.</summary>
public sealed record KeyDriver(
    string Icon, string Category, string Device, string Provider, string Version, DateTime? Date,
    KeyDriverStatus Status, string Advice, string? Url, string UrlLabel)
{
    /// <summary>ID do hardware (para procurar versões novas no Catálogo do Microsoft Update).</summary>
    public string? HardwareId { get; init; }
}

/// <summary>Versão antiga de um driver que ficou guardada no Windows (já há uma mais nova instalada).</summary>
public sealed record OldDriverPackage(
    string PublishedName, string OriginalName, string Provider, string Class,
    string Version, DateTime? Date, string NewerVersion, long SizeBytes);

public sealed record DriverInstallResult(int Installed, int Failed, bool RebootRequired, string Message);

/// <summary>
/// Analisa os drivers do Windows: dispositivos sem driver / com driver estragado, drivers em falta no Windows Update,
/// idade dos drivers importantes e versões antigas duplicadas no armazenamento de drivers.
/// Tudo o que instala vem do Windows Update (assinado pela Microsoft); nada é descarregado de sites desconhecidos.
/// </summary>
public static class DriverManager
{
    // ------------------------------------------------------------------ dispositivos com problemas

    public static List<ProblemDevice> ProblemDevices()
    {
        var list = new List<ProblemDevice>();
        using var s = new ManagementObjectSearcher(
            "SELECT Name, DeviceID, PNPClass, HardwareID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
        foreach (ManagementObject o in s.Get())
        {
            using (o)
            {
                var code = Convert.ToUInt32(o["ConfigManagerErrorCode"]);
                if (code is 45 or 47) continue; // desligado / preparado para remoção segura: não é problema
                var hwid = (o["HardwareID"] as string[])?.FirstOrDefault();
                var name = o["Name"] as string;
                if (string.IsNullOrWhiteSpace(name)) name = Loc.T("Dispositivo desconhecido");
                var (kind, text) = Explain(code);
                list.Add(new ProblemDevice(name, o["DeviceID"] as string ?? "", o["PNPClass"] as string ?? "",
                    hwid, code, kind, text));
            }
        }
        return list.OrderBy(d => d.Kind).ThenBy(d => d.Name).ToList();
    }

    static (DeviceProblemKind, string) Explain(uint code) => code switch
    {
        28 => (DeviceProblemKind.Missing, Loc.T("Não tem driver instalado: o Windows não sabe usar esta peça.")),
        1 => (DeviceProblemKind.Missing, Loc.T("Não está configurado: falta o driver certo.")),
        18 => (DeviceProblemKind.Broken, Loc.T("O driver tem de ser reinstalado.")),
        3 => (DeviceProblemKind.Broken, Loc.T("O driver pode estar corrompido.")),
        39 => (DeviceProblemKind.Broken, Loc.T("O driver está corrompido ou falta um ficheiro.")),
        10 => (DeviceProblemKind.Broken, Loc.T("O dispositivo não consegue arrancar (driver errado ou estragado).")),
        31 => (DeviceProblemKind.Broken, Loc.T("O driver não carrega.")),
        37 => (DeviceProblemKind.Broken, Loc.T("O driver falhou ao iniciar.")),
        40 => (DeviceProblemKind.Broken, Loc.T("A informação do driver no registo está estragada.")),
        19 => (DeviceProblemKind.Broken, Loc.T("A configuração no registo está estragada.")),
        41 => (DeviceProblemKind.Broken, Loc.T("O driver carregou mas não encontra a peça.")),
        43 => (DeviceProblemKind.Broken, Loc.T("O Windows parou-o porque reportou problemas (falha do driver ou da peça).")),
        48 => (DeviceProblemKind.Broken, Loc.T("O driver foi bloqueado por ter problemas conhecidos: precisa de uma versão nova.")),
        52 => (DeviceProblemKind.Broken, Loc.T("O driver não tem assinatura digital válida (pode estar adulterado ou corrompido).")),
        12 => (DeviceProblemKind.Broken, Loc.T("Conflito de recursos com outro dispositivo.")),
        24 => (DeviceProblemKind.Broken, Loc.T("Não está presente ou não funciona bem.")),
        32 => (DeviceProblemKind.Broken, Loc.T("O serviço do driver está desativado.")),
        22 => (DeviceProblemKind.Disabled, Loc.T("Desativado (por ti ou por um programa).")),
        29 => (DeviceProblemKind.Disabled, Loc.T("Desativado na BIOS.")),
        14 or 21 => (DeviceProblemKind.NeedsReboot, Loc.T("Precisa de reiniciar o PC para acabar a instalação.")),
        _ => (DeviceProblemKind.Broken, Loc.T($"Erro {code} no Gestor de Dispositivos.")),
    };

    /// <summary>Remove o dispositivo e volta a detetá-lo: o Windows reinstala o melhor driver que tiver.</summary>
    public static async Task<string> ReinstallDeviceAsync(ProblemDevice d)
    {
        Simulation.Guard(Loc.T($"reinstalar o driver de {d.Name}"));
        Admin.Require();
        ChangeLog.Record(ChangeKind.Driver, Loc.T($"Driver reinstalado: {d.Name}"), d.Explanation);
        var rm = await Shell.RunAsync("pnputil.exe", $"/remove-device \"{d.InstanceId}\"", 120_000);
        var scan = await Shell.RunAsync("pnputil.exe", "/scan-devices", 180_000);
        if (!rm.Ok && !scan.Ok) return Loc.T("Não deu: ") + Shell.CleanError(rm);
        var now = ProblemDevices().FirstOrDefault(p => p.InstanceId.Equals(d.InstanceId, StringComparison.OrdinalIgnoreCase));
        return now is null ? Loc.T("✓ Reinstalado: já não tem erro.")
            : now.Kind == DeviceProblemKind.Missing ? Loc.T("Reinstalado, mas o Windows continua sem driver para ele. Procura no Windows Update ou no site do fabricante.")
            : Loc.T("Reinstalado, mas ainda tem erro: ") + now.Explanation;
    }

    public static async Task<string> EnableDeviceAsync(ProblemDevice d)
    {
        Simulation.Guard($"ativar {d.Name}");
        Admin.Require();
        var r = await Shell.RunAsync("pnputil.exe", $"/enable-device \"{d.InstanceId}\"", 60_000);
        if (r.Ok) ChangeLog.Record(ChangeKind.Driver, Loc.T($"Dispositivo ativado: {d.Name}"));
        return r.Ok ? "✓ Ativado." : Loc.T("Não deu: ") + Shell.CleanError(r);
    }

    /// <summary>Pede ao Windows para voltar a procurar hardware novo (instala drivers que já tenha guardados).</summary>
    public static Task<ShellResult> ScanForHardwareAsync() => Shell.RunAsync("pnputil.exe", "/scan-devices", 180_000);

    /// <summary>Link de pesquisa pelo ID do hardware: é a forma mais fiável de descobrir o que é um "dispositivo desconhecido".</summary>
    public static string SearchUrl(ProblemDevice d)
    {
        var id = d.HardwareId ?? d.InstanceId;
        // VEN_xxxx&DEV_xxxx chega para identificar a peça; o resto (SUBSYS, REV) só baralha a pesquisa
        var m = Regex.Match(id, @"(VEN|VID)_[0-9A-F]{4}&(DEV|PID)_[0-9A-F]{4}", RegexOptions.IgnoreCase);
        var q = (m.Success ? m.Value : id) + " driver";
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(q);
    }

    // ------------------------------------------------------------------ Windows Update

    const string WuSearch =
        "$s=New-Object -ComObject Microsoft.Update.Session;$q=$s.CreateUpdateSearcher();$q.ServerSelection=2;$q.Online=$true;" +
        "$r=$q.Search(\"IsInstalled=0 and Type='Driver' and IsHidden=0\");";

    /// <summary>Procura no Windows Update drivers para este PC que ainda não estão instalados (inclui os "opcionais").</summary>
    public static async Task<List<DriverUpdate>> SearchWindowsUpdateAsync()
    {
        var script = WuSearch +
            "$o=@(foreach($u in $r.Updates){[pscustomobject]@{Id=$u.Identity.UpdateID;Title=$u.Title;Class=[string]$u.DriverClass;" +
            "Maker=[string]$u.DriverManufacturer;Date=$(if($u.DriverVerDate){$u.DriverVerDate.ToString('yyyy-MM-dd')}else{''});Size=[long]$u.MaxDownloadSize}});" +
            "ConvertTo-Json -InputObject $o -Compress";
        var r = await Shell.PowerShellAsync(script, 300_000);
        if (!r.Ok) throw new InvalidOperationException(Loc.T("O Windows Update não respondeu: ") + Shell.CleanError(r));
        var json = r.StdOut.Trim();
        if (json.Length == 0) return new();

        var list = new List<DriverUpdate>();
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            DateTime? date = DateTime.TryParse(e.GetProperty("Date").GetString(), out var d) ? d : null;
            list.Add(new DriverUpdate(e.GetProperty("Id").GetString() ?? "", e.GetProperty("Title").GetString() ?? "",
                e.GetProperty("Class").GetString() ?? "", e.GetProperty("Maker").GetString() ?? "", date,
                e.GetProperty("Size").GetInt64()));
        }
        return list;
    }

    /// <summary>Descarrega e instala os drivers escolhidos pelo Windows Update.</summary>
    public static async Task<DriverInstallResult> InstallFromWindowsUpdateAsync(IReadOnlyCollection<DriverUpdate> updates)
    {
        Simulation.Guard(Loc.T($"instalar {updates.Count} driver(s) do Windows Update"));
        Admin.Require();
        if (updates.Count == 0) return new(0, 0, false, Loc.T("Nada para instalar."));
        var ids = string.Join(",", updates.Select(u => Shell.Quote(u.Id)));
        var script = $"$ids=@({ids});" + WuSearch +
            "$c=New-Object -ComObject Microsoft.Update.UpdateColl;" +
            "foreach($u in $r.Updates){if($ids -contains $u.Identity.UpdateID){if(-not $u.EulaAccepted){$u.AcceptEula()};[void]$c.Add($u)}};" +
            "if($c.Count -eq 0){'{\"ok\":0,\"fail\":0,\"reboot\":false,\"missing\":true}';exit};" +
            "$d=$s.CreateUpdateDownloader();$d.Updates=$c;[void]$d.Download();" +
            "$i=$s.CreateUpdateInstaller();$i.Updates=$c;$res=$i.Install();" +
            "$ok=0;$fail=0;for($k=0;$k -lt $c.Count;$k++){$rc=$res.GetUpdateResult($k).ResultCode;if($rc -eq 2 -or $rc -eq 3){$ok++}else{$fail++}};" +
            "ConvertTo-Json -Compress -InputObject @{ok=$ok;fail=$fail;reboot=[bool]$res.RebootRequired;missing=$false}";
        var r = await Shell.PowerShellAsync(script, 45 * 60_000);
        if (!r.Ok) return new(0, updates.Count, false, Loc.T("O Windows Update falhou: ") + Shell.CleanError(r));

        using var doc = JsonDocument.Parse(r.StdOut.Trim().Split('\n').Last());
        var root = doc.RootElement;
        int ok = root.GetProperty("ok").GetInt32(), fail = root.GetProperty("fail").GetInt32();
        var reboot = root.GetProperty("reboot").GetBoolean();
        if (root.GetProperty("missing").GetBoolean())
            return new(0, 0, false, Loc.T("O Windows Update já não oferece estes drivers (talvez já tenham sido instalados)."));
        var msg = fail == 0 ? $"✓ {ok} driver(s) instalado(s)." : $"{ok} instalado(s), {fail} falhou/falharam.";
        if (ok > 0)
            ChangeLog.Record(ChangeKind.Driver, Loc.T($"{ok} driver(s) instalado(s) pelo Windows Update"),
                string.Join(" · ", updates.Select(u => u.Title)), UndoKind.RestorePoint);
        if (reboot) msg += Loc.T(" Reinicia o PC para acabar.");
        return new(ok, fail, reboot, msg);
    }

    // ------------------------------------------------------------------ drivers importantes

    sealed record SignedDriver(string Device, string Class, string Provider, string Version, DateTime? Date, string Inf, string? HardwareId);

    static List<SignedDriver> SignedDrivers()
    {
        var list = new List<SignedDriver>();
        using var s = new ManagementObjectSearcher(
            "SELECT DeviceName, DeviceClass, DriverProviderName, DriverVersion, DriverDate, InfName, HardWareID FROM Win32_PnPSignedDriver");
        foreach (ManagementObject o in s.Get())
        {
            using (o)
            {
                if (o["DeviceName"] is not string name || string.IsNullOrWhiteSpace(name)) continue;
                DateTime? date = null;
                if (o["DriverDate"] is string raw)
                    try { date = ManagementDateTimeConverter.ToDateTime(raw); } catch { }
                list.Add(new SignedDriver(name, (o["DeviceClass"] as string ?? "").ToUpperInvariant(),
                    o["DriverProviderName"] as string ?? "", o["DriverVersion"] as string ?? "", date, o["InfName"] as string ?? "",
                    o["HardWareID"] as string));
            }
        }
        return list;
    }

    static readonly Regex VirtualRx = new(
        @"virtual|wan miniport|vpn|tap-|hyper-v|loopback|kernel debug|bluetooth device|wi-?fi direct|teredo|6to4|isatap|npcap|fxsound|steam streaming|vb-audio|voicemeeter|nvidia broadcast|sonar",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static bool IsMicrosoft(string provider) => provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
    static bool Is(string s, string v) => s.Contains(v, StringComparison.OrdinalIgnoreCase);
    static int Months(DateTime? d) => d is DateTime t ? (int)((DateTime.Now - t).TotalDays / 30.4) : 0;

    /// <summary>Onde estão os drivers oficiais da motherboard / do portátil.</summary>
    public static (string Name, string Url) BoardSupport()
    {
        string sysMaker = "", sysModel = "", boardMaker = "", board = "";
        try
        {
            using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                foreach (ManagementObject o in s.Get()) using (o) { sysMaker = o["Manufacturer"] as string ?? ""; sysModel = o["Model"] as string ?? ""; }
            using (var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
                foreach (ManagementObject o in s.Get()) using (o) { boardMaker = o["Manufacturer"] as string ?? ""; board = o["Product"] as string ?? ""; }
        }
        catch { }

        // portáteis / PCs de marca: o site da marca tem tudo
        if (Is(sysMaker, "Dell")) return ($"Dell {sysModel}", "https://www.dell.com/support/home/pt-pt");
        if (Is(sysMaker, "HP") || Is(sysMaker, "Hewlett")) return ($"HP {sysModel}", "https://support.hp.com/pt-pt/drivers");
        if (Is(sysMaker, "Lenovo")) return ($"Lenovo {sysModel}", "https://pcsupport.lenovo.com/");

        var site = boardMaker switch
        {
            var m when Is(m, "Gigabyte") => "gigabyte.com",
            var m when Is(m, "ASUS") => "asus.com",
            var m when Is(m, "Micro-Star") || Is(m, "MSI") => "msi.com",
            var m when Is(m, "ASRock") => "asrock.com",
            var m when Is(m, "Acer") => "acer.com",
            _ => null,
        };
        var name = $"{boardMaker} {board}".Trim();
        if (name.Length == 0) name = Loc.T("a tua motherboard");
        var q = $"{board} drivers" + (site is null ? "" : $" site:{site}");
        return (name, "https://www.google.com/search?q=" + Uri.EscapeDataString(q));
    }

    /// <summary>Placas compradas à parte (não vêm na motherboard): o driver está no site da marca da placa.</summary>
    static readonly Regex AddOnCardRx = new(@"TP-?Link|Netgear|D-Link|ASUS|Tenda|Ubiquiti|Edimax|Fenvi|Archer|USB",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string DeviceSearch(string device) => "https://www.google.com/search?q=" + Uri.EscapeDataString(device + " driver download");

    const string AmdDrivers = "https://www.amd.com/en/support/download/drivers.html";
    const string IntelDsa = "https://www.intel.com/content/www/us/en/support/detect.html";
    const string NvidiaApp = "https://www.nvidia.com/pt-pt/software/nvidia-app/";

    public static List<KeyDriver> KeyDrivers()
    {
        var all = SignedDrivers();
        var (boardName, boardUrl) = BoardSupport();
        var list = new List<KeyDriver>();

        // Placa gráfica
        foreach (var g in all.Where(d => d.Class == "DISPLAY").GroupBy(d => d.Device).Select(g => g.First()))
        {
            if (Is(g.Device, "Basic Display") || Is(g.Device, "Basic Render"))
            {
                list.Add(new("🎮", Loc.T("Placa gráfica"), g.Device, g.Provider, g.Version, g.Date, KeyDriverStatus.Missing,
                    Loc.T("A gráfica está a usar o driver básico da Microsoft: sem aceleração, jogos e vídeos ficam muito lentos. Instala o driver do fabricante."),
                    null, "") { HardwareId = g.HardwareId });
                continue;
            }
            if (Is(g.Device, "Virtual") || Is(g.Device, "Parsec") || Is(g.Device, "Remote")) continue;
            var m = Months(g.Date);
            var (url, label) = Is(g.Provider, "NVIDIA") || Is(g.Device, "NVIDIA") ? (NvidiaApp, "NVIDIA App")
                : Is(g.Device, "Radeon") || Is(g.Provider, "Advanced Micro") || Is(g.Provider, "AMD") ? (AmdDrivers, "AMD Adrenalin")
                : Is(g.Device, "Intel") ? (IntelDsa, "Intel") : ((string?)null, "");
            var status = m >= 6 ? KeyDriverStatus.Update : KeyDriverStatus.Ok;
            var advice = status == KeyDriverStatus.Update
                ? Loc.T($"Tem {m} meses. Drivers novos da gráfica dão mais FPS em jogos recentes e corrigem crashes.")
                : Loc.T("Recente. A gráfica é o driver que mais conta para jogos: atualiza quando sair um jogo grande.");
            list.Add(new("🎮", Loc.T("Placa gráfica"), g.Device, g.Provider, g.Version, g.Date, status, advice, url, label) { HardwareId = g.HardwareId });
        }

        // Chipset (AMD / Intel): o mais recente decide
        var chipset = all.Where(d => d.Class == "SYSTEM" &&
                                     (Is(d.Provider, "Advanced Micro") || Is(d.Provider, "AMD") || Is(d.Provider, "Intel")))
                         .OrderByDescending(d => d.Date).FirstOrDefault();
        if (chipset != null)
        {
            var amd = !Is(chipset.Provider, "Intel");
            var m = Months(chipset.Date);
            var status = m >= 24 ? KeyDriverStatus.Update : KeyDriverStatus.Ok;
            var advice = status == KeyDriverStatus.Update
                ? (amd
                    ? Loc.T($"O driver do chipset mais recente que tens é de há {m / 12} anos. O \"AMD Chipset Software\" novo traz melhor gestão de energia do Ryzen e correções de USB/PCIe.")
                    : Loc.T($"Tem {m / 12} anos. O Intel Driver & Support Assistant deteta e instala a versão nova."))
                : "Atualizado.";
            list.Add(new("🧩", "Chipset", amd ? Loc.T("Chipset AMD (GPIO, SMBus, PCI…)") : "Chipset Intel", chipset.Provider,
                chipset.Version, chipset.Date, status, advice, amd ? AmdDrivers : IntelDsa, amd ? "AMD (chipset)" : "Intel"));
        }
        else if (all.Any(d => d.Class == "SYSTEM" && Is(d.Device, "AMD")) || Is(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "", "AMD"))
        {
            list.Add(new("🧩", "Chipset", "Chipset AMD", "Microsoft", "", null, KeyDriverStatus.Update,
                Loc.T("Estás com os drivers genéricos do Windows. Instala o \"AMD Chipset Software\" para o Ryzen gerir melhor a energia e os núcleos."),
                AmdDrivers, "AMD (chipset)"));
        }

        // Rede (placas físicas)
        foreach (var n in all.Where(d => d.Class == "NET" && !IsMicrosoft(d.Provider) && !VirtualRx.IsMatch(d.Device))
                             .GroupBy(d => d.Device).Select(g => g.First()))
        {
            var m = Months(n.Date);
            var wifi = Is(n.Device, "Wireless") || Is(n.Device, "Wi-Fi") || Is(n.Device, "WiFi") || Is(n.Device, "802.11");
            var status = m >= 30 ? KeyDriverStatus.Update : KeyDriverStatus.Ok;
            var advice = status == KeyDriverStatus.Update
                ? Loc.T($"Tem {m / 12} anos. Drivers de rede antigos causam quebras de ligação e mais latência: vale a pena atualizar.")
                : "Atualizado.";
            var (url, label) = Is(n.Provider, "Intel") ? (IntelDsa, "Intel")
                : AddOnCardRx.IsMatch(n.Device) ? (DeviceSearch(n.Device), Loc.T("Procurar driver"))
                : (boardUrl, Loc.T("Site da motherboard"));
            list.Add(new(wifi ? "📶" : "🌐", wifi ? "Wi-Fi" : Loc.T("Rede (cabo)"), n.Device, n.Provider, n.Version, n.Date, status, advice, url, label) { HardwareId = n.HardwareId });
        }

        // Áudio (placa de som da motherboard; ignora o áudio HDMI da gráfica e virtuais)
        foreach (var a in all.Where(d => d.Class == "MEDIA" && !IsMicrosoft(d.Provider) && !VirtualRx.IsMatch(d.Device) &&
                                         !Is(d.Provider, "NVIDIA") && !Is(d.Device, "NVIDIA") && !Is(d.Device, "AMD High Definition") &&
                                         !Is(d.Device, "Intel(R) Display Audio"))
                             .GroupBy(d => d.Device).Select(g => g.First()))
        {
            var m = Months(a.Date);
            var status = m >= 36 ? KeyDriverStatus.Optional : KeyDriverStatus.Ok;
            var advice = status == KeyDriverStatus.Optional
                ? Loc.T($"Tem {m / 12} anos. É normal nos drivers de áudio: só vale a pena atualizar se tiveres problemas de som.")
                : "Atualizado.";
            list.Add(new("🔊", Loc.T("Áudio"), a.Device, a.Provider, a.Version, a.Date, status, advice, boardUrl, Loc.T("Site da motherboard")) { HardwareId = a.HardwareId });
        }

        // Bluetooth
        foreach (var b in all.Where(d => d.Class == "BLUETOOTH" && !IsMicrosoft(d.Provider) && !VirtualRx.IsMatch(d.Device))
                             .GroupBy(d => d.Device).Select(g => g.First()))
        {
            var m = Months(b.Date);
            var status = m >= 36 ? KeyDriverStatus.Optional : KeyDriverStatus.Ok;
            list.Add(new("🔵", "Bluetooth", b.Device, b.Provider, b.Version, b.Date, status,
                status == KeyDriverStatus.Ok ? "Atualizado." : Loc.T($"Tem {m / 12} anos. Atualiza se o Bluetooth falhar ou cortar o som."),
                AddOnCardRx.IsMatch(b.Device) ? DeviceSearch(b.Device) : boardUrl,
                AddOnCardRx.IsMatch(b.Device) ? Loc.T("Procurar driver") : Loc.T("Site da motherboard")) { HardwareId = b.HardwareId });
        }

        return list;
    }

    // ------------------------------------------------------------------ versões antigas guardadas

    sealed record InfInfo(string Provider, string Class, string Version, DateTime? Date);

    static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>
    /// Pacotes de drivers de terceiros (oemNN.inf) que ficaram guardados quando instalaste uma versão mais nova
    /// do mesmo driver, e que nenhum dispositivo está a usar. Remover só liberta espaço; sem /force, o Windows recusa
    /// apagar um driver em uso.
    /// </summary>
    public static List<OldDriverPackage> OldPackages()
    {
        var infDir = Path.Combine(WinDir, "INF");
        var repo = Path.Combine(WinDir, "System32", "DriverStore", "FileRepository");
        var inUse = SignedDrivers().Select(d => d.Inf).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // indexar o repositório por tamanho do .inf, para encontrar a pasta de cada oemNN.inf comparando o conteúdo
        var bySize = new Dictionary<long, List<(string Dir, string Inf)>>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(repo))
            {
                var folder = Path.GetFileName(dir);
                var cut = folder.IndexOf(".inf_", StringComparison.OrdinalIgnoreCase);
                if (cut < 0) continue;
                var inf = Path.Combine(dir, folder[..(cut + 4)]);
                try
                {
                    var len = new FileInfo(inf).Length;
                    if (!bySize.TryGetValue(len, out var l)) bySize[len] = l = new();
                    l.Add((dir, inf));
                }
                catch { }
            }
        }
        catch { return new(); }

        var packages = new List<(string Published, string Original, string Dir, InfInfo Info)>();
        foreach (var oem in Directory.EnumerateFiles(infDir, "oem*.inf"))
        {
            try
            {
                var len = new FileInfo(oem).Length;
                if (!bySize.TryGetValue(len, out var candidates)) continue;
                var hash = Hash(oem);
                var match = candidates.FirstOrDefault(c => Hash(c.Inf).SequenceEqual(hash));
                if (match.Dir is null) continue;
                packages.Add((Path.GetFileName(oem), Path.GetFileName(match.Inf), match.Dir, ReadInf(oem)));
            }
            catch { }
        }

        var result = new List<OldDriverPackage>();
        foreach (var g in packages.GroupBy(p => (p.Original.ToLowerInvariant(), p.Info.Provider.ToLowerInvariant())))
        {
            if (g.Count() < 2) continue;
            var newest = g.OrderByDescending(p => ParseVersion(p.Info.Version)).ThenByDescending(p => p.Info.Date).First();
            foreach (var p in g)
            {
                if (p.Published == newest.Published || inUse.Contains(p.Published)) continue;
                // só versões mesmo mais antigas (versão menor, ou a mesma versão com data anterior)
                var cmp = ParseVersion(p.Info.Version).CompareTo(ParseVersion(newest.Info.Version));
                if (cmp > 0 || (cmp == 0 && !(p.Info.Date < newest.Info.Date))) continue;
                result.Add(new OldDriverPackage(p.Published, p.Original, p.Info.Provider, p.Info.Class, p.Info.Version,
                    p.Info.Date, cmp == 0 ? Loc.T($"{newest.Info.Version} de {newest.Info.Date:dd/MM/yyyy}") : newest.Info.Version, DirSize(p.Dir)));
            }
        }
        return result.OrderByDescending(p => p.SizeBytes).ToList();
    }

    /// <summary>Apaga um pacote antigo. Sem /force: se algum dispositivo o usar, o Windows recusa.</summary>
    public static async Task<string> DeletePackageAsync(OldDriverPackage p)
    {
        Simulation.Guard(Loc.T($"remover a versão antiga {p.OriginalName} {p.Version}"));
        Admin.Require();
        var r = await Shell.RunAsync("pnputil.exe", $"/delete-driver {p.PublishedName}", 120_000);
        if (r.Ok) ChangeLog.Record(ChangeKind.Driver, Loc.T($"Versão antiga removida: {p.OriginalName} {p.Version}"), p.Provider);
        return r.Ok ? "✓ Removido." : Loc.T("O Windows não deixou (pode estar em uso): ") + Shell.CleanError(r);
    }

    static byte[] Hash(string path)
    {
        using var f = File.OpenRead(path);
        return SHA256.HashData(f);
    }

    static long DirSize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch { return 0; }
    }

    static Version ParseVersion(string v) => Version.TryParse(v, out var x) ? x : new Version(0, 0);

    /// <summary>Lê Provider, Class e DriverVer da secção [Version] de um .inf (resolve %tokens% em [Strings]).</summary>
    static InfInfo ReadInf(string path)
    {
        string section = "", provider = "", cls = "", ver = "";
        DateTime? date = null;
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[') { section = line.Trim('[', ']').Trim().ToLowerInvariant(); continue; }
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..];
            var semi = value.IndexOf(';');
            if (semi >= 0 && !value.TrimStart().StartsWith('"')) value = value[..semi];
            value = value.Trim().Trim('"');

            if (section == "version")
            {
                if (key.Equals("Provider", StringComparison.OrdinalIgnoreCase)) provider = value;
                else if (key.Equals("Class", StringComparison.OrdinalIgnoreCase)) cls = value;
                else if (key.Equals("DriverVer", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = value.Split(',');
                    // a data nos .inf é sempre mm/dd/aaaa
                    if (DateTime.TryParseExact(parts[0].Trim(), new[] { "MM/dd/yyyy", "M/d/yyyy" },
                            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
                        date = d;
                    if (parts.Length > 1) ver = parts[1].Trim();
                }
            }
            else if (section.StartsWith("strings") && !strings.ContainsKey(key))
            {
                strings[key] = value;
            }
        }
        provider = Regex.Replace(provider, "%([^%]+)%", m => strings.GetValueOrDefault(m.Groups[1].Value, m.Value));
        return new InfInfo(provider, cls, ver, date);
    }
}
