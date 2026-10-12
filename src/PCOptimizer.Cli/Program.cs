// Versão de linha de comandos, útil para testar o Core sem a interface.
// Só faz leituras: não aplica ajustes nem fecha processos.
using System.Text;
using PCOptimizer.Core;
using PCOptimizer.Core.Diagnostics;
using PCOptimizer.Core.Monitoring;
using PCOptimizer.Core.Network;
using PCOptimizer.Core.Optimization;
using PCOptimizer.Core.Util;

Console.OutputEncoding = Encoding.UTF8;
var cmd = args.FirstOrDefault() ?? "help";

switch (cmd)
{
    case "diag":
        foreach (var f in await DiagnosticsEngine.RunAsync(new Progress<string>(Console.WriteLine)))
            Print(f);
        break;

    case "tweaks":
        foreach (var p in TweakCatalog.Profiles)
        {
            Console.WriteLine($"\n== {p.Name} ==");
            foreach (var t in p.Tweaks)
            {
                var applied = await t.IsAppliedAsync();
                var status = applied switch { true => "APLICADO", false => "não aplicado", null => "n/a" };
                Console.WriteLine($"  [{status,-12}] {t.Name}{(TweakEngine.CanRevert(t) ? " (tem backup)" : "")}");
            }
        }
        break;

    case "startup":
        foreach (var s in StartupManager.List())
            Console.WriteLine($"  [{(s.Enabled ? "ON " : "off")}] {s.Name}  —  {s.Description ?? s.Command}  ({s.Location})");
        break;

    case "net":
    {
        var host = args.ElementAtOrDefault(1) ?? GameServers.Presets[0].Host;
        var report = await NetworkDiagnostics.RunAsync(host, host, new Progress<string>(Console.WriteLine));
        Console.WriteLine($"Ligação: {report.Connection?.TypeText} via {report.Connection?.Gateway}");
        Console.WriteLine($"Router:   {report.Router}");
        Console.WriteLine($"Internet: {report.Internet}");
        Console.WriteLine($"Servidor: {report.Game}");
        foreach (var f in report.Findings) Print(f);
        break;
    }

    case "dns":
        foreach (var r in await DnsBenchmark.RunAsync())
            Console.WriteLine($"  {r.Label,-24} {r.Server,-16} {r.TimeText}");
        break;

    case "bloat":
    {
        var b = await BufferbloatTest.RunAsync(new Progress<string>(Console.WriteLine));
        Console.WriteLine($"Parado: {b.IdleMs:N0} ms | Com download: {b.LoadedMs:N0} ms | +{b.AddedMs:N0} ms | Nota {b.Grade} | ~{b.DownloadMbps:N0} Mbps");
        break;
    }

    case "leak":
    {
        // teste sintético do detetor de memory leaks
        var t0 = DateTime.UtcNow;
        var rnd = new Random(1);
        var leaking = Enumerable.Range(0, 60).Select(i => (t0.AddSeconds(i * 10), 800 + i * 12 + rnd.NextDouble() * 20)).ToList();
        var stable = Enumerable.Range(0, 60).Select(i => (t0.AddSeconds(i * 10), 1500 + rnd.NextDouble() * 200)).ToList();
        var spike = Enumerable.Range(0, 60).Select(i => (t0.AddSeconds(i * 10), i < 30 ? 600.0 : 1400.0)).ToList();
        Console.WriteLine($"Leak (sobe sempre):   {ResourceMonitor.Analyze(leaking)}");
        Console.WriteLine($"Estável (oscila):     {ResourceMonitor.Analyze(stable)}");
        Console.WriteLine($"Degrau (abriu ficheiro): {ResourceMonitor.Analyze(spike)}");
        break;
    }

    case "monitor":
    {
        var seconds = int.Parse(args.ElementAtOrDefault(1) ?? "20");
        using var m = new ResourceMonitor(TimeSpan.FromSeconds(5));
        m.Updated += s => Console.WriteLine(
            $"{s.Time:T} CPU {s.CpuPercent,5:N1}% | RAM {s.Memory.LoadPercent}% | top: " +
            string.Join(", ", s.Apps.Take(3).Select(a => $"{a.DisplayName} {a.MemoryMb:N0}MB")));
        m.Alert += f => Print(f);
        m.Start();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        break;
    }

    case "ramtest":
    {
        // ramtest [GB] [passagens]
        var gb = double.Parse(args.ElementAtOrDefault(1) ?? "1", System.Globalization.CultureInfo.InvariantCulture);
        var passes = int.Parse(args.ElementAtOrDefault(2) ?? "1");
        var tester = new PCOptimizer.Core.Memory.RamTester();
        tester.Progress += p => Console.Write($"\rPassagem {p.Pass} · {p.Test,-34} {p.PassPercent,5:N1}% · {p.GbPerSecond,5:N1} GB/s · erros {p.Errors}   ");
        var result = await tester.RunAsync(new((long)(gb * (1L << 30)), passes, Environment.ProcessorCount), CancellationToken.None);
        Console.WriteLine($"\nTestados {result.BytesTested / (double)(1L << 30):N1} GB, {result.PassesCompleted} passagem(ns), {result.Elapsed:mm\\:ss}, erros: {result.Errors}, memória presa: {result.AllLocked}");
        Console.WriteLine($"Sugestão por defeito neste PC: {PCOptimizer.Core.Memory.RamTester.SuggestedBytes() / (double)(1L << 30):N1} GB");
        break;
    }

    case "report":
    {
        var findings = await DiagnosticsEngine.RunAsync();
        var html = PCOptimizer.Core.Reports.ReportBuilder.Build(findings, 42, "cli");
        var path = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "pcopt-report.html");
        File.WriteAllText(path, html, new UTF8Encoding(true));
        Console.WriteLine(path);
        break;
    }

    case "metrics":
    {
        var m = await PCOptimizer.Core.Benchmark.PcMetrics.MeasureAsync("teste", null);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(m, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var older = m with { BootSeconds = m.BootSeconds + 8, StartupApps = m.StartupApps + 6, RamUsedGb = m.RamUsedGb + 1.5, Health = 16 };
        foreach (var d in PCOptimizer.Core.Benchmark.PcMetrics.Compare(older, m with { Health = 70 }))
            Console.WriteLine($"  {d.Name,-36} {d.Before,10} -> {d.Now,10}  {d.Change} {(d.Better is null ? "" : d.Better.Value ? "melhor" : "pior")}");
        break;
    }

    case "crashes":
    {
        var dumps = CrashDumps.ReadAll(args.Length > 1 ? args[1] : null);
        foreach (var d in dumps)
        {
            Console.WriteLine($"{d.When:yyyy-MM-dd HH:mm} {d.CodeText} p1={d.Parameters[0]:X} p2={d.Parameters[1]:X}");
            Console.WriteLine($"   culpado: {d.CausedBy} ({d.CausedByHow}) — {(d.CausedBy is null ? "" : CrashDumps.Describe(d.CausedBy))}");
            Console.WriteLine($"   dica: {CrashDumps.ParameterHint(d)}");
            Console.WriteLine($"   pilha: {string.Join(", ", d.StackDrivers)}");
        }
        var v = CrashDumps.Verdict(dumps);
        Console.WriteLine();
        Console.WriteLine($"== {v.Title}");
        Console.WriteLine(v.Explanation);
        break;
    }

    case "driver-offers":
    {
        var keys = PCOptimizer.Core.Drivers.DriverManager.KeyDrivers();
        var probs = PCOptimizer.Core.Drivers.DriverManager.ProblemDevices();
        foreach (var o in await PCOptimizer.Core.Drivers.DriverDownloads.FindOffersAsync(keys, probs))
            Console.WriteLine($"  [{o.MatchKey}] {o.Source}: {o.Title} v{o.Version} {o.Date:yyyy-MM-dd} {SystemInfo.FormatBytes(o.SizeBytes)} {o.Url ?? o.CatalogId}");
        break;
    }

    case "route":
    {
        var host = args.Length > 1 ? args[1] : "1.1.1.1";
        var ip = System.Net.IPAddress.TryParse(host, out var parsed) ? parsed : (await System.Net.Dns.GetHostAddressesAsync(host)).First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        var rep = await RouteAnalyzer.RunAsync(ip, rounds: args.Length > 2 ? int.Parse(args[2]) : 25, new Progress<string>(Console.WriteLine));
        Console.WriteLine($"Destino {rep.Target} · respondeu: {(rep.ReachedTarget ? "sim" : "não")}");
        foreach (var h in rep.Hops)
            Console.WriteLine($"  {h.Ttl,2} {h.Segment,-9} {h.Address?.ToString() ?? "*",-16} {(h.Responds ? $"{h.Avg,6:N1} ms  pior {h.Worst,5:N0}  perda {h.LossPercent,3:N0}%" : "sem resposta"),-40} {h.HostName}");
        Console.WriteLine($"=> {rep.Verdict.Title}: {rep.Verdict.Detail}");
        break;
    }

    case "nic":
    {
        var nic = await NicTuning.ReadAsync();
        if (nic is null) { Console.WriteLine("Sem placa de rede ligada à internet."); break; }
        Console.WriteLine($"{nic.Description} ({nic.Name})");
        foreach (var (k, v) in nic.Values) Console.WriteLine($"  {k} = {v}");
        var plan = NicTuning.Plan(nic.Values);
        Console.WriteLine(plan.Count == 0 ? "Já está afinada." : "Mudaria:");
        foreach (var c in plan) Console.WriteLine($"  {c.Label}: {c.From} -> {c.To}");
        break;
    }

    case "nic-apply":
    case "nic-revert":
    {
        // o mesmo que os botões da app (precisa de administrador; a internet cai uns segundos)
        var nic = await NicTuning.ReadAsync() ?? throw new InvalidOperationException("Sem placa de rede.");
        if (cmd == "nic-apply") await NicTuning.ApplyAsync(nic, NicTuning.Plan(nic.Values));
        else Console.WriteLine(await NicTuning.RevertAsync(nic.Guid) ? "Reposta." : "Sem backup.");
        var after = await NicTuning.ReadAsync();
        foreach (var (k, v) in after!.Values) Console.WriteLine($"  {k} = {v}");
        break;
    }

    case "app-install":
    case "app-uninstall":
    {
        // o mesmo que a aba Apps: pcopt app-install voidtools.Everything
        var id = args.ElementAtOrDefault(1) ?? throw new ArgumentException("Falta o ID do winget.");
        var app = PCOptimizer.Core.Apps.AppCatalog.All.FirstOrDefault(a => a.Id == id) ?? new(id, id, "");
        if (cmd == "app-install") await PCOptimizer.Core.Apps.Winget.InstallAsync(app);
        else await PCOptimizer.Core.Apps.Winget.UninstallAsync(id);
        Console.WriteLine($"{cmd} {id}: ok · instalada agora: {(await PCOptimizer.Core.Apps.Winget.InstalledIdsAsync()).Contains(id)}");
        break;
    }

    case "record":
    {
        // pcopt record [segundos]: grava, marca "aconteceu agora" a meio e mostra a análise
        var secs = int.TryParse(args.ElementAtOrDefault(1), out var n) ? n : 40;
        using var rec = new PCOptimizer.Core.Monitoring.ProblemRecorder();
        rec.Sampled += s => Console.WriteLine($"{s.Time:HH:mm:ss} CPU {s.Cpu,3:N0}%  disco {s.Disk,3:N0}%  RAM livre {s.RamFreeGb:N1} GB  GPU {s.GpuLoad}% {s.GpuTempC}°C  ping {s.PingMs} ms  {string.Join(", ", s.TopApps)}");
        rec.Start("teste", null);
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, secs - 15)));
        Console.WriteLine("--- aconteceu agora ---");
        var r = await rec.MarkAsync();
        if (r is null) break;
        Console.WriteLine($"{r.Samples.Count} medições guardadas em {PCOptimizer.Core.Monitoring.ProblemRecorder.Folder}");
        foreach (var o in PCOptimizer.Core.Monitoring.RecordingAnalyzer.Analyze(r)) Console.WriteLine($"[{o.Kind}] {o.Text}");
        break;
    }

    case "games":
    {
        var games = PCOptimizer.Core.Gaming.GameCrashes.Installed();
        Console.WriteLine($"{games.Count} jogos: " + string.Join(", ", games.Select(g => $"{g.Name} ({g.Store})")));
        foreach (var (g, crashes) in PCOptimizer.Core.Gaming.GameCrashes.Find(games))
        {
            Console.WriteLine($"\n{g.Name}: {crashes.Count} crash(es)  verificar: {g.VerifyUri}");
            foreach (var c in crashes.Take(8))
                Console.WriteLine($"  {c.When:dd/MM HH:mm} {c.Exe} módulo={c.Module} código={(c.Code is uint x ? PCOptimizer.Core.Gaming.GameCrashes.CodeName(x) : "?")} → {PCOptimizer.Core.Gaming.GameCrashes.Cause(c.Module, c.Code)} [{c.Source}]");
        }
        break;
    }

    case "timeline":
    {
        var ev = PCOptimizer.Core.History.Timeline.Build();
        foreach (var e in ev.Take(40)) Console.WriteLine($"{e.When:dd/MM HH:mm} {e.Kind,-8} {e.Title} {e.Detail}");
        Console.WriteLine($"({ev.Count} eventos)\nSuspeitos:");
        foreach (var s in PCOptimizer.Core.History.Timeline.Suspects(ev)) Console.WriteLine($"  {s.Change.When:dd/MM} {s.Change.Title} — {s.Why}");
        break;
    }

    case "adblock":
    {
        // pcopt adblock on|off (precisa de administrador)
        if (args.ElementAtOrDefault(1) == "on") Console.WriteLine($"Bloqueados: {await HostsBlocker.EnableAsync():N0}");
        else if (args.ElementAtOrDefault(1) == "off") Console.WriteLine(await HostsBlocker.DisableAsync() ? "Desligado." : "Já estava desligado.");
        Console.WriteLine($"Ligado: {HostsBlocker.IsEnabled()} · domínios: {HostsBlocker.BlockedCount():N0}");
        break;
    }

    case "game-watch":
    {
        // deteta um jogo (ou um processo dado: game-watch <processo>) e mostra o servidor e a ligação ao vivo
        var seconds = args.Length > 2 ? int.Parse(args[2]) : 20;
        PCOptimizer.Core.Gaming.RunningGame? game;
        if (args.Length > 1)
        {
            var name = Path.GetFileNameWithoutExtension(args[1]);
            var procs = System.Diagnostics.Process.GetProcessesByName(name);
            game = procs.Length == 0 ? null : new(new(name, name + ".exe"), procs[0].Id, null);
            foreach (var pr in procs) pr.Dispose();
        }
        else game = PCOptimizer.Core.Gaming.GameCatalog.FindRunning(new PCOptimizer.Core.Settings.AppSettings());
        if (game is null) { Console.WriteLine("Nenhum jogo aberto."); break; }
        Console.WriteLine($"Jogo: {game.Game.Name} (PID {game.Pid}) {game.Path}");
        using var watcher = new PCOptimizer.Core.Gaming.GameTrafficWatcher(game.Pid);
        watcher.Start();
        Console.WriteLine(watcher.SeesUdp ? "A ver o tráfego UDP (administrador)." : "Sem administrador: só ligações TCP.");
        await Task.Delay(5000);
        var (traffic, secs, up, down) = watcher.Take();
        foreach (var t in traffic.OrderByDescending(t => t.Packets).Take(6))
            Console.WriteLine($"  {(t.Udp ? "UDP" : "TCP")} {t.Address}:{t.Port}  {t.Packets / secs:N1} pacotes/s");
        var server = PCOptimizer.Core.Gaming.ServerPicker.Pick(traffic, secs);
        Console.WriteLine(server is null ? "Servidor: não encontrado." : $"Servidor: {server.Address}:{server.Port} ({(server.Udp ? "jogo, UDP" : "TCP")}) · ↑{up:N1} KB/s ↓{down:N1} KB/s");
        if (server is null) break;
        using var live = new PCOptimizer.Core.Gaming.LiveLatency(server.Address);
        live.Updated += s => Console.WriteLine($"  ping {s.PingMs?.ToString("N0") ?? "--"} ms · média {s.AvgMs:N0} · perda {s.LossPercent:N0}% · jitter {s.JitterMs:N1} · {s.Quality}{(s.NearServer ? " (salto junto ao servidor)" : "")}");
        live.Start();
        await Task.Delay(seconds * 1000);
        break;
    }

    case "driver-install":
    {
        // o mesmo caminho do botão Descarregar da app: ponto de restauro, fonte oficial, assinatura, instalação silenciosa
        if (args.Length < 2) { Console.WriteLine("Uso: driver-install <id>  (o id entre [ ] em driver-offers)"); break; }
        var keys = PCOptimizer.Core.Drivers.DriverManager.KeyDrivers();
        var probs = PCOptimizer.Core.Drivers.DriverManager.ProblemDevices();
        var offer = (await PCOptimizer.Core.Drivers.DriverDownloads.FindOffersAsync(keys, probs))
            .FirstOrDefault(o => o.MatchKey.Equals(args[1], StringComparison.OrdinalIgnoreCase));
        if (offer is null) { Console.WriteLine("Não há nenhuma oferta com esse id."); Environment.ExitCode = 2; break; }
        Console.WriteLine($"{offer.Title} v{offer.Version} · {offer.Source} · {SystemInfo.FormatBytes(offer.SizeBytes)}");
        Console.WriteLine(await TweakEngine.CreateRestorePointAsync("Rigwise: antes de instalar " + offer.Title));
        var last = -10.0;
        var (ok, reboot, message) = await PCOptimizer.Core.Drivers.DriverDownloads.DownloadAndInstallAsync(offer,
            new Progress<(double Percent, string Text)>(p => { if (p.Percent - last >= 10 || p.Percent >= 100) { last = p.Percent; Console.WriteLine(p.Text); } }),
            CancellationToken.None);
        Console.WriteLine(message + (reboot ? " (precisa de reiniciar)" : ""));
        Environment.ExitCode = ok ? 0 : 1;
        break;
    }

    case "drivers":
    {
        Console.WriteLine("== Dispositivos com problemas ==");
        foreach (var d in PCOptimizer.Core.Drivers.DriverManager.ProblemDevices())
            Console.WriteLine($"  [{d.Kind}] {d.Name} ({d.Code}) {d.Explanation}  {d.HardwareId}");
        Console.WriteLine("== Drivers importantes ==");
        foreach (var k in PCOptimizer.Core.Drivers.DriverManager.KeyDrivers())
            Console.WriteLine($"  [{k.Status,-8}] {k.Category}: {k.Device} | {k.Provider} {k.Version} {k.Date:yyyy-MM-dd} | {k.Advice} | {k.UrlLabel} {k.Url}");
        Console.WriteLine("== Versões antigas guardadas ==");
        foreach (var p in PCOptimizer.Core.Drivers.DriverManager.OldPackages())
            Console.WriteLine($"  {p.PublishedName} {p.OriginalName} {p.Provider} {p.Version} (nova {p.NewerVersion}) {SystemInfo.FormatBytes(p.SizeBytes)}");
        Console.WriteLine("== Windows Update ==");
        foreach (var u in await PCOptimizer.Core.Drivers.DriverManager.SearchWindowsUpdateAsync())
            Console.WriteLine($"  {u.Title} | {u.Class} | {u.Date:yyyy-MM-dd} | {SystemInfo.FormatBytes(u.SizeBytes)}");
        break;
    }

    case "disk":
    {
        foreach (var d in await PCOptimizer.Core.Storage.StorageDrives.ListAsync())
            Console.WriteLine($"{d.Letter} {d.Label,-14} {d.Media,-9} {d.Model,-28} {SystemInfo.FormatBytes(d.UsedBytes)} / {SystemInfo.FormatBytes(d.TotalBytes)} ({d.UsedPercent:N0}%)");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var apps = await PCOptimizer.Core.Storage.AppInventory.ScanAsync();
        Console.WriteLine($"\n{apps.Count} itens em {sw.Elapsed.TotalSeconds:N1} s");
        foreach (var g in apps.GroupBy(a => a.Source)) Console.WriteLine($"  {g.Key}: {g.Count()}");
        foreach (var g in apps.GroupBy(a => a.Verdict)) Console.WriteLine($"  {g.Key}: {g.Count()}");
        Console.WriteLine($"  com data de uso: {apps.Count(a => a.LastUsed != null)}  com tamanho: {apps.Count(a => a.SizeBytes != null)}");
        Console.WriteLine("\nMais inúteis:");
        foreach (var a in apps.OrderBy(a => a.Utility).ThenByDescending(a => a.SizeBytes).Take(15))
            Console.WriteLine($"  [{a.Utility,3}] {a.Name,-45} {a.Drive} {(a.SizeBytes is long s ? SystemInfo.FormatBytes(s) : "?"),9}  {a.LastUsed:yyyy-MM-dd}  {a.Source}");
        Console.WriteLine("\nMais usados:");
        foreach (var a in apps.Where(a => a.Verdict != PCOptimizer.Core.Storage.AppVerdict.Essential).OrderByDescending(a => a.LastUsed).Take(8))
            Console.WriteLine($"  [{a.Utility,3}] {a.Name,-45} {a.LastUsed:yyyy-MM-dd}");
        break;
    }

    case "scan":
    {
        var root = args.ElementAtOrDefault(1) ?? @"C:\Program Files";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var node = await PCOptimizer.Core.Storage.FolderScanner.ScanAsync(root, null, CancellationToken.None);
        Console.WriteLine($"{root}: {SystemInfo.FormatBytes(node.Size)} em {node.Files:N0} ficheiros ({sw.Elapsed.TotalSeconds:N1} s)");
        foreach (var c in node.Children.Take(8)) Console.WriteLine($"  {SystemInfo.FormatBytes(c.Size),10}  {c.Name}");
        break;
    }

    case "advise":
    {
        var root = args.ElementAtOrDefault(1) ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var apps = await PCOptimizer.Core.Storage.AppInventory.ScanAsync();
        var advisor = new PCOptimizer.Core.Storage.FolderAdvisor(apps);
        var node = await PCOptimizer.Core.Storage.FolderScanner.ScanAsync(root, null, CancellationToken.None);
        Console.WriteLine($"{root}: {SystemInfo.FormatBytes(node.Size)} · seguro de apagar: {SystemInfo.FormatBytes(advisor.SafeBytes(node))}");
        foreach (var c in node.Children.Take(int.Parse(args.ElementAtOrDefault(2) ?? "12")))
        {
            var a = advisor.Advise(c);
            Console.WriteLine($"  {SystemInfo.FormatBytes(c.Size),9}  [{a.Safety,-7}] {c.Name,-40} {a.What}  (seguro aqui dentro: {SystemInfo.FormatBytes(advisor.SafeBytes(c))})");
        }
        break;
    }

    case "ports":
    {
        var s = PortUsage.Take(8);
        Console.WriteLine($"TCP: {s.TotalTcp}  UDP: {s.TotalUdp}");
        foreach (var h in s.Top) Console.WriteLine($"  {h.Name,-24} TCP {h.Tcp,6}  UDP {h.Udp,6}");
        Console.WriteLine($"Esgotamentos nos últimos 30 dias: {ConnectionWatcher.PastPortExhaustion().Count}");
        break;
    }

    case "stab-check":
    {
        Console.WriteLine($"Administrador: {Admin.IsElevated}");
        Console.WriteLine($"Acesso à memória física (SeLockMemoryPrivilege): {PCOptimizer.Core.Memory.PhysicalRamTester.TryEnableLockPagesPrivilege()}");
        if (Admin.IsElevated)
            Console.WriteLine($"Páginas já isoladas: {(await PCOptimizer.Core.Memory.BadMemoryList.ReadAsync()).Count}");
        break;
    }

    case "maintenance":
    {
        // corre a manutenção automática toda (só lê); usa definições à parte para não "gastar" os avisos da app
        Environment.SetEnvironmentVariable("PCOPT_SETTINGS_FILE", Path.Combine(Path.GetTempPath(), "pcopt-cli-maintenance.json"));
        var s = new PCOptimizer.Core.Settings.AppSettings { AutoMaintenance = true };
        var notices = await PCOptimizer.Core.Maintenance.MaintenanceRunner.RunAsync(s, new Progress<string>(Console.WriteLine));
        if (notices.Count == 0) Console.WriteLine("Nada a avisar.");
        foreach (var n in notices)
        {
            Console.WriteLine($"[{(n.Important ? "!" : "i")}] {n.Title}  (abre: {n.Tab})");
            Console.WriteLine(n.Text);
            Console.WriteLine();
        }
        break;
    }

    default:
        Console.WriteLine("Comandos: diag | tweaks | startup | net [host] | dns | bloat | leak | monitor [segundos] | drivers | driver-offers | crashes | metrics | report | maintenance");
        Console.WriteLine($"Administrador: {(Admin.IsElevated ? "sim" : "não")}");
        break;
}

static void Print(Finding f)
{
    var tag = f.Severity switch
    {
        Severity.Critical => "CRÍTICO",
        Severity.Warning => "AVISO",
        Severity.Info => "INFO",
        _ => "OK",
    };
    Console.WriteLine($"\n[{tag}] ({f.Category}) {f.Title}\n  {f.Description.Replace("\n", "\n  ")}");
    if (f.Actions.Count > 0) Console.WriteLine("  Ações: " + string.Join(" | ", f.Actions.Select(a => a.Label)));
}
