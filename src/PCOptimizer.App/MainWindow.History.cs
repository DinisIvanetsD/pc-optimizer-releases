using System.Windows;
using System.Windows.Controls;
using PCOptimizer.Core.History;
using PCOptimizer.Core.Util;

namespace PCOptimizer.App;

public partial class MainWindow
{
    bool _historyHooked;

    void LoadHistory()
    {
        if (!_historyHooked)
        {
            _historyHooked = true;
            // atualiza sozinho quando outra aba faz uma alteração
            ChangeLog.Changed += () => Dispatcher.BeginInvoke(() =>
            {
                if (Tabs.SelectedItem == HistoryTab) LoadHistory();
            });
        }

        var all = ChangeLog.All;
        var onlyUndo = HistoryUndoOnly.IsChecked == true;
        var shown = all.Where(e => !onlyUndo || e.CanUndo).ToList();
        HistoryDays.ItemsSource = shown
            .GroupBy(e => e.When.Date)
            .Select(g => new ChangeDayVm(g.Key, g.Select(e => new ChangeVm(e))))
            .ToList();

        var undoable = all.Count(e => e.CanUndo);
        HistorySummary.Text = all.Count == 0
            ? Loc.T("Ainda não foi feita nenhuma alteração")
            : Loc.T($"Alterações feitas pela app: {all.Count} · podem ser desfeitas: {undoable}");
        HistoryEmpty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryEmptyText.Text = all.Count == 0
            ? Loc.T("As alterações da app aparecem aqui, com Desfazer.")
            : Loc.T("Nada para desfazer com este filtro.");
    }

    void HistoryFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) LoadHistory();
    }

    void HistoryOpenFile_Click(object sender, RoutedEventArgs e) =>
        PCOptimizer.Core.Util.Shell.Open("explorer.exe", $"/select,\"{ChangeLog.FilePath}\"");

    async void HistoryUndo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChangeVm vm }) return;
        var needsAdmin = vm.Entry.Undo is UndoKind.Tweak or UndoKind.Dns or UndoKind.Qos or UndoKind.BadMemory ||
                         (vm.Entry.Undo == UndoKind.Startup && vm.Entry.Data.GetValueOrDefault("admin") == "1");
        if (needsAdmin && !EnsureAdmin()) return;
        if (vm.Entry.Undo != UndoKind.RestorePoint && !Confirm($"Desfazer \"{vm.Title}\"?")) return;

        vm.Busy = true;
        try
        {
            vm.Message = await ChangeLog.UndoAsync(vm.Entry);
        }
        catch (Exception ex)
        {
            vm.Message = Ui.Error(ex, Loc.T("Não deu: "));
        }
        finally
        {
            vm.Busy = false;
            vm.Refresh();
        }

        // as outras abas mostram o estado novo
        if (vm.Entry.Undo == UndoKind.Startup) await LoadStartupAsync();
    }
}
