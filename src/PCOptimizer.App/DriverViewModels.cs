using System.Windows.Media;
using PCOptimizer.Core.Drivers;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

/// <summary>Base com mensagem de estado e "ocupado", comum às linhas da aba Drivers.</summary>
public abstract class DriverRowVm : Observable
{
    string? _message;
    bool _busy;

    public bool Busy { get => _busy; set { Set(ref _busy, value); Raise(nameof(Idle)); } }
    public bool Idle => !Busy;

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

    DriverOffer? _offer;
    double _progress;
    bool _downloading;

    /// <summary>Versão nova pronta a descarregar de uma fonte oficial (null = não há).</summary>
    public DriverOffer? Offer
    {
        get => _offer;
        set
        {
            Set(ref _offer, value);
            Raise(nameof(HasOffer));
            Raise(nameof(OfferText));
            Raise(nameof(OfferSource));
            OnOfferChanged();
        }
    }

    public bool HasOffer => Offer != null;
    public string OfferText => Offer is null ? "" : Loc.T($"⬇  Descarregar e instalar {ShortVersion(Offer.Version)}") + (Offer.SizeBytes > 0 ? $"  ·  {Size(Offer.SizeBytes)}" : "");
    public string OfferSource => Offer is null ? "" : $"Fonte: {Offer.Source}" + (Offer.Date is DateTime d ? Loc.T($" · de {d:dd/MM/yyyy}") : "");

    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public bool Downloading { get => _downloading; set => Set(ref _downloading, value); }

    protected virtual void OnOfferChanged() { }

    static string ShortVersion(string v) => v.Length > 18 ? v[..18] + "…" : v;

    public static string Size(long bytes) =>
        bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : SystemInfo.FormatBytes(bytes);
}

public sealed class DeviceVm : DriverRowVm
{
    public DeviceVm(ProblemDevice d) => Device = d;

    public ProblemDevice Device { get; }
    public string Name => Device.Name;
    public string Explanation => Device.Explanation;
    public string IdText => Device.HardwareId is null ? Device.InstanceId : $"ID: {Device.HardwareId}";
    public bool CanReinstall => Device.Kind is DeviceProblemKind.Missing or DeviceProblemKind.Broken;
    public bool CanEnable => Device.Code == 22;

    public string Icon => Device.Kind switch
    {
        DeviceProblemKind.Missing => "❓",
        DeviceProblemKind.Broken => "⚠",
        DeviceProblemKind.Disabled => "⏸",
        _ => "↻",
    };

    public string KindText => Device.Kind switch
    {
        DeviceProblemKind.Missing => Loc.T("Sem driver"),
        DeviceProblemKind.Broken => Loc.T("Driver com erro"),
        DeviceProblemKind.Disabled => "Desativado",
        _ => Loc.T("Falta reiniciar"),
    };

    public SolidColorBrush KindBrush => Device.Kind switch
    {
        DeviceProblemKind.Missing or DeviceProblemKind.Broken => Palette.Critical,
        DeviceProblemKind.NeedsReboot => Palette.Warning,
        _ => Palette.Muted,
    };
}

public sealed class DriverUpdateVm : DriverRowVm
{
    bool _isSelected = true;

    public DriverUpdateVm(DriverUpdate u) => Update = u;

    public DriverUpdate Update { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public string Title => Update.Title;
    public string Detail => string.Join(" · ", new[]
    {
        Update.Class, Update.Manufacturer, Update.Date?.ToString("dd/MM/yyyy"),
        Update.SizeBytes > 0 ? SystemInfo.FormatBytes(Update.SizeBytes) : null,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public sealed class KeyDriverVm : DriverRowVm
{
    public KeyDriverVm(KeyDriver k) => Driver = k;

    public KeyDriver Driver { get; }
    public string Icon => Driver.Icon;
    public string Category => Driver.Category.ToUpperInvariant();
    public string Device => Driver.Device;
    public string Advice => HasOffer && Driver.Status == KeyDriverStatus.Ok
        ? Loc.T("Há uma versão mais nova. Carrega em Descarregar e instalar: a app vai buscá-la à fonte oficial e instala sozinha.")
        : Driver.Advice;

    public string Detail => string.Join(" · ", new[]
    {
        Driver.Provider,
        string.IsNullOrEmpty(Driver.Version) ? null : Loc.T($"versão {Driver.Version}"),
        Driver.Date is DateTime d ? Loc.T($"de {d:dd/MM/yyyy}") : null,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    protected override void OnOfferChanged()
    {
        Raise(nameof(StatusText));
        Raise(nameof(StatusBrush));
        Raise(nameof(Advice));
    }

    public string StatusText => HasOffer && Driver.Status != KeyDriverStatus.Missing ? Loc.T("⬆ Há versão nova") : Driver.Status switch
    {
        KeyDriverStatus.Ok => "✓ Atualizado",
        KeyDriverStatus.Optional => "Antigo, opcional",
        KeyDriverStatus.Update => Loc.T("⬆ Vale a pena atualizar"),
        _ => Loc.T("✕ Em falta"),
    };

    public SolidColorBrush StatusBrush => HasOffer && Driver.Status != KeyDriverStatus.Missing ? Palette.Warning : Driver.Status switch
    {
        KeyDriverStatus.Ok => Palette.Ok,
        KeyDriverStatus.Optional => Palette.Muted,
        KeyDriverStatus.Update => Palette.Warning,
        _ => Palette.Critical,
    };

    public bool HasUrl => Driver.Url != null;
    public string UrlText => $"🌐  {Driver.UrlLabel}";
}

public sealed class OldPackageVm : DriverRowVm
{
    bool _isSelected = true;

    public OldPackageVm(OldDriverPackage p) => Package = p;

    public OldDriverPackage Package { get; }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public string Title => $"{Package.Provider} — {Package.OriginalName}";
    public string Detail => Loc.T($"Versão {Package.Version} guardada, mas já usas a {Package.NewerVersion}");
    public string SizeText => Size(Package.SizeBytes);
}
