using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PCOptimizer.Core.Util;
using PCOptimizer.Core.History;

namespace PCOptimizer.Core.Drivers;

public enum OfferKind
{
    /// <summary>Pacote .cab do Catálogo do Microsoft Update, instalado com pnputil.</summary>
    CatalogCab,
    /// <summary>Instalador oficial do fabricante (.exe assinado), corrido em modo silencioso.</summary>
    Installer,
}

/// <summary>Uma versão mais nova de um driver, pronta a descarregar de uma fonte oficial.</summary>
public sealed record DriverOffer
{
    /// <summary>Nome do dispositivo (KeyDriver.Device), InstanceId de um dispositivo sem driver, ou "chipset".</summary>
    public required string MatchKey { get; init; }
    public required string Source { get; init; }
    public required string Title { get; init; }
    public required string Version { get; init; }
    public DateTime? Date { get; init; }
    public long SizeBytes { get; init; }
    public required OfferKind Kind { get; init; }
    public string? CatalogId { get; init; }
    public string? Url { get; init; }
    public string? Referer { get; init; }
    /// <summary>O instalador tem de estar assinado por alguém cujo nome contém isto.</summary>
    public string? Signer { get; init; }
    public string SilentArgs { get; init; } = "";
    public string? HardwareId { get; init; }
}

/// <summary>
/// Descarrega e instala drivers diretamente das fontes oficiais:
/// Catálogo do Microsoft Update (drivers WHQL por ID do hardware), NVIDIA (API oficial) e AMD (chipset).
/// Só aceita HTTPS de domínios conhecidos, e os instaladores têm de ter assinatura digital válida do fabricante.
/// </summary>
public static class DriverDownloads
{
    const string CatalogSearch = "https://www.catalog.update.microsoft.com/Search.aspx?q=";
    const string CatalogDownload = "https://www.catalog.update.microsoft.com/DownloadDialog.aspx";

    static readonly string[] AllowedHosts =
    {
        "download.windowsupdate.com", "catalog.s.download.windowsupdate.com", "catalog.sf.dl.delivery.mp.microsoft.com",
        "download.nvidia.com", "us.download.nvidia.com", "international-gfe.download.nvidia.com", "drivers.amd.com",
    };

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(60) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36");
        return h;
    }

    public static string DownloadFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCOptimizer", "Downloads");

    // ------------------------------------------------------------------ procurar

    /// <summary>Procura versões mais novas para os drivers importantes e drivers para os dispositivos sem driver.</summary>
    public static async Task<List<DriverOffer>> FindOffersAsync(IReadOnlyList<KeyDriver> keys, IReadOnlyList<ProblemDevice> problems, CancellationToken ct = default)
    {
        var tasks = new List<Task<DriverOffer?>>();
        foreach (var k in keys)
        {
            if (k.Category == "Chipset") tasks.Add(Safe(() => AmdChipsetOfferAsync(ct)));
            else if (k.HardwareId is string hw && Is(hw, "VEN_10DE") && k.Category == Loc.T("Placa gráfica")) tasks.Add(Safe(() => NvidiaOfferAsync(k, ct)));
            else if (k.HardwareId is string id) tasks.Add(Safe(() => CatalogOfferAsync(k.Device, id, k.Version, k.Date, ct)));
        }
        foreach (var p in problems.Where(p => p.Kind is DeviceProblemKind.Missing or DeviceProblemKind.Broken && p.HardwareId != null))
            tasks.Add(Safe(() => CatalogOfferAsync(p.InstanceId, p.HardwareId!, null, null, ct)));

        var offers = await Task.WhenAll(tasks);
        return offers.OfType<DriverOffer>().ToList();
    }

    static async Task<DriverOffer?> Safe(Func<Task<DriverOffer?>> f)
    {
        try { return await f(); }
        catch { return null; } // sem internet / site mudou: fica só o botão do site oficial
    }

    static bool Is(string s, string v) => s.Contains(v, StringComparison.OrdinalIgnoreCase);

    // ---------- Catálogo do Microsoft Update

    sealed record CatalogRow(string Id, string Title, string Products, DateTime? Date, Version? Version, long Size);

    static async Task<List<CatalogRow>> SearchCatalogAsync(string query, CancellationToken ct)
    {
        var html = await Http.GetStringAsync(CatalogSearch + Uri.EscapeDataString(query), ct);
        var rows = new List<CatalogRow>();
        foreach (Match m in Regex.Matches(html, @"<tr id=""([0-9a-f-]{36})_R\d+""[\s\S]*?</tr>"))
        {
            var cells = Regex.Matches(m.Value, @"<td[^>]*>([\s\S]*?)</td>")
                .Select(c => WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(c.Groups[1].Value, "<[^>]+>", " "), @"\s+", " ")).Trim())
                .ToList();
            if (cells.Count < 7) continue;
            var title = cells[1];
            DateTime? date = DateTime.TryParseExact(cells[4], "M/d/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            var verText = Version.TryParse(cells[5], out var v) ? v
                : Regex.Match(title, @"\d+\.\d+\.\d+(\.\d+)?") is { Success: true } tm && Version.TryParse(tm.Value, out var tv) ? tv : null;
            var size = Regex.Match(cells[6], @"(\d+)\s*$") is { Success: true } sm ? long.Parse(sm.Groups[1].Value) : 0;
            rows.Add(new CatalogRow(m.Groups[1].Value, title, cells[2], date, verText, size));
        }
        return rows;
    }

    static readonly int OsBuild = Environment.OSVersion.Version.Build;
    static readonly string? OsDisplayVersion = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null) as string;

    /// <summary>Serve para este Windows (x64, Windows 10/11, e não exige uma versão do Windows mais nova do que a tua)?</summary>
    static bool FitsThisWindows(CatalogRow r)
    {
        if (Is(r.Title, "ARM64") || Is(r.Products, "ARM64") || Is(r.Title, "x86 ")) return false;
        var win11 = Is(r.Products, "Windows 11");
        var win10 = Is(r.Products, "Windows 10");
        if (!win11 && !win10) return false;
        if (OsBuild < 22000) return win10;
        if (!win11 || OsDisplayVersion is null) return true;

        // "Windows 11 Client, version 24H2 and later": só serve se o teu Windows for 24H2 ou mais novo
        var mine = YearHalf(OsDisplayVersion);
        var needed = Regex.Matches(r.Products, @"Windows 11[^,]*?version (\d\d)H(\d)").Select(m => YearHalf($"{m.Groups[1].Value}H{m.Groups[2].Value}")).ToList();
        return needed.Count == 0 || needed.Min() <= mine;
    }

    static int YearHalf(string displayVersion) =>
        Regex.Match(displayVersion, @"(\d\d)H(\d)") is { Success: true } m ? int.Parse(m.Groups[1].Value) * 10 + int.Parse(m.Groups[2].Value) : 999;

    /// <summary>
    /// Procura primeiro pelo ID exato (com SUBSYS = feito para a tua placa); se não houver nada mais novo,
    /// procura pelo ID genérico do chip (VEN/DEV).
    /// </summary>
    static async Task<DriverOffer?> CatalogOfferAsync(string matchKey, string hardwareId, string? installedVersion, DateTime? installedDate, CancellationToken ct)
    {
        var current = Version.TryParse(installedVersion, out var cv) ? cv : null;
        var queries = new List<string>();
        var specific = Regex.Match(hardwareId, @"^[^\\]+\\.*?(SUBSYS_[0-9A-F]{8})", RegexOptions.IgnoreCase);
        var generic = Regex.Match(hardwareId, @"^[^\\]+\\(FUNC_\d+&)?(VEN|VID)_[0-9A-Z]{4}&(DEV|PID)_[0-9A-Z]{4}", RegexOptions.IgnoreCase);
        if (specific.Success) queries.Add(specific.Value);
        if (generic.Success) queries.Add(generic.Value);
        if (queries.Count == 0) queries.Add(hardwareId);

        foreach (var q in queries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var best = (await SearchCatalogAsync(q, ct))
                .Where(r => r.Version != null && FitsThisWindows(r))
                .Where(r => current is null || r.Version > current)
                .Where(r => installedDate is null || r.Date is null || r.Date >= installedDate.Value.AddDays(-31))
                .OrderByDescending(r => r.Version).ThenByDescending(r => r.Date)
                .FirstOrDefault();
            if (best is null) continue;
            return new DriverOffer
            {
                MatchKey = matchKey, Source = Loc.T("Catálogo do Microsoft Update"), Title = best.Title, Version = best.Version!.ToString(),
                Date = best.Date, SizeBytes = best.Size, Kind = OfferKind.CatalogCab, CatalogId = best.Id, HardwareId = hardwareId,
            };
        }
        return null;
    }

    static async Task<List<string>> CatalogFileUrlsAsync(string updateId, CancellationToken ct)
    {
        var json = $"[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{updateId}\",\"updateID\":\"{updateId}\"}}]";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["updateIDs"] = json });
        var res = await Http.PostAsync(CatalogDownload, content, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        return Regex.Matches(text, @"\.url = '([^']+)'").Select(m => m.Groups[1].Value).Distinct().ToList();
    }

    // ---------- NVIDIA

    /// <summary>"32.0.16.1656" → 616.56 (a versão "comercial" da NVIDIA são os últimos 5 dígitos).</summary>
    public static decimal? NvidiaVersion(string windowsVersion)
    {
        var digits = windowsVersion.Replace(".", "");
        if (digits.Length < 5 || !digits.All(char.IsDigit)) return null;
        var last5 = digits[^5..];
        return decimal.Parse(last5[..3] + "." + last5[3..], CultureInfo.InvariantCulture);
    }

    static async Task<DriverOffer?> NvidiaOfferAsync(KeyDriver gpu, CancellationToken ct)
    {
        var dev = Regex.Match(gpu.HardwareId ?? "", @"DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        if (!dev.Success) return null;
        var laptop = HasBattery() ? "1" : "0";
        var query = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["dIDa"] = new[] { $"{dev.Groups[1].Value.ToUpperInvariant()}_10DE" }, ["osC"] = "10.0", ["osB"] = OsBuild.ToString(),
            ["is6"] = "1", ["lg"] = "1033", ["iLp"] = laptop, ["prvMd"] = "0", ["gcV"] = gpu.Version, ["gIsB"] = "1",
            ["dch"] = "1", ["upCRD"] = "0", ["isCRD"] = "0",
        });
        var url = "https://gfwsl.geforce.com/nvidia_web_services/controller.gfeclientcontent.NG.php/com.nvidia.services.GFEClientContent_NG.getDispDrvrByDevid/"
                  + Uri.EscapeDataString(query);
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        if (!doc.RootElement.TryGetProperty("DriverAttributes", out var a) || a.ValueKind != JsonValueKind.Object) return null;

        var latestText = a.GetProperty("Version").GetString() ?? "";
        if (!decimal.TryParse(latestText, NumberStyles.Number, CultureInfo.InvariantCulture, out var latest)) return null;
        var installed = NvidiaVersion(gpu.Version);
        if (installed is decimal i && latest <= i) return null;

        var download = a.TryGetProperty("DownloadURLAdmin", out var d) ? d.GetString() : null;
        if (string.IsNullOrEmpty(download)) return null;
        DateTime? date = a.TryGetProperty("ReleaseDateTime", out var rd) && DateTime.TryParse(rd.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
        long size = 0;
        try
        {
            using var head = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, download), ct);
            size = head.Content.Headers.ContentLength ?? 0;
        }
        catch { }

        return new DriverOffer
        {
            MatchKey = gpu.Device, Source = "NVIDIA (oficial)", Title = $"{a.GetProperty("Name").GetString()} {latestText}",
            Version = latestText, Date = date, SizeBytes = size, Kind = OfferKind.Installer, Url = download,
            Signer = "NVIDIA", SilentArgs = "-s -noreboot -noeula",
        };
    }

    static bool HasBattery()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_Battery");
            return s.Get().Count > 0;
        }
        catch { return false; }
    }

    // ---------- AMD chipset

    static readonly string[] Am4 = { "A320", "B350", "X370", "B450", "X470", "A520", "B550", "X570" };
    static readonly string[] Am5 = { "A620", "B650E", "B650", "X670E", "X670", "B840", "B850", "X870E", "X870" };

    static async Task<DriverOffer?> AmdChipsetOfferAsync(CancellationToken ct)
    {
        if (!Is(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "", "AMD")) return null;
        string board = "";
        using (var s = new ManagementObjectSearcher("SELECT Product FROM Win32_BaseBoard"))
            foreach (ManagementObject o in s.Get()) using (o) board = o["Product"] as string ?? "";

        var chip = Am5.FirstOrDefault(c => Regex.IsMatch(board, $@"\b{c}", RegexOptions.IgnoreCase));
        var socket = "am5";
        if (chip is null) { chip = Am4.FirstOrDefault(c => Regex.IsMatch(board, $@"\b{c}", RegexOptions.IgnoreCase)); socket = "am4"; }
        if (chip is null) return null;

        var page = $"https://www.amd.com/en/support/downloads/drivers.html/chipsets/{socket}/{chip.ToLowerInvariant()}.html";
        var html = await Http.GetStringAsync(page, ct);
        var m = Regex.Match(html, @"drivers\.amd\.com/drivers/(AMD_Chipset_Software_([\d\.]+)\.exe)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var latest = m.Groups[2].Value.TrimEnd('.');
        var installed = InstalledProgramVersion("AMD Chipset Software");
        if (installed != null && Version.TryParse(installed, out var iv) && Version.TryParse(latest, out var lv) && lv <= iv) return null;

        return new DriverOffer
        {
            MatchKey = "chipset", Source = "AMD (oficial)", Title = $"AMD Chipset Software {latest} ({chip})", Version = latest,
            Kind = OfferKind.Installer, Url = "https://" + m.Value, Referer = "https://www.amd.com/", Signer = "Advanced Micro Devices",
            SilentArgs = "/S",
        };
    }

    static string? InstalledProgramVersion(string name)
    {
        foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var k = Registry.LocalMachine.OpenSubKey(root);
            if (k is null) continue;
            foreach (var sub in k.GetSubKeyNames())
            {
                using var app = k.OpenSubKey(sub);
                if (app?.GetValue("DisplayName") is string n && n.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    return app.GetValue("DisplayVersion") as string;
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ descarregar e instalar

    /// <summary>Descarrega, verifica e instala. Devolve uma mensagem para o utilizador.</summary>
    public static async Task<(bool Ok, bool Reboot, string Message)> DownloadAndInstallAsync(
        DriverOffer offer, IProgress<(double Percent, string Text)> progress, CancellationToken ct)
    {
        Simulation.Guard(Loc.T($"descarregar e instalar {offer.Title}"));
        Admin.Require();
        var folder = Path.Combine(DownloadFolder, Regex.Replace(offer.Title, @"[^\w\.\- ]", "_").Trim() + "_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        try
        {
            var urls = offer.Kind == OfferKind.CatalogCab
                ? await CatalogFileUrlsAsync(offer.CatalogId!, ct)
                : new List<string> { offer.Url! };
            if (urls.Count == 0) return (false, false, Loc.T("O catálogo não devolveu o ficheiro para descarregar. Tenta mais tarde."));

            var files = new List<string>();
            for (var i = 0; i < urls.Count; i++)
            {
                var label = urls.Count > 1 ? $" ({i + 1}/{urls.Count})" : "";
                files.Add(await DownloadAsync(urls[i], folder, offer.Referer,
                    new Progress<(long Done, long Total)>(p =>
                    {
                        var pct = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
                        var total = p.Total > 0 ? Loc.T(" de ") + SystemInfo.FormatBytes(p.Total) : "";
                        progress.Report((pct, $"A descarregar{label}… {SystemInfo.FormatBytes(p.Done)}{total}"));
                    }), ct));
            }

            progress.Report((100, Loc.T("A verificar e a instalar…")));
            var result = offer.Kind == OfferKind.CatalogCab
                ? await InstallCabsAsync(files, folder, offer)
                : await RunInstallerAsync(files[0], offer, progress);
            if (result.Item1)
                ChangeLog.Record(ChangeKind.Driver, Loc.T($"Driver instalado: {offer.Title}"), $"Fonte: {offer.Source}", UndoKind.RestorePoint);
            return result;
        }
        finally
        {
            // os instaladores ocupam espaço (o da NVIDIA tem ~1 GB): não ficam guardados
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    static async Task<string> DownloadAsync(string url, string folder, string? referer, IProgress<(long, long)> progress, CancellationToken ct)
    {
        var uri = new Uri(url.Replace("http://", "https://"));
        if (uri.Scheme != "https" || !AllowedHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(Loc.T($"Endereço não oficial recusado: {uri.Host}"));

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        if (referer != null) req.Headers.Referrer = new Uri(referer);
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? 0;
        var path = Path.Combine(folder, Path.GetFileName(uri.LocalPath));

        await using var src = await res.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(path);
        var buffer = new byte[1 << 16];
        long done = 0, lastReport = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            if (done - lastReport > 512 * 1024) { progress.Report((done, total)); lastReport = done; }
        }
        progress.Report((done, total));
        return path;
    }

    /// <summary>Extrai o .cab e instala com pnputil (o Windows recusa drivers sem assinatura válida).</summary>
    static async Task<(bool, bool, string)> InstallCabsAsync(List<string> cabs, string folder, DriverOffer offer)
    {
        var dir = Path.Combine(folder, "drv");
        Directory.CreateDirectory(dir);
        foreach (var cab in cabs)
        {
            var x = await Shell.RunAsync("expand.exe", $"-F:* \"{cab}\" \"{dir}\"", 300_000);
            if (!x.Ok) return (false, false, Loc.T("Não foi possível extrair o pacote: ") + Shell.CleanError(x));
        }
        if (!Directory.EnumerateFiles(dir, "*.inf", SearchOption.AllDirectories).Any())
            return (false, false, Loc.T("O pacote não tem nenhum driver (.inf)."));

        var r = await Shell.RunAsync("pnputil.exe", $"/add-driver \"{dir}\\*.inf\" /subdirs /install", 600_000);
        var reboot = r.ExitCode == 3010 || Is(r.StdOut, "reboot") || Is(r.StdOut, "reinici");
        if (r.ExitCode is 0 or 3010)
            return (true, reboot, Loc.T($"✓ Driver {offer.Version} instalado.") + (reboot ? Loc.T(" Reinicia o PC para acabar.") : ""));
        if (r.ExitCode == 259) // ERROR_NO_MORE_ITEMS: ficou guardado, mas o Windows achou o atual melhor/igual
            return (false, false, Loc.T("O Windows guardou o driver novo, mas manteve o atual porque o considera mais adequado à tua placa."));
        return (false, false, Loc.T("O Windows recusou o driver: ") + Shell.CleanError(r));
    }

    /// <summary>Confirma a assinatura digital do fabricante e corre o instalador em silêncio.</summary>
    static async Task<(bool, bool, string)> RunInstallerAsync(string exe, DriverOffer offer, IProgress<(double Percent, string Text)> progress)
    {
        var sig = await Shell.PowerShellAsync(
            $"$s=Get-AuthenticodeSignature -LiteralPath {Shell.Quote(exe)};\"$($s.Status)|$($s.SignerCertificate.Subject)\"", 120_000);
        var parts = sig.StdOut.Trim().Split('|', 2);
        if (parts.Length < 2 || parts[0] != "Valid" || (offer.Signer != null && !Is(parts[1], offer.Signer)))
            return (false, false, Loc.T($"⚠ O ficheiro não tem a assinatura digital esperada ({offer.Signer}). Não foi instalado, por segurança."));

        static bool Done(int code) => code is 0 or 1 or 3010 or 1641;
        var r = new ShellResult(-1, "", "");
        // os instaladores com vários pacotes (ex.: AMD) falham se o Windows Installer estiver ocupado a meio: 2 tentativas
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await WaitForWindowsInstallerAsync(progress);
            if (attempt > 1) progress.Report((100, Loc.T("A tentar outra vez…")));
            r = await Shell.RunAsync(exe, offer.SilentArgs, 45 * 60_000);
            if (Done(r.ExitCode)) return (true, true, Loc.T($"✓ {offer.Title} instalado. Reinicia o PC para acabar."));
        }

        // Alguns instaladores falham em modo silencioso (ex.: o da AMD num PC com restos de uma instalação antiga).
        // Plano B: abre o instalador normal do fabricante, já verificado, para acabar com cliques.
        if (!InteractiveFallback) return (false, false, Loc.T($"O instalador terminou com erro (código {r.ExitCode}). Tenta instalar à mão pelo site oficial."));
        await WaitForWindowsInstallerAsync(progress);
        progress.Report((100, Loc.T("A instalação automática falhou: abri o instalador do fabricante, segue os passos dele…")));
        var ui = await RunVisibleAsync(exe);
        if (Done(ui))
            return (true, true, Loc.T($"✓ {offer.Title} instalado (pelo instalador do fabricante). Reinicia o PC para acabar."));
        return (false, false, Loc.T($"A instalação silenciosa falhou (código {r.ExitCode}) e o instalador do fabricante terminou com o código {ui}. ") +
                              Loc.T("Se o fechaste a meio, tenta outra vez; se não, reinicia o PC e tenta de novo."));
    }

    /// <summary>Se a instalação silenciosa falhar, abrir o instalador normal (desligado em testes e na linha de comandos).</summary>
    public static bool InteractiveFallback { get; set; } = true;

    /// <summary>O Windows Installer só faz uma instalação de cada vez (o mutex Global\_MSIExecute existe enquanto está ocupado).</summary>
    public static bool WindowsInstallerBusy
    {
        get
        {
            try { Mutex.OpenExisting(@"Global\_MSIExecute").Dispose(); return true; }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return true; } // existe, só não temos acesso
        }
    }

    /// <summary>Espera (até 15 min) que o Windows acabe outra instalação, ex.: Windows Update.</summary>
    static async Task WaitForWindowsInstallerAsync(IProgress<(double Percent, string Text)> progress)
    {
        var until = DateTime.Now.AddMinutes(15);
        // alguns segundos de folga: os instaladores em cadeia largam e voltam a pegar no Windows Installer
        for (var free = 0; free < 3 && DateTime.Now < until; )
        {
            if (WindowsInstallerBusy) { free = 0; progress.Report((100, Loc.T("À espera que o Windows acabe outra instalação…"))); }
            else free++;
            await Task.Delay(1000);
        }
    }

    static async Task<int> RunVisibleAsync(string exe)
    {
        using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })
                      ?? throw new InvalidOperationException(Loc.T("Não foi possível abrir o instalador."));
        await p.WaitForExitAsync();
        return p.ExitCode;
    }
}
