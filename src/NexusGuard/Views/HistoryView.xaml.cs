using System.Windows;
using System.Windows.Controls;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard.Views;

public partial class HistoryView : UserControl
{
    private bool _loadedOnce;

    public HistoryView()
    {
        InitializeComponent();

        LogList.ItemsSource = Logger.Entries;

        Loaded += (_, _) =>
        {
            if (_loadedOnce) return;
            _loadedOnce = true;

            History.EnsureLoaded();
            EntryList.ItemsSource = History.Entries;
            History.Entries.CollectionChanged += (_, _) => UpdateSummary();

            UpdateSummary();
        };
    }

    private void UpdateSummary()
    {
        var total = History.Entries.Count;
        var undoable = History.Entries.Count(e => e.CanUndo);

        SummaryText.Text = total == 0
            ? "Nenhuma ação registada ainda."
            : $"{Fmt.Count(total)} ações · {undoable} ainda reversíveis.";

        EmptyText.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        var log = TabLog.IsChecked == true;
        HistoryPanel.Visibility = log ? Visibility.Collapsed : Visibility.Visible;
        LogPanel.Visibility = log ? Visibility.Visible : Visibility.Collapsed;

        if (log) Dispatcher.BeginInvoke(() => LogScroller.ScrollToEnd());
    }

    // ---------------- Desfazer ----------------

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HistoryEntry entry }) return;

        if (!Ui.Confirm(this, "Desfazer ação",
                $"{entry.Description}\n\nRegistado em {entry.WhenText}.\n\nReverter esta ação?"))
            return;

        var ok = entry.Undo switch
        {
            UndoKind.QuarantineBatch => UndoQuarantine(entry),
            UndoKind.RegistryExport => await UndoRegistryAsync(entry),
            UndoKind.RegistryValue => UndoRegistryValue(entry),
            UndoKind.StartupItem => UndoStartup(entry),
            _ => false
        };

        if (ok)
        {
            History.MarkUndone(entry);
            UpdateSummary();
            Ui.Inform(this, "Desfazer", "A ação foi revertida.");
        }
        else
        {
            Ui.Warn(this, "Desfazer", "Não foi possível reverter esta ação. Consulte o registro técnico.");
        }
    }

    private static bool UndoQuarantine(HistoryEntry entry)
    {
        if (!entry.Data.TryGetValue("batch", out var batchId)) return false;

        var batch = Quarantine.Find(batchId);
        if (batch is null)
        {
            Logger.Warn("Histórico", $"O lote {batchId} já não está em quarentena.");
            return false;
        }

        var (restored, _) = Quarantine.Restore(batch);
        return restored > 0;
    }

    private static async Task<bool> UndoRegistryAsync(HistoryEntry entry)
    {
        if (!entry.Data.TryGetValue("file", out var file)) return false;
        return await RegistryCleaner.RestoreAsync(file);
    }

    private static bool UndoRegistryValue(HistoryEntry entry)
    {
        if (!entry.Data.TryGetValue("name", out var name)) return false;
        if (!entry.Data.TryGetValue("previous", out var previous)) return false;

        var toggle = Privacy.Build().FirstOrDefault(t => t.Name == name);
        if (toggle is null) return false;

        return Privacy.Apply(toggle, previous == "1", out _);
    }

    private static bool UndoStartup(HistoryEntry entry)
    {
        if (!entry.Data.TryGetValue("name", out var name)) return false;
        if (!entry.Data.TryGetValue("previous", out var previous)) return false;

        var item = StartupManager.Load().FirstOrDefault(i => i.Name == name);
        if (item is null) return false;

        return StartupManager.SetEnabled(item, previous == "1", out _);
    }

    // ---------------- Exportar ----------------

    private void OnOpenFolder(object sender, RoutedEventArgs e) => Shell.OpenExternal(Paths.Logs);

    private async void OnExportReport(object sender, RoutedEventArgs e)
    {
        ReportButton.IsEnabled = false;

        try
        {
            var file = await Report.GenerateAsync(MainWindow.Instance?.Snapshot);

            if (file is null)
            {
                Ui.Warn(this, "Relatório", "Não foi possível gerar o relatório. Consulte o registro técnico.");
                return;
            }

            if (Ui.Confirm(this, "Relatório criado",
                    $"O relatório foi salvo em:\n{file}\n\nAbrir agora?"))
                Shell.OpenExternal(file);
        }
        finally
        {
            ReportButton.IsEnabled = true;
        }
    }
}
