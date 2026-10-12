using Microsoft.Win32;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Optimization;

public sealed class OptimizationProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Icon { get; init; }
    public required string Summary { get; init; }
    public string? Notes { get; init; }
    public required string[] TweakIds { get; init; }

    public IEnumerable<Tweak> Tweaks => TweakIds.Select(TweakCatalog.Get);
}

/// <summary>Todos os ajustes que a app conhece, e os perfis que os agrupam.</summary>
public static class TweakCatalog
{
    const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    const string Multimedia = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    const string Games = Multimedia + @"\Tasks\Games";
    const string ContentDelivery = @"HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    const string ActiveAdapters = "$ad = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object Status -eq 'Up')";

    public static IReadOnlyList<Tweak> All { get; } = new Tweak[]
    {
        // ---------------- Energia ----------------
        new PowerPlanTweak
        {
            Id = "power.ultimate", Target = PowerPlanTweak.Ultimate, SharedSnapshotKey = "power.plan",
            Name = Loc.T("Plano de energia: Desempenho Máximo"),
            Description = Loc.T("Cria e ativa o plano \"Desempenho Máximo\". A CPU deixa de reduzir a velocidade para poupar energia. Gasta mais eletricidade e, em portáteis, mais bateria."),
        },
        new PowerPlanTweak
        {
            Id = "power.balanced", Target = PowerPlanTweak.Balanced, SharedSnapshotKey = "power.plan", RequiresAdmin = false,
            Name = Loc.T("Plano de energia: Equilibrado"),
            Description = Loc.T("O plano recomendado: rápido quando precisas, poupa energia quando o PC está parado."),
        },
        new PowerSettingTweak
        {
            Id = "power.cpu_min100", Subgroup = SubProcessor, Setting = "893dee8e-2bef-41e0-89c6-b55d0929964c", AcValue = 100,
            Name = Loc.T("CPU sempre à velocidade máxima"),
            Description = Loc.T("Estado mínimo do processador = 100% quando ligado à corrente. Elimina o atraso de \"acordar\" a CPU. Mais calor e consumo."),
        },
        new PowerSettingTweak
        {
            Id = "power.core_parking_off", Subgroup = SubProcessor, Setting = "0cc5b647-c1df-4637-891a-dec35c318583", AcValue = 100,
            Name = Loc.T("Desligar o estacionamento de núcleos"),
            Description = Loc.T("Mantém todos os núcleos da CPU acordados. Reduz picos de latência (estalos no áudio, micro-stutter)."),
        },
        new PowerSettingTweak
        {
            Id = "power.usb_suspend_off", Subgroup = "2a737441-1930-4402-8d77-b2bebba308a3", Setting = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", AcValue = 0,
            Name = Loc.T("Desligar a suspensão seletiva USB"),
            Description = Loc.T("Impede o Windows de \"adormecer\" as portas USB. Evita cortes em interfaces de áudio, MIDI e periféricos."),
        },
        new PowerSettingTweak
        {
            Id = "power.pcie_aspm_off", Subgroup = "501a4d13-42af-4429-9fd1-a8218c268e20", Setting = "ee12f906-d277-404b-b6da-e5fa1a576df5", AcValue = 0,
            Name = Loc.T("Desligar a poupança de energia PCI Express"),
            Description = Loc.T("Desativa o Link State Power Management. Reduz a latência de placas de som, gráficas e SSD NVMe."),
        },
        new PowerSettingTweak
        {
            Id = "power.disk_sleep_off", Subgroup = "0012ee47-9041-4b5d-9b77-535fba8b1442", Setting = "6738e2c4-e8a5-4a42-b16a-e040e769756e", AcValue = 0,
            Name = Loc.T("Os discos nunca adormecem"),
            Description = Loc.T("Evita a pausa de alguns segundos quando um disco tem de \"acordar\" a meio de uma gravação ou de um render."),
        },

        // ---------------- Gaming ----------------
        new RegistryTweak
        {
            Id = "game.gamemode", RequiresAdmin = false,
            Name = Loc.T("Ativar o Modo de Jogo do Windows"),
            Description = Loc.T("O Windows dá prioridade ao jogo e não instala atualizações nem mostra notificações enquanto jogas."),
            Values = new[]
            {
                new RegValue(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, RegistryValueKind.DWord),
                new RegValue(@"HKCU\Software\Microsoft\GameBar", "AllowAutoGameMode", 1, RegistryValueKind.DWord),
            },
        },
        new RegistryTweak
        {
            Id = "game.hags", RequiresReboot = true,
            Name = Loc.T("Agendamento de GPU acelerado por hardware"),
            Description = Loc.T("A placa gráfica gere a própria memória. Reduz a latência e é necessário para o DLSS Frame Generation. Requer GPU recente (NVIDIA GTX 10xx+, AMD RX 5000+)."),
            Values = new[] { new RegValue(@"HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, RegistryValueKind.DWord) },
        },
        new RegistryTweak
        {
            Id = "game.dvr_off", RequiresAdmin = false,
            Name = Loc.T("Desligar a gravação em segundo plano (Game DVR)"),
            Description = Loc.T("A Xbox Game Bar pode estar sempre a gravar os últimos minutos de jogo, o que gasta GPU e disco. Podes continuar a gravar manualmente."),
            Values = new[]
            {
                new RegValue(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", 0, RegistryValueKind.DWord),
                new RegValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, RegistryValueKind.DWord),
            },
        },
        new RegistryTweak
        {
            Id = "game.mouse_accel_off", RequiresAdmin = false, RequiresReboot = true, DefaultSelected = false,
            Name = Loc.T("Desligar a aceleração do rato"),
            Description = Loc.T("O cursor anda sempre a mesma distância para o mesmo movimento da mão: melhor pontaria. Tem efeito depois de terminar sessão."),
            Values = new[]
            {
                new RegValue(@"HKCU\Control Panel\Mouse", "MouseSpeed", "0", RegistryValueKind.String),
                new RegValue(@"HKCU\Control Panel\Mouse", "MouseThreshold1", "0", RegistryValueKind.String),
                new RegValue(@"HKCU\Control Panel\Mouse", "MouseThreshold2", "0", RegistryValueKind.String),
            },
        },
        new RegistryTweak
        {
            Id = "sys.responsiveness", RequiresReboot = true,
            Name = Loc.T("Mais CPU para jogos e áudio"),
            Description = Loc.T("Reduz de 20% para 10% a CPU reservada a tarefas de fundo enquanto um jogo ou app de áudio está ativo (serviço MMCSS)."),
            Values = new[] { new RegValue(Multimedia, "SystemResponsiveness", 10, RegistryValueKind.DWord) },
        },
        new RegistryTweak
        {
            Id = "game.mmcss_games", RequiresReboot = true,
            Name = Loc.T("Prioridade alta para jogos (MMCSS)"),
            Description = Loc.T("Dá aos jogos mais prioridade de CPU, GPU e disco no agendador multimédia do Windows."),
            Values = new[]
            {
                new RegValue(Games, "GPU Priority", 8, RegistryValueKind.DWord),
                new RegValue(Games, "Priority", 6, RegistryValueKind.DWord),
                new RegValue(Games, "Scheduling Category", "High", RegistryValueKind.String),
                new RegValue(Games, "SFIO Priority", "High", RegistryValueKind.String),
            },
        },
        new RegistryTweak
        {
            Id = "game.priority_sep",
            Name = Loc.T("Mais prioridade para a janela ativa"),
            Description = Loc.T("Ajusta o agendador do Windows (Win32PrioritySeparation = 0x26): a app em primeiro plano recebe fatias de CPU mais frequentes."),
            Values = new[] { new RegValue(@"HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26, RegistryValueKind.DWord) },
        },
        new RegistryTweak
        {
            Id = "game.vbs_off", Risk = Risk.High, RequiresReboot = true, DefaultSelected = false,
            Name = Loc.T("Desligar a Integridade da Memória (VBS/HVCI)"),
            Description = Loc.T("Pode dar +5–10% de FPS, mas desliga uma proteção importante contra malware no kernel. Alguns anti-cheats podem exigir que esteja ligada."),
            Values = new[] { new RegValue(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0, RegistryValueKind.DWord) },
        },

        // ---------------- Rede ----------------
        new RegistryTweak
        {
            Id = "net.throttling_off", RequiresReboot = true,
            Name = Loc.T("Desligar a limitação de rede multimédia"),
            Description = Loc.T("Por defeito o Windows limita o processamento de pacotes de rede enquanto toca multimédia (NetworkThrottlingIndex)."),
            Values = new[] { new RegValue(Multimedia, "NetworkThrottlingIndex", unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord) },
        },
        new PowerShellTweak
        {
            Id = "net.nic_power_off", RequiresReboot = true,
            Name = Loc.T("A placa de rede nunca adormece"),
            Description = Loc.T("Impede o Windows de desligar a placa de rede para poupar energia, o que causa cortes e picos de ping."),
            Prelude = ActiveAdapters,
            Check = """
                $pm = @($ad | Get-NetAdapterPowerManagement -ErrorAction SilentlyContinue)
                if ($pm.Count -eq 0) { 'NA' } else { [string](@($pm | Where-Object { "$($_.AllowComputerToTurnOffDevice)" -eq 'Enabled' }).Count -eq 0) }
                """,
            Capture = """(@($ad | Get-NetAdapterPowerManagement -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name)=$($_.AllowComputerToTurnOffDevice)" })) -join ';'""",
            Apply = """
                foreach ($p in @($ad | Get-NetAdapterPowerManagement -ErrorAction SilentlyContinue)) {
                  if ("$($p.AllowComputerToTurnOffDevice)" -eq 'Enabled') { $p.AllowComputerToTurnOffDevice = 'Disabled'; $p | Set-NetAdapterPowerManagement -NoRestart }
                }
                """,
            Revert = """
                foreach ($kv in ($state -split ';')) {
                  if (-not $kv) { continue }
                  $n, $v = $kv -split '=', 2
                  if ($v -eq 'Enabled') {
                    $p = Get-NetAdapterPowerManagement -Name $n -ErrorAction SilentlyContinue
                    if ($p) { $p.AllowComputerToTurnOffDevice = 'Enabled'; $p | Set-NetAdapterPowerManagement -NoRestart }
                  }
                }
                """,
        },
        new PowerShellTweak
        {
            Id = "net.eee_off", RequiresReboot = true,
            Name = Loc.T("Desligar o Energy Efficient Ethernet"),
            Description = Loc.T("Esta poupança de energia do cabo de rede adiciona pequenos atrasos ao \"acordar\" a ligação. Desligá-la estabiliza o ping."),
            Prelude = ActiveAdapters,
            Check = """
                $p = @($ad | Get-NetAdapterAdvancedProperty -RegistryKeyword '*EEE' -ErrorAction SilentlyContinue)
                if ($p.Count -eq 0) { 'NA' } else { [string](@($p | Where-Object { "$($_.RegistryValue)" -ne '0' }).Count -eq 0) }
                """,
            Capture = """(@($ad | Get-NetAdapterAdvancedProperty -RegistryKeyword '*EEE' -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name)=$($_.RegistryValue)" })) -join ';'""",
            Apply = """
                foreach ($p in @($ad | Get-NetAdapterAdvancedProperty -RegistryKeyword '*EEE' -ErrorAction SilentlyContinue)) {
                  if ("$($p.RegistryValue)" -ne '0') { Set-NetAdapterAdvancedProperty -Name $p.Name -RegistryKeyword '*EEE' -RegistryValue '0' -NoRestart }
                }
                """,
            Revert = """
                foreach ($kv in ($state -split ';')) {
                  if (-not $kv) { continue }
                  $n, $v = $kv -split '=', 2
                  if ($v) { Set-NetAdapterAdvancedProperty -Name $n -RegistryKeyword '*EEE' -RegistryValue $v -NoRestart }
                }
                """,
        },
        new PowerShellTweak
        {
            Id = "net.dynports", Risk = Risk.Medium,
            Name = Loc.T("Mais portas de rede (contra quebras de ligação)"),
            Description = Loc.T("Aumenta as portas de rede que as apps podem usar de 16 384 para 55 535. Se uma app abrir ligações a mais, o Windows deixa de ficar sem portas, que é o que faz a internet \"cair\" uns segundos. Muito raramente, pode colidir com um programa que use uma porta fixa acima de 10000."),
            Prelude = """
                function Get-Range($proto) { $o = netsh int ipv4 show dynamicport $proto | Out-String; $n = @([regex]::Matches($o, '\d+') | ForEach-Object { $_.Value }); "$($n[-2]),$($n[-1])" }
                """,
            Check = """if ((Get-Range udp) -eq '10000,55535' -and (Get-Range tcp) -eq '10000,55535') { 'True' } else { 'False' }""",
            Capture = """ "$(Get-Range udp);$(Get-Range tcp)" """,
            Apply = """
                foreach ($proto in 'tcp','udp') { foreach ($ip in 'ipv4','ipv6') {
                  netsh int $ip set dynamicport $proto start=10000 num=55535 | Out-Null
                  if ($LASTEXITCODE -ne 0) { throw "netsh falhou ($ip $proto)" }
                } }
                """,
            Revert = """
                $u, $t = $state -split ';'
                foreach ($pair in @(@('udp', $u), @('tcp', $t))) {
                  $s, $n = $pair[1] -split ','
                  if ($s -and $n) { foreach ($ip in 'ipv4','ipv6') { netsh int $ip set dynamicport $pair[0] start=$s num=$n | Out-Null } }
                }
                """,
        },
        new RegistryTweak
        {
            Id = "net.delivery_opt_off",
            Name = Loc.T("Não partilhar atualizações com outros PCs"),
            Description = Loc.T("Desliga o envio de partes de atualizações do Windows para outros PCs na internet. Poupa upload, que afeta muito o ping."),
            Values = new[] { new RegValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0, RegistryValueKind.DWord) },
        },

        // ---------------- Vídeo ----------------
        new PowerShellTweak
        {
            Id = "video.defender_exclusions", Risk = Risk.Medium,
            Name = Loc.T("Excluir as caches de vídeo do antivírus"),
            Description = Loc.T("O Microsoft Defender deixa de analisar as pastas de cache do Premiere Pro/After Effects e do DaVinci Resolve. A leitura de media fica muito mais rápida. Os teus downloads continuam protegidos."),
            Prelude = """
                $want = @("$env:APPDATA\Adobe\Common", "$env:APPDATA\Blackmagic Design\DaVinci Resolve\Support") | Where-Object { Test-Path $_ }
                $want = @($want)
                $cur = @()
                try { $cur = @((Get-MpPreference -ErrorAction Stop).ExclusionPath) } catch {}
                """,
            Check = """if ($want.Count -eq 0) { 'NA' } else { [string](@($want | Where-Object { $cur -notcontains $_ }).Count -eq 0) }""",
            Capture = """@($want | Where-Object { $cur -contains $_ }) -join ';'""",
            Apply = """
                if ($want.Count -eq 0) { throw 'Não encontrei pastas de cache do Premiere/After Effects/DaVinci neste PC.' }
                Add-MpPreference -ExclusionPath $want
                """,
            Revert = """
                $keep = $state -split ';'
                $rm = @($want | Where-Object { $keep -notcontains $_ })
                if ($rm.Count) { Remove-MpPreference -ExclusionPath $rm }
                """,
        },

        // ---------------- Desktop ----------------
        new RegistryTweak
        {
            Id = "desk.suggestions_off", RequiresAdmin = false,
            Name = Loc.T("Sem apps \"sugeridas\" nem anúncios"),
            Description = Loc.T("Impede o Windows de instalar sozinho apps sugeridas (jogos, etc.) e de mostrar sugestões no menu Iniciar e nas Definições."),
            Values = new[]
            {
                new RegValue(ContentDelivery, "SilentInstalledAppsEnabled", 0, RegistryValueKind.DWord),
                new RegValue(ContentDelivery, "SystemPaneSuggestionsEnabled", 0, RegistryValueKind.DWord),
                new RegValue(ContentDelivery, "SubscribedContent-338388Enabled", 0, RegistryValueKind.DWord),
                new RegValue(ContentDelivery, "SubscribedContent-338389Enabled", 0, RegistryValueKind.DWord),
                new RegValue(ContentDelivery, "SubscribedContent-353694Enabled", 0, RegistryValueKind.DWord),
                new RegValue(ContentDelivery, "SubscribedContent-353696Enabled", 0, RegistryValueKind.DWord),
            },
        },
        new RegistryTweak
        {
            Id = "desk.search_web_off", RequiresReboot = true,
            Name = Loc.T("Pesquisa do Iniciar só no PC"),
            Description = Loc.T("A pesquisa do menu Iniciar deixa de mostrar resultados do Bing: fica mais rápida e não envia o que escreves para a internet."),
            Values = new[] { new RegValue(@"HKCU\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, RegistryValueKind.DWord) },
        },

        // ---------------- Privacidade ----------------
        new RegistryTweak
        {
            Id = "priv.telemetry",
            Name = Loc.T("Telemetria no mínimo"),
            Description = Loc.T("O Windows envia à Microsoft só os dados de diagnóstico obrigatórios."),
            Values = new[] { new RegValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, RegistryValueKind.DWord) },
        },
        new RegistryTweak
        {
            Id = "priv.ad_id", RequiresAdmin = false,
            Name = Loc.T("Sem ID de publicidade"),
            Description = Loc.T("As apps deixam de usar um identificador teu para mostrar anúncios personalizados."),
            Values = new[] { new RegValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, RegistryValueKind.DWord) },
        },
        new RegistryTweak
        {
            Id = "priv.activity_off",
            Name = Loc.T("Sem histórico de atividade"),
            Description = Loc.T("O Windows deixa de guardar e enviar a lista das apps e ficheiros que abres."),
            Values = new[]
            {
                new RegValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 0, RegistryValueKind.DWord),
                new RegValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, RegistryValueKind.DWord),
                new RegValue(@"HKLM\SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, RegistryValueKind.DWord),
            },
        },
        new RegistryTweak
        {
            Id = "priv.tailored_off", RequiresAdmin = false,
            Name = Loc.T("Sem dicas nem \"experiências personalizadas\""),
            Description = Loc.T("Desliga as dicas do Windows e as sugestões baseadas nos teus dados de diagnóstico."),
            Values = new[]
            {
                new RegValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, RegistryValueKind.DWord),
                new RegValue(ContentDelivery, "SoftLandingEnabled", 0, RegistryValueKind.DWord),
                new RegValue(ContentDelivery, "SubscribedContent-338393Enabled", 0, RegistryValueKind.DWord),
                new RegValue(ContentDelivery, "SubscribedContent-353698Enabled", 0, RegistryValueKind.DWord),
            },
        },
        new RegistryTweak
        {
            Id = "priv.feedback_off", RequiresAdmin = false,
            Name = Loc.T("Sem pedidos de feedback"),
            Description = Loc.T("O Windows deixa de te perguntar a opinião em janelas de notificação."),
            Values = new[] { new RegValue(@"HKCU\Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, RegistryValueKind.DWord) },
        },
        new RegistryTweak
        {
            Id = "priv.copilot_off", DefaultSelected = false,
            Name = Loc.T("Desligar o Copilot"),
            Description = Loc.T("Esconde o botão e desativa o Copilot do Windows."),
            Values = new[]
            {
                new RegValue(@"HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1, RegistryValueKind.DWord),
                new RegValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 0, RegistryValueKind.DWord),
            },
        },
    };

    public static IReadOnlyList<OptimizationProfile> Profiles { get; } = new[]
    {
        new OptimizationProfile
        {
            Id = "gaming", Icon = "🎮", Name = "Gaming",
            Summary = Loc.T("Máximo de FPS e mínimo de latência."),
            Notes = Loc.T("Os ajustes de risco alto vêm desligados. Antes de jogar, fecha o browser e as apps de fundo: a aba Monitor mostra o que está a gastar recursos. Para o ping, usa também o perfil Rede."),
            TweakIds = new[] { "power.ultimate", "game.gamemode", "game.hags", "game.dvr_off", "sys.responsiveness", "game.mmcss_games", "game.priority_sep", "net.throttling_off", "game.mouse_accel_off", "game.vbs_off" },
        },
        new OptimizationProfile
        {
            Id = "network", Icon = "🌐", Name = Loc.T("Rede / Jogos online"),
            Summary = Loc.T("Menos ping, jitter e perda de pacotes."),
            Notes = Loc.T("Estes ajustes tratam do teu PC. Para descobrir se o lag vem do Wi-Fi, do router ou do ISP, usa o teste da aba Rede."),
            TweakIds = new[] { "net.dynports", "net.throttling_off", "net.nic_power_off", "net.eee_off", "net.delivery_opt_off" },
        },
        new OptimizationProfile
        {
            Id = "video", Icon = "🎬", Name = Loc.T("Edição de vídeo"),
            Summary = Loc.T("Renders e pré-visualização mais rápidos."),
            Notes = Loc.T("Dicas: guarda os projetos e a cache num SSD NVMe, e se tiveres uma placa NVIDIA usa o driver \"Studio\" em vez do \"Game Ready\"."),
            TweakIds = new[] { "power.ultimate", "power.cpu_min100", "power.disk_sleep_off", "video.defender_exclusions", "game.hags", "game.dvr_off" },
        },
        new OptimizationProfile
        {
            Id = "music", Icon = "🎵", Name = Loc.T("Produção de música"),
            Summary = Loc.T("Latência de áudio baixa, sem estalos."),
            Notes = Loc.T("Usa sempre o driver ASIO da tua interface de áudio. Se continuares a ouvir estalos, corre o LatencyMon (gratuito) para descobrir o driver culpado."),
            TweakIds = new[] { "power.ultimate", "power.cpu_min100", "power.core_parking_off", "power.usb_suspend_off", "power.pcie_aspm_off", "power.disk_sleep_off", "sys.responsiveness", "game.dvr_off" },
        },
        new OptimizationProfile
        {
            Id = "desktop", Icon = "🖥", Name = "Desktop / Trabalho / Bateria",
            Summary = Loc.T("Rápido no dia a dia e poupa energia."),
            Notes = Loc.T("Vê também a aba Arranque: desativar programas que arrancam com o Windows é o ajuste com mais impacto no dia a dia."),
            TweakIds = new[] { "power.balanced", "desk.suggestions_off", "desk.search_web_off", "game.dvr_off", "net.delivery_opt_off" },
        },
        new OptimizationProfile
        {
            Id = "privacy", Icon = "🔒", Name = Loc.T("Privacidade"),
            Summary = Loc.T("Menos dados enviados e sem anúncios no Windows."),
            TweakIds = new[] { "priv.telemetry", "priv.ad_id", "priv.activity_off", "priv.tailored_off", "priv.feedback_off", "desk.suggestions_off", "desk.search_web_off", "priv.copilot_off" },
        },
    };

    public static Tweak Get(string id) => All.First(t => t.Id == id);
}
