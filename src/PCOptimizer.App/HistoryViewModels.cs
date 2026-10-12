using System.Windows.Media;
using PCOptimizer.Core.History;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

public sealed class ChangeVm : Observable
{
    string? _message;
    bool _busy;

    public ChangeVm(ChangeEntry e) => Entry = e;

    public ChangeEntry Entry { get; }
    public string Title => Entry.Title;
    public string Detail => Entry.Detail;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Entry.Detail);
    public string Time => Entry.When.ToString("HH:mm");

    public string Icon => Entry.Kind switch
    {
        ChangeKind.Tweak => "⚡",
        ChangeKind.Startup => "🚀",
        ChangeKind.Network => "🌐",
        ChangeKind.Memory => "🧠",
        ChangeKind.Files => "🗑",
        ChangeKind.Uninstall => "📦",
        ChangeKind.Driver => "🔧",
        ChangeKind.Install => "⬇",
        ChangeKind.Recording => "⏺",
        _ => "🧹",
    };

    public bool CanUndo => Entry.CanUndo && !Busy;
    public bool ShowUndo => Entry.CanUndo;
    public string UndoLabel => Entry.Undo == UndoKind.RestorePoint ? Loc.T("↶  Restauro do Sistema") : "↶  Desfazer";

    public string StatusText => Entry.Undone
        ? $"Desfeito{(Entry.UndoneAt is DateTime t ? Loc.T($" às {t:HH:mm}") : "")}"
        : Entry.Undo == UndoKind.None ? Loc.T("Não dá para desfazer") : "";

    public SolidColorBrush StatusBrush => Entry.Undone ? Palette.Ok : Palette.Muted;
    public double RowOpacity => Entry.Undone ? 0.6 : 1;

    public bool Busy { get => _busy; set { Set(ref _busy, value); Raise(nameof(CanUndo)); } }

    public string? Message
    {
        get => _message;
        set { Set(ref _message, value); Raise(nameof(HasMessage)); }
    }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public void Refresh()
    {
        Raise(nameof(CanUndo));
        Raise(nameof(ShowUndo));
        Raise(nameof(StatusText));
        Raise(nameof(StatusBrush));
        Raise(nameof(RowOpacity));
    }
}

public sealed class ChangeDayVm
{
    public ChangeDayVm(DateTime day, IEnumerable<ChangeVm> items)
    {
        Day = day;
        Items = items.ToList();
    }

    public DateTime Day { get; }
    public List<ChangeVm> Items { get; }

    public string DayText
    {
        get
        {
            var today = DateTime.Today;
            var text = Day == today ? "Hoje" : Day == today.AddDays(-1) ? "Ontem" : Day.ToString(Loc.T("dddd, d 'de' MMMM"), new System.Globalization.CultureInfo("pt-PT"));
            return char.ToUpper(text[0]) + text[1..];
        }
    }
}
