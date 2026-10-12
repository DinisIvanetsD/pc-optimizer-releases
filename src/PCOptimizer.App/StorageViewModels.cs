using System.Windows.Media;
using PCOptimizer.Core.Storage;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

public sealed class DriveVm : Observable
{
    public DriveVm(DriveSummary d, bool? trim)
    {
        Drive = d;
        TrimText = !StorageDrives.IsSsd(d) || trim is null ? "" : trim.Value ? Loc.T("✓ TRIM ligado") : Loc.T("⚠ TRIM desligado no Windows");
        TrimBrush = trim == false ? Palette.Warning : Palette.Ok;
    }

    public DriveSummary Drive { get; }
    public string TrimText { get; }
    public SolidColorBrush TrimBrush { get; }
    public bool CanOptimize => (StorageDrives.IsSsd(Drive) || StorageDrives.IsHdd(Drive)) && !Busy;
    public string OptimizeLabel => StorageDrives.IsSsd(Drive) ? Loc.T("Otimizar (TRIM)") : Loc.T("Desfragmentar");
    public string OptimizeTip => StorageDrives.IsSsd(Drive)
        ? Loc.T("Diz ao SSD que blocos estão livres, para escrever depressa. Demora segundos e não apaga nada.")
        : Loc.T("Junta os pedaços dos ficheiros para o disco ler mais depressa. Pode demorar mais de uma hora; podes usar o PC.");

    bool _busy;
    string _message = "";
    public bool Busy { get => _busy; set { Set(ref _busy, value); Raise(nameof(CanOptimize)); } }
    public string Message { get => _message; set { Set(ref _message, value); Raise(nameof(HasMessage)); } }
    public bool HasMessage => Message.Length > 0;
    public string Title => $"{Drive.Letter}  {Drive.Label}";
    public string Media => string.IsNullOrEmpty(Drive.Model) ? Drive.Media : $"{Drive.Media} · {Drive.Model}";
    public double Percent => Drive.UsedPercent;
    public string UsedText => Loc.T($"{SystemInfo.FormatBytes(Drive.UsedBytes)} usados de {SystemInfo.FormatBytes(Drive.TotalBytes)}");
    public string FreeText => Loc.T($"{SystemInfo.FormatBytes(Drive.FreeBytes)} livres");
    public SolidColorBrush Brush => Percent >= 90 ? Palette.Critical : Percent >= 80 ? Palette.Warning : Palette.Info;
}

public sealed class AppVm : Observable
{
    string? _message;
    bool _isSelected;
    bool _busy;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value && CanSelect); }
    public bool Busy { get => _busy; set { Set(ref _busy, value); Raise(nameof(CanSelect)); } }

    /// <summary>Essenciais: bloqueado. Em uso: 3 confirmações. Pouco usado: 2. Não usado/inútil: 1.</summary>
    public DeleteTier Tier => App.Kind == AppKind.Essential ? DeleteTier.Blocked : App.Verdict switch
    {
        AppVerdict.InUse => DeleteTier.Triple,
        AppVerdict.Occasional => DeleteTier.Double,
        _ => DeleteTier.Normal,
    };

    public bool CanSelect => Tier != DeleteTier.Blocked && !Busy && App.CanUninstall;
    public string SelectTip => Tier switch
    {
        DeleteTier.Blocked => Loc.T("Componente essencial: não dá para desinstalar"),
        DeleteTier.Triple => Loc.T("Estás a usar isto: vai pedir 3 confirmações"),
        DeleteTier.Double => Loc.T("Pouco usado: vai pedir 2 confirmações"),
        _ => Loc.T("Selecionar para desinstalar"),
    };

    public AppVm(InstalledApp app, IReadOnlyDictionary<string, string> mediaByDrive)
    {
        App = app;
        DriveText = app.Drive == "?" ? Loc.T("Disco ?") : $"{app.Drive} · {mediaByDrive.GetValueOrDefault(app.Drive, Loc.T("Disco"))}";
    }

    public InstalledApp App { get; }
    public string Name => App.Name;
    public string Subtitle => string.Join(" · ", new[] { App.Source == "Programa" ? null : App.Source, App.Publisher, App.Version }
        .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
    public string KindIcon => App.Kind switch { AppKind.Game => "🎮", AppKind.Store => "🛍", AppKind.Essential => "🧩", _ => "📦" };
    public string DriveText { get; }
    public long SortSize => App.SizeBytes ?? -1;
    public string SizeText => App.SizeBytes is long s ? SystemInfo.FormatBytes(s) : App.Source == "Microsoft Store" ? "—" : "a medir…";
    public string LastUsedText => App.LastUsed is DateTime t ? Ago(t) : App.Verdict == AppVerdict.Essential ? "—" : Loc.T("sem registo");
    public int Utility => App.Utility;
    public string Reason => App.Reason;
    public bool CanUninstall => App.CanUninstall;
    public bool HasFolder => App.InstallLocation != null;

    public string VerdictText => App.Verdict switch
    {
        AppVerdict.Essential => "Essencial",
        AppVerdict.InUse => Loc.T("Em uso"),
        AppVerdict.Occasional => Loc.T("Pouco usado"),
        AppVerdict.Unused => Loc.T("Não usado"),
        _ => Loc.T("Inútil"),
    };

    public SolidColorBrush UtilityBrush => App.Utility switch
    {
        >= 75 => Palette.Ok,
        >= 40 => Palette.Warning,
        _ => Palette.Critical,
    };

    public string? Message
    {
        get => _message;
        set
        {
            Set(ref _message, value);
            Raise(nameof(HasMessage));
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>Chamado quando o tamanho real é medido em segundo plano.</summary>
    public void RefreshSize()
    {
        Raise(nameof(SizeText));
        Raise(nameof(SortSize));
        Raise(nameof(Utility));
        Raise(nameof(UtilityBrush));
        Raise(nameof(Reason));
        Raise(nameof(VerdictText));
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalHours < 24 ? "hoje"
            : d.TotalDays < 2 ? "ontem"
            : d.TotalDays < 30 ? Loc.T($"há {d.TotalDays:N0} dias")
            : d.TotalDays < 60 ? Loc.T("há 1 mês")
            : d.TotalDays < 365 ? Loc.T($"há {d.TotalDays / 30:N0} meses")
            : Loc.T($"há {d.TotalDays / 365:N1} anos");
    }
}

public sealed class DirVm : Observable
{
    string? _message;
    bool _isSelected;
    bool _busy;

    /// <summary>Texto a mostrar em vez do nome (no modo "só o que é seguro", mostra o caminho).</summary>
    public string? DisplayOverride { get; init; }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value && CanDelete); }
    public bool Busy { get => _busy; set { Set(ref _busy, value); Raise(nameof(CanDelete)); } }

    /// <summary>Não apagar: bloqueado. Cuidado / não sei: 3 confirmações. Provavelmente seguro: 2. Seguro: 1.</summary>
    public DeleteTier Tier => Advice.Safety switch
    {
        FolderSafety.Danger => DeleteTier.Blocked,
        FolderSafety.Caution or FolderSafety.Unknown => DeleteTier.Triple,
        FolderSafety.Likely => DeleteTier.Double,
        _ => DeleteTier.Normal,
    };

    public bool CanDelete => Tier != DeleteTier.Blocked && !Node.IsFilesGroup && !Node.Locked && Node.Size > 0 && !Busy;
    public bool CanDeleteRisky => CanDelete && !Advice.CanRecycle;

    public DirVm(DirNode node, long parentSize, FolderAdvice advice, long safeBytes)
    {
        Node = node;
        Advice = advice;
        SafeBytes = safeBytes;
        Percent = parentSize <= 0 ? 0 : 100.0 * node.Size / parentSize;
    }

    public DirNode Node { get; }
    public FolderAdvice Advice { get; }
    public long SafeBytes { get; }
    public string What => Advice.What;
    public string Explanation => Advice.Explanation;
    public bool CanRecycle => Advice.CanRecycle && CanDelete;

    public string SafetyText => Advice.Safety switch
    {
        FolderSafety.Safe => Loc.T("✓ Seguro apagar"),
        FolderSafety.Likely => Loc.T("Provavelmente seguro"),
        FolderSafety.Caution => Loc.T("⚠ Cuidado"),
        FolderSafety.Danger => Loc.T("✕ Não apagar"),
        _ => Loc.T("? Não sei"),
    };

    public SolidColorBrush SafetyBrush => Advice.Safety switch
    {
        FolderSafety.Safe or FolderSafety.Likely => Palette.Ok,
        FolderSafety.Caution => Palette.Warning,
        FolderSafety.Danger => Palette.Critical,
        _ => Palette.Muted,
    };

    /// <summary>Pastas "com cuidado/não apagar" que têm partes seguras lá dentro.</summary>
    public string SafeInsideText => !Advice.CanRecycle && SafeBytes >= 1 << 20 ? Loc.T($"● {SystemInfo.FormatBytes(SafeBytes)} seguros lá dentro") : "";

    public string? Message
    {
        get => _message;
        set
        {
            Set(ref _message, value);
            Raise(nameof(HasMessage));
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public string Name => DisplayOverride ?? (Node.IsFilesGroup ? Node.Name : (Node.Locked ? "🔒  " : "📁  ") + Node.Name);
    public string SizeText => Node.Locked ? "?" : SystemInfo.FormatBytes(Node.Size);
    public string FilesText => Node.Locked
        ? (Node.Name.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)
            ? Loc.T("Apps da Microsoft Store: o Windows protege esta pasta e não deixa medir")
            : Loc.T("O Windows não deixa ler esta pasta") + (Admin.IsElevated ? "" : Loc.T(" (tenta como administrador)")))
        : Loc.T($"{Node.Files:N0} ficheiros");
    public double Percent { get; }
    public string PercentText => $"{Percent:N1}%";
    public bool CanOpen => !Node.IsFilesGroup && Node.Children.Count > 0;
}

public sealed class CleanupVm : Observable
{
    string _sizeText = "a medir…";
    string? _message;
    bool _busy;

    public CleanupVm(CleanupItem item) => Item = item;

    public CleanupItem Item { get; }
    public string Icon => Item.Icon;
    public string Name => Item.Name;
    public string Description => Item.Description;
    public bool CanClean => Item.Clean != null;
    public bool CanOpen => Item.Open != null;
    public string CleanLabel => Item.CleanLabel + (Item.NeedsAdmin ? " 🛡" : "");
    public string OpenLabel => Item.OpenLabel;

    public string SizeText { get => _sizeText; set => Set(ref _sizeText, value); }
    public bool Busy { get => _busy; set => Set(ref _busy, value); }

    public string? Message
    {
        get => _message;
        set
        {
            Set(ref _message, value);
            Raise(nameof(HasMessage));
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public async Task MeasureAsync()
    {
        SizeText = "a medir…";
        var size = await Task.Run(() => { try { return Item.Measure(); } catch { return null; } });
        SizeText = size is long s ? SystemInfo.FormatBytes(s) : !Admin.IsElevated ? "🛡 admin" : "—";
    }
}
