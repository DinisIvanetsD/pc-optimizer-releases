using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PCOptimizer.Core.History;
using PCOptimizer.Core.Util;

namespace PCOptimizer.Core.Storage;

public enum FolderSafety
{
    /// <summary>Cache/temporários: voltam a ser criados sozinhos.</summary>
    Safe,
    /// <summary>Provavelmente seguro (ex.: restos de um programa já desinstalado).</summary>
    Likely,
    /// <summary>Dados de apps ou ficheiros pessoais: apagar tem consequências.</summary>
    Caution,
    /// <summary>Windows, programas instalados, pontos de restauro: não apagar.</summary>
    Danger,
    /// <summary>Não sei: na dúvida, não apagar.</summary>
    Unknown,
}

public sealed record FolderAdvice(FolderSafety Safety, string What, string Explanation)
{
    public bool CanRecycle => Safety is FolderSafety.Safe or FolderSafety.Likely;
}

/// <summary>
/// Diz o que é uma pasta e se é seguro apagá-la, com regras baseadas no caminho (as pastas do
/// Windows têm sítios e nomes bem conhecidos) e na lista de programas instalados.
/// </summary>
public sealed class FolderAdvisor
{
    readonly Dictionary<string, string> _appByFolder = new(StringComparer.OrdinalIgnoreCase);   // pasta de instalação → nome
    readonly Dictionary<string, string> _appByPackage = new(StringComparer.OrdinalIgnoreCase);  // "Claude_pzs8sxrjxfjjc" → "Claude"
    readonly HashSet<string> _appWords = new(StringComparer.OrdinalIgnoreCase);                   // nomes/editoras de programas instalados
    readonly Dictionary<string, long> _safeBytes = new(StringComparer.OrdinalIgnoreCase);

    static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    static readonly string UsersRoot = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? @"C:\Users";

    static readonly Regex CacheName = new(
        @"^(cache|caches|code cache|gpucache|grshadercache|graphitedawncache|shadercache|shader ?cache|dxcache|glcache|d3dscache|dxccache|" +
        @"inetcache|webcache|htmlcache|crashdumps|crash ?reports|crashpad|logs?|temp|tmp|tempstate|cachestorage|scriptcache|" +
        @"blob_storage|service worker|component_crx_cache|optimization_guide_model_store|squirrel ?temp|package ?cache ?temp)$",
        RegexOptions.IgnoreCase);

    public FolderAdvisor(IEnumerable<InstalledApp> apps)
    {
        foreach (var a in apps)
        {
            if (a.InstallLocation != null) _appByFolder[a.InstallLocation.TrimEnd('\\')] = a.Name;
            if (a.PackageFamilyName != null) _appByPackage[a.PackageFamilyName] = a.Name;
            foreach (var w in Words(a.Name).Concat(Words(a.Publisher))) _appWords.Add(w);
        }
    }

    static IEnumerable<string> Words(string s) =>
        Regex.Split(s, @"[^\p{L}\p{N}]+").Where(w => w.Length >= 4 && !Regex.IsMatch(w, @"^(corporation|software|microsoft|games?|inc|ltd|technologies|version|update|edition)$", RegexOptions.IgnoreCase));

    public FolderAdvice Advise(DirNode node)
    {
        if (node.IsFilesGroup)
            return new(FolderSafety.Unknown, Loc.T("Ficheiros soltos"), Loc.T("Ficheiros que estão diretamente nesta pasta (não numa subpasta)."));

        var path = node.FullPath.TrimEnd('\\');
        var name = node.Name;
        var root = Path.GetPathRoot(path)?.TrimEnd('\\') ?? "";
        bool Under(string folder) => path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || path.Equals(folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        bool Is(string folder) => path.Equals(folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        // ---- pastas especiais na raiz do disco ----
        if (Is(root + @"\$Recycle.Bin")) return new(FolderSafety.Safe, "Reciclagem", Loc.T("Ficheiros que já apagaste. Esvazia-a em Limpeza rápida."));
        if (Is(root + @"\System Volume Information")) return new(FolderSafety.Danger, Loc.T("Pontos de restauro do Windows"), Loc.T("Guarda os pontos de restauro e cópias de segurança do sistema. Gere o espaço em Painel de Controlo › Sistema › Proteção do sistema."));
        if (Is(root + @"\Recovery") || Is(root + @"\$WinREAgent") || Is(root + @"\Boot")) return new(FolderSafety.Danger, Loc.T("Recuperação do Windows"), Loc.T("Necessária para reparar ou reinstalar o Windows. Não apagues."));
        if (Is(root + @"\Windows.old")) return new(FolderSafety.Safe, Loc.T("Windows antigo"), "Cópia da versão anterior do Windows. Remove-a com a Limpeza de Disco (em Limpeza rápida), não à mão.");
        if (Is(root + @"\PerfLogs")) return new(FolderSafety.Likely, Loc.T("Relatórios de desempenho"), Loc.T("Relatórios antigos do Monitor de Desempenho. Normalmente vazia e inútil."));
        if (Is(root + @"\$SysReset") || Is(root + @"\$Windows.~BT") || Is(root + @"\$Windows.~WS")) return new(FolderSafety.Likely, Loc.T("Restos de atualizações do Windows"), "Ficheiros temporários de uma atualização ou reposição do Windows. Remove-os com a Limpeza de Disco.");

        // ---- Windows ----
        if (Under(Windows))
        {
            if (Under(Path.Combine(Windows, "Temp"))) return new(FolderSafety.Safe, Loc.T("Temporários do Windows"), Loc.T("Ficheiros temporários do sistema. Seguro (limpa em Limpeza rápida)."));
            if (Under(Path.Combine(Windows, @"SoftwareDistribution\Download"))) return new(FolderSafety.Safe, Loc.T("Cache do Windows Update"), Loc.T("Atualizações já instaladas. Limpa com a Limpeza de Disco do Windows."));
            if (Under(Path.Combine(Windows, "Installer"))) return new(FolderSafety.Danger, Loc.T("Instaladores dos programas"), Loc.T("O Windows precisa destes ficheiros para atualizar, reparar e desinstalar programas. Apagar pode deixar programas impossíveis de desinstalar."));
            if (Under(Path.Combine(Windows, "WinSxS"))) return new(FolderSafety.Danger, Loc.T("Componentes do Windows"), Loc.T("Peças do próprio Windows. Para reduzir, usa \"Reparar Windows (DISM)\" ou a Limpeza de Disco, nunca à mão."));
            if (Under(Path.Combine(Windows, "Minidump"))) return new(FolderSafety.Caution, Loc.T("Relatórios de ecrãs azuis"), Loc.T("Ocupam pouco e servem para descobrir a causa dos ecrãs azuis que tens tido. Mantém, por agora."));
            return new(FolderSafety.Danger, "Windows", Loc.T("Ficheiros do sistema operativo. Não apagues nada aqui à mão."));
        }

        // ---- programas instalados (Steam, Epic, Program Files…) ----
        if (_appByFolder.TryGetValue(path, out var installed))
            return new(FolderSafety.Danger, Loc.T($"Programa instalado: {installed}"), Loc.T("Não apagues a pasta: desinstala em Programas e jogos (assim também se limpa o registo e os atalhos)."));
        if (Regex.IsMatch(path, @"\\steamapps\\shadercache(\\|$)", RegexOptions.IgnoreCase))
            return new(FolderSafety.Safe, Loc.T("Cache de shaders do Steam"), Loc.T("Volta a ser criada quando abres os jogos (o primeiro arranque fica um pouco mais lento)."));
        if (Regex.IsMatch(path, @"\\steamapps\\(downloading|temp)(\\|$)", RegexOptions.IgnoreCase))
            return new(FolderSafety.Likely, Loc.T("Downloads a meio do Steam"), Loc.T("Atualizações ou jogos que não acabaram de transferir. Seguro, se não estiveres a meio de um download."));
        if (Regex.IsMatch(path, @"\\steamapps\\workshop(\\|$)", RegexOptions.IgnoreCase))
            return new(FolderSafety.Caution, Loc.T("Mods do Steam Workshop"), Loc.T("Mods que subscreveste. Remove-os anulando a subscrição no Steam."));
        if (Regex.IsMatch(path, @"\\steamapps\\common\\[^\\]+$", RegexOptions.IgnoreCase))
            return new(FolderSafety.Danger, Loc.T("Jogo do Steam"), Loc.T("Desinstala pelo Steam (ou em Programas e jogos). Se o jogo já não aparecer no Steam, é um resto e pode ser apagado."));

        // ---- dentro da pasta de um programa instalado ----
        var parentApp = _appByFolder.FirstOrDefault(kv => path.StartsWith(kv.Key + "\\", StringComparison.OrdinalIgnoreCase));
        if (parentApp.Key != null)
        {
            if (CacheName.IsMatch(name)) return new(FolderSafety.Safe, Loc.T($"Cache de {parentApp.Value}"), Loc.T("Cache ou registos do programa. Volta a ser criada. Fecha o programa antes."));
            return new(FolderSafety.Danger, Loc.T($"Parte do programa {parentApp.Value}"), Loc.T("Ficheiros de um programa instalado. Apagar pode estragá-lo; desinstala-o em vez disso."));
        }

        // ---- caches e temporários (em qualquer sítio) ----
        if (CacheName.IsMatch(name))
            return new(FolderSafety.Safe, Loc.T("Cache / temporários"), Loc.T("Ficheiros que a app volta a criar sozinha. Seguro de apagar; fecha a app antes. A app pode ficar um pouco mais lenta da primeira vez."));
        if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
            return new(FolderSafety.Likely, Loc.T("Dependências de um projeto de programação"), Loc.T("Bibliotecas de um projeto JavaScript. Recriam-se com \"npm install\". Seguro se não estiveres a trabalhar no projeto."));

        // ---- Program Files sem programa conhecido ----
        if (Under(ProgramFiles) || Under(ProgramFilesX86))
        {
            var depth = path.Count(c => c == '\\') - (Under(ProgramFiles) ? ProgramFiles : ProgramFilesX86).TrimEnd('\\').Count(c => c == '\\');
            if (Is(ProgramFiles) || Is(ProgramFilesX86)) return new(FolderSafety.Danger, Loc.T("Programas instalados"), Loc.T("Desinstala programas em Programas e jogos em vez de apagar pastas."));
            if (name.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)) return new(FolderSafety.Danger, Loc.T("Apps da Microsoft Store"), Loc.T("Gerida pelo Windows. Desinstala as apps da Loja em Programas e jogos."));
            if (depth == 1 && !MatchesInstalledApp(name))
                return new(FolderSafety.Caution, Loc.T("Pode ser resto de um programa desinstalado"), Loc.T("Não encontrei nenhum programa instalado com este nome. Se não reconheces, pode ser um resto, mas alguns componentes (drivers, serviços) não aparecem na lista. Na dúvida, não apagues."));
            return new(FolderSafety.Danger, Loc.T("Parte de um programa"), Loc.T("Ficheiros de um programa instalado. Desinstala-o em vez de apagar."));
        }

        // ---- ProgramData ----
        if (Under(ProgramData))
        {
            if (Under(Path.Combine(ProgramData, "Package Cache"))) return new(FolderSafety.Danger, Loc.T("Instaladores guardados (Package Cache)"), Loc.T("Instaladores de programas como o Visual C++ e o .NET. Apagar pode impedir que sejam atualizados ou desinstalados."));
            if (Under(Path.Combine(ProgramData, "Microsoft"))) return new(FolderSafety.Danger, Loc.T("Dados do Windows"), Loc.T("Definições e dados do Windows e do Defender. Não apagues."));
            if (Regex.IsMatch(path, @"NVIDIA Corporation\\Downloader", RegexOptions.IgnoreCase)) return new(FolderSafety.Safe, Loc.T("Instaladores antigos da NVIDIA"), Loc.T("Cópias de drivers antigos. Seguro (limpa em Limpeza rápida)."));
            if (Is(ProgramData)) return new(FolderSafety.Danger, Loc.T("Dados partilhados dos programas"), Loc.T("Definições e licenças de programas para todos os utilizadores. Não apagues a pasta."));
            return new(FolderSafety.Caution, Loc.T("Dados de um programa (todos os utilizadores)"), Loc.T("Definições, licenças ou bases de dados de um programa. Apagar pode fazer o programa perder dados ou deixar de funcionar.") +
                (MatchesInstalledApp(name) ? "" : Loc.T(" Não encontrei um programa instalado com este nome: pode ser um resto.")));
        }

        // ---- pastas de utilizador ----
        if (Under(UsersRoot))
        {
            var rel = path.Length > UsersRoot.Length ? path[(UsersRoot.Length + 1)..] : "";
            var parts = rel.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return new(FolderSafety.Danger, Loc.T("Contas de utilizador"), Loc.T("As pastas de todos os utilizadores do PC."));
            if (parts.Length == 1)
                return parts[0].Equals("Public", StringComparison.OrdinalIgnoreCase)
                    ? new(FolderSafety.Caution, Loc.T("Pasta pública"), Loc.T("Ficheiros partilhados entre utilizadores."))
                    : new(FolderSafety.Danger, $"A conta \"{parts[0]}\"", Loc.T("Todos os ficheiros e definições deste utilizador. Entra para ver o que ocupa espaço."));

            var sub = parts[1];
            if (parts.Length == 2)
            {
                return sub.ToLowerInvariant() switch
                {
                    "desktop" or "documents" or "pictures" or "videos" or "music" or "ambiente de trabalho" or "documentos" or "imagens" or "vídeos" or "música" or "saved games" or "favorites" or "contacts" =>
                        new(FolderSafety.Caution, Loc.T("Os teus ficheiros pessoais"), Loc.T("Só tu sabes o que é preciso aqui. A app nunca apaga ficheiros pessoais: revê-os tu.")),
                    "downloads" or "transferências" =>
                        new(FolderSafety.Caution, Loc.T("Transferências"), Loc.T("Costuma ter instaladores e ficheiros antigos que já não precisas. Revê-a tu: apaga o que já instalaste ou já não usas.")),
                    "onedrive" => new(FolderSafety.Caution, "OneDrive", Loc.T("Sincronizado com a nuvem: apagar aqui apaga também no OneDrive e nos teus outros dispositivos.")),
                    "appdata" => new(FolderSafety.Danger, Loc.T("Dados das apps (AppData)"), Loc.T("Definições, sessões e caches de todos os teus programas. Não apagues a pasta inteira: entra para ver as partes seguras.")),
                    ".nuget" or ".gradle" or ".m2" or ".cargo" or ".npm" or ".cache" =>
                        new(FolderSafety.Likely, Loc.T("Cache de ferramentas de programação"), Loc.T("Pacotes descarregados por ferramentas de programação. Voltam a ser descarregados quando precisares.")),
                    ".ollama" => new(FolderSafety.Caution, Loc.T("Modelos de IA do Ollama"), Loc.T("Os modelos de IA local que transferiste. Para remover um, usa \"ollama rm nome-do-modelo\" (é mais limpo do que apagar à mão).")),
                    _ => Unknown(name),
                };
            }

            // AppData\Local | Roaming | LocalLow
            if (sub.Equals("AppData", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
            {
                var area = parts[2];
                if (parts.Length == 3)
                    return area.ToLowerInvariant() switch
                    {
                        "local" => new(FolderSafety.Danger, "AppData\\Local", Loc.T("Dados e caches das apps neste PC. Entra para ver quais são seguras.")),
                        "roaming" => new(FolderSafety.Danger, "AppData\\Roaming", Loc.T("Definições das apps (as que acompanham a tua conta). Entra para ver.")),
                        "locallow" => new(FolderSafety.Caution, "AppData\\LocalLow", Loc.T("Dados de apps com permissões reduzidas, muitas vezes saves de jogos (Unity).")),
                        _ => Unknown(name),
                    };

                if (area.Equals("Local", StringComparison.OrdinalIgnoreCase) && parts[3].Equals("Packages", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts.Length == 4) return new(FolderSafety.Danger, Loc.T("Dados das apps da Microsoft Store"), Loc.T("Uma pasta por cada app da Loja. Entra para ver cada uma."));
                    var pkg = parts[4];
                    var appName = _appByPackage.GetValueOrDefault(pkg) ?? pkg.Split('_')[0];
                    if (parts.Length == 5)
                        return new(FolderSafety.Caution, Loc.T($"Dados da app {appName} (Microsoft Store)"),
                            Loc.T($"Sessão, definições e cache da app {appName}. Se apagares a pasta inteira, a app perde a sessão e as definições e pode deixar de abrir. ") +
                            Loc.T("As subpastas de cache lá dentro (marcadas a verde) podem ser apagadas. Se já não usas a app, desinstala-a: o Windows apaga isto sozinho.") +
                            (_appByPackage.ContainsKey(pkg) ? "" : Loc.T(" ⚠ Não encontrei esta app instalada: pode ser um resto, e nesse caso é seguro apagar.")));
                    return parts[5].ToLowerInvariant() switch
                    {
                        // Nas apps "de secretária" empacotadas (Claude, ChatGPT, WhatsApp…), a LocalCache contém
                        // as pastas Local/Roaming redirecionadas: são os DADOS REAIS da app, não cache.
                        "localcache" when parts.Length == 6 && HasAppDataFolders(node, path) =>
                            new(FolderSafety.Caution, Loc.T($"Dados reais da app {appName}"),
                                Loc.T("Apesar do nome, nesta app é aqui que ficam as definições, sessões e dados (é a AppData dela). Não apagues a pasta inteira: entra para encontrar as caches verdadeiras, marcadas a verde.")),
                        "localcache" when parts.Length == 6 => new(FolderSafety.Safe, Loc.T($"Cache da app {appName}"), Loc.T("Cache da app. Volta a ser criada. Fecha a app antes.")),
                        "tempstate" or "ac" => new(FolderSafety.Safe, Loc.T($"Cache da app {appName}"), Loc.T("Cache e ficheiros temporários. Volta a ser criada. Fecha a app antes.")),
                        "localstate" or "roamingstate" or "settings" => new(FolderSafety.Caution, Loc.T($"Dados da app {appName}"), Loc.T("Sessão, definições e dados guardados da app. Apagar = a app volta ao início.")),
                        _ => new(FolderSafety.Caution, Loc.T($"Parte dos dados da app {appName}"), Loc.T("Dados da app. Apagar pode fazer perder sessões ou definições.")),
                    };
                }

                if (parts.Length == 4)
                {
                    var vendor = parts[3];
                    if (vendor.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
                        return new(FolderSafety.Danger, Loc.T("Dados do Windows e das apps da Microsoft"), Loc.T("Inclui definições do Windows, do Office e do Edge. Não apagues a pasta: entra para ver as caches."));
                    if (vendor.Equals("Temp", StringComparison.OrdinalIgnoreCase))
                        return new(FolderSafety.Safe, Loc.T("Temporários"), Loc.T("Ficheiros temporários. Seguro (limpa em Limpeza rápida)."));
                    if (vendor.Equals("CrashDumps", StringComparison.OrdinalIgnoreCase))
                        return new(FolderSafety.Safe, Loc.T("Relatórios de crashes de apps"), Loc.T("Seguro de apagar."));
                    if (KnownAppData.TryGetValue(vendor, out var knownAdvice)) return knownAdvice;
                    return MatchesInstalledApp(vendor)
                        ? new(FolderSafety.Caution, Loc.T($"Dados/definições de {vendor}"), Loc.T("Definições, sessões, saves ou caches desta app. Apagar a pasta inteira faz a app voltar ao início (e podes perder saves de jogos). Entra para ver as caches, que são seguras."))
                        : new(FolderSafety.Caution, Loc.T($"Dados de {vendor} (não encontrei a app instalada)"),
                            Loc.T("Não encontrei um programa instalado com este nome. Pode ser resto de algo que desinstalaste, mas também pode pertencer a uma app com outro nome ") +
                            Loc.T("(ex.: a pasta \"OpenAI\" é do ChatGPT). Na dúvida, não apagues."));
                }
            }
            return Unknown(name);
        }

        return Unknown(name);
    }

    /// <summary>Pastas bem conhecidas dentro de AppData\Local e AppData\Roaming.</summary>
    static readonly Dictionary<string, FolderAdvice> KnownAppData = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Programs"] = new(FolderSafety.Danger, Loc.T("Programas instalados só para ti"), Loc.T("Muitos programas (VS Code, Discord, etc.) instalam-se aqui em vez de em Program Files. Desinstala-os em Programas e jogos.")),
        ["wsl"] = new(FolderSafety.Danger, Loc.T("Discos do Linux (WSL)"), Loc.T("Discos virtuais das distribuições Linux do WSL. Apagar destrói-as. Remove com \"wsl --unregister nome\".")),
        ["Docker"] = new(FolderSafety.Danger, Loc.T("Dados do Docker"), Loc.T("Imagens e contentores do Docker. Limpa com \"docker system prune\" em vez de apagar.")),
        ["Packages"] = new(FolderSafety.Danger, Loc.T("Dados das apps da Microsoft Store"), Loc.T("Uma pasta por cada app da Loja. Entra para ver cada uma.")),
        ["npm-cache"] = new(FolderSafety.Likely, Loc.T("Cache do npm (programação)"), Loc.T("Pacotes JavaScript descarregados. Voltam a ser descarregados quando precisares.")),
        ["pip"] = new(FolderSafety.Likely, Loc.T("Cache do pip (Python)"), Loc.T("Pacotes Python descarregados. Voltam a ser descarregados quando precisares.")),
        ["Yarn"] = new(FolderSafety.Likely, Loc.T("Cache do Yarn (programação)"), Loc.T("Pacotes descarregados. Voltam a ser descarregados quando precisares.")),
        ["pnpm"] = new(FolderSafety.Likely, Loc.T("Cache do pnpm (programação)"), Loc.T("Pacotes descarregados. Voltam a ser descarregados quando precisares.")),
        ["NuGet"] = new(FolderSafety.Likely, Loc.T("Cache do NuGet (programação)"), Loc.T("Pacotes .NET descarregados. Voltam a ser descarregados quando precisares.")),
        ["go-build"] = new(FolderSafety.Likely, Loc.T("Cache do Go (programação)"), Loc.T("Ficheiros de compilação. Recriam-se sozinhos.")),
        ["ms-playwright"] = new(FolderSafety.Likely, Loc.T("Browsers de teste do Playwright"), Loc.T("Browsers usados para testes automáticos. Reinstalam-se com \"npx playwright install\".")),
        ["Google"] = new(FolderSafety.Caution, Loc.T("Google Chrome e outras apps da Google"), Loc.T("Inclui o teu perfil do Chrome (favoritos, palavras-passe, sessões). Entra para ver as caches, que são seguras.")),
        ["BraveSoftware"] = new(FolderSafety.Caution, Loc.T("Perfil do Brave"), Loc.T("Favoritos, palavras-passe e sessões do browser. Entra para ver as caches, que são seguras.")),
        ["Opera Software"] = new(FolderSafety.Caution, Loc.T("Perfil do Opera"), Loc.T("Favoritos, palavras-passe e sessões do browser. Entra para ver as caches, que são seguras.")),
        ["Mozilla"] = new(FolderSafety.Caution, Loc.T("Perfil do Firefox"), Loc.T("Favoritos, palavras-passe e sessões do browser. Entra para ver as caches, que são seguras.")),
        ["OpenAI"] = new(FolderSafety.Caution, Loc.T("Dados do ChatGPT / Codex"), Loc.T("Dados das apps da OpenAI (sessões, definições). Apagar faz-te perder a sessão.")),
        ["Claude"] = new(FolderSafety.Caution, Loc.T("Dados do Claude"), Loc.T("Sessões, definições e dados do Claude. Apagar faz-te perder a sessão e o trabalho guardado.")),
        ["Steam"] = new(FolderSafety.Caution, Loc.T("Dados do Steam"), Loc.T("Definições e cache do Steam. Os jogos não estão aqui. Entra para ver as caches, que são seguras.")),
    };

    static FolderAdvice Unknown(string name) =>
        new(FolderSafety.Unknown, Loc.T("Não sei ao certo"), Loc.T($"Não reconheço a pasta \"{name}\". Na dúvida, não apagues."));

    bool MatchesInstalledApp(string folderName) => Words(folderName).Any(_appWords.Contains) || _appWords.Contains(folderName);

    /// <summary>Quanto espaço dentro desta pasta é seguro (ou provavelmente seguro) de apagar.</summary>
    /// <summary>LocalCache com Local/Roaming lá dentro (vê o que foi analisado; se ainda não há subpastas, vê o disco).</summary>
    static bool HasAppDataFolders(DirNode node, string path) =>
        node.Children.Count > 0
            ? node.Children.Any(c => c.Name.Equals("Roaming", StringComparison.OrdinalIgnoreCase) || c.Name.Equals("Local", StringComparison.OrdinalIgnoreCase))
            : Directory.Exists(Path.Combine(path, "Roaming")) || Directory.Exists(Path.Combine(path, "Local"));

    public long SafeBytes(DirNode node)
    {
        if (_safeBytes.TryGetValue(node.FullPath + (node.IsFilesGroup ? "|f" : ""), out var cached)) return cached;
        var advice = Advise(node);
        long result = advice.CanRecycle ? node.Size
            : advice.Safety == FolderSafety.Danger && node.Children.Count == 0 ? 0
            : node.Children.Where(c => !c.IsFilesGroup).Sum(SafeBytes);
        _safeBytes[node.FullPath + (node.IsFilesGroup ? "|f" : "")] = result;
        return result;
    }

    public void Forget(DirNode node)
    {
        for (var n = node; n != null; n = n.Parent) _safeBytes.Remove(n.FullPath);
    }

    /// <summary>Envia o conteúdo da pasta para a Reciclagem (dá para recuperar). Ficheiros em uso são ignorados.</summary>
    public static (long Bytes, int Items, int Failed) RecycleContents(string folder)
    {
        Simulation.Guard(Loc.T($"enviar o conteúdo de {folder} para a Reciclagem"));
        long bytes = 0;
        int items = 0, failed = 0;
        var since = DateTime.Now;
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
        {
            var len = entry is FileInfo f ? f.Length : StorageDrives.FolderSize(entry.FullName);
            if (SendToRecycleBin(entry.FullName) && !entry.Exists())
            {
                bytes += len;
                items++;
            }
            else
            {
                failed++; // em uso ou sem permissão
            }
        }
        if (items > 0)
            ChangeLog.Record(ChangeKind.Files, Loc.T($"Enviado para a Reciclagem: {Path.GetFileName(folder.TrimEnd('\\'))}"),
                $"{folder} · {items} item(ns) · {Util.SystemInfo.FormatBytes(bytes)}", UndoKind.RecycleBin,
                new() { ["folder"] = folder, ["since"] = since.ToString("o") });
        return (bytes, items, failed);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    static bool SendToRecycleBin(string path)
    {
        const uint foDelete = 3;
        const ushort allowUndo = 0x40, noConfirmation = 0x10, noErrorUi = 0x400, silent = 0x4;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = foDelete,
            pFrom = path + "\0", // a API exige dois zeros no fim; o marshaller junta o segundo
            fFlags = allowUndo | noConfirmation | noErrorUi | silent,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }
}

static class FileSystemInfoExtensions
{
    public static bool Exists(this FileSystemInfo e)
    {
        e.Refresh();
        return e.Exists;
    }
}