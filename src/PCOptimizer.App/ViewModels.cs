using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Media;
using PCOptimizer.Core;
using PCOptimizer.Core.Optimization;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class Palette
{
    static SolidColorBrush Make(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static readonly SolidColorBrush Critical = Make("#FF5C5C");
    public static readonly SolidColorBrush Warning = Make("#FFB020");
    public static readonly SolidColorBrush Info = Make("#C9C9CE");
    public static readonly SolidColorBrush Ok = Make("#3DD68C");
    public static readonly SolidColorBrush Muted = Make("#9A9A9F");
    public static readonly SolidColorBrush Neutral = Make("#2A2A2F");

    public static SolidColorBrush For(Severity s) => s switch
    {
        Severity.Critical => Critical,
        Severity.Warning => Warning,
        Severity.Info => Info,
        _ => Ok,
    };
}

public sealed class SeverityBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Severity s ? Palette.For(s) : Palette.Muted;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SeverityTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Severity.Critical => Loc.T("CRÍTICO"),
        Severity.Warning => Loc.T("AVISO"),
        Severity.Info => "INFO",
        _ => "OK",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FindingVm : Observable
{
    string? _message;

    public FindingVm(Finding f)
    {
        Finding = f;
        Actions = f.Actions.Select(a => new ActionVm(a, this)).ToList();
    }

    public Finding Finding { get; }
    public Severity Severity => Finding.Severity;
    public string Category => Loc.T(Finding.Category);
    public string Title => Finding.Title;
    public string Description => Finding.Description;
    public string MoreLabel => Loc.T("Ver detalhes ▾");
    public string LessLabel => Loc.T("Ver menos ▴");
    public List<ActionVm> Actions { get; }
    public string Time { get; } = DateTime.Now.ToString("HH:mm");

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
}

public sealed record ActionVm(FixAction Action, FindingVm Owner)
{
    public string Label => Action.Label;
}

public sealed class TweakVm : Observable
{
    bool _isSelected;
    bool? _applied;
    bool _loaded;
    bool _canRevert;
    string? _message;

    public TweakVm(Tweak tweak)
    {
        Tweak = tweak;
        _isSelected = tweak.DefaultSelected;
    }

    public Tweak Tweak { get; }
    public string Name => Tweak.Name;
    public string Description => Tweak.Description;
    public bool RequiresReboot => Tweak.RequiresReboot;
    public bool RequiresAdmin => Tweak.RequiresAdmin;

    public string RiskText => Tweak.Risk switch { Risk.High => Loc.T("Risco alto"), Risk.Medium => Loc.T("Risco médio"), _ => Loc.T("Risco baixo") };
    /// <summary>Só se mostra o risco quando não é baixo (menos ruído na lista).</summary>
    public bool ShowRisk => Tweak.Risk != Risk.Low;
    public SolidColorBrush RiskBrush => Tweak.Risk switch { Risk.High => Palette.Critical, Risk.Medium => Palette.Warning, _ => Palette.Ok };

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public bool CanRevert { get => _canRevert; private set => Set(ref _canRevert, value); }

    public string StatusText => !_loaded ? "A verificar…" : _applied switch
    {
        true => "✓ Aplicado",
        false => Loc.T("Não aplicado"),
        null => Loc.T("Não se aplica a este PC"),
    };

    public SolidColorBrush StatusBrush => !_loaded ? Palette.Muted : _applied switch
    {
        true => Palette.Ok,
        false => Palette.Muted,
        null => Palette.Muted,
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

    public async Task RefreshAsync()
    {
        try
        {
            _applied = await Task.Run(Tweak.IsAppliedAsync);
        }
        catch
        {
            _applied = null;
        }
        _loaded = true;
        CanRevert = TweakEngine.CanRevert(Tweak);
        Raise(nameof(StatusText));
        Raise(nameof(StatusBrush));
    }
}

public sealed class ProfileVm
{
    public ProfileVm(OptimizationProfile p)
    {
        Profile = p;
        Tweaks = p.Tweaks.Select(t => new TweakVm(t)).ToList();
    }

    public OptimizationProfile Profile { get; }
    public string Name => Profile.Name;
    public string Icon => Profile.Icon;
    public string Summary => Profile.Summary;
    public string? Notes => Profile.Notes;
    public List<TweakVm> Tweaks { get; }
}

public sealed class StartupVm : Observable
{
    bool _enabled;
    string? _message;

    public StartupVm(StartupItem item)
    {
        Item = item;
        _enabled = item.Enabled;
    }

    public StartupItem Item { get; }
    public string Name => Item.Name;
    public string Detail => Item.Description ?? Item.Command;
    public string Location => Item.Location + (Item.RequiresAdmin ? Loc.T(" · precisa de admin") : "");
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

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
}
