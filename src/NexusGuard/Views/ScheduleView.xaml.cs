using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard.Views;

public partial class ScheduleView : UserControl
{
    private readonly ObservableCollection<ScheduledJob> _jobs = new();
    private bool _loadedOnce;
    private bool _busy;

    public ScheduleView()
    {
        InitializeComponent();
        JobList.ItemsSource = _jobs;

        foreach (var job in Scheduler.Build()) _jobs.Add(job);

        State.PrimaryClicked += (_, _) => App.RestartElevated();

        Loaded += async (_, _) =>
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            await RefreshAsync();
        };
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busy) return;

        _busy = true;
        RefreshButton.IsEnabled = false;

        try
        {
            await Scheduler.RefreshAsync(_jobs);

            var active = _jobs.Count(j => j.Enabled);
            SummaryText.Text = active == 0
                ? "Nenhuma tarefa agendada neste momento."
                : $"{active} de {_jobs.Count} tarefas ativas.";

            // O backup agendado só funciona com um destino já escolhido na página Backup.
            var backup = _jobs.FirstOrDefault(j => j.Key == "Backup");

            if (backup is { Enabled: true } && string.IsNullOrWhiteSpace(Settings.Current.BackupDestination))
                backup.Status = "sem destino guardado";

            if (!Fmt.IsAdmin) State.ShowNeedsAdmin("Criar ou alterar tarefas agendadas");
            else State.Hide();
        }
        catch (Exception ex)
        {
            Logger.Error("Agendamento", "Falha ao ler as tarefas", ex);
            SummaryText.Text = "Não foi possível ler as tarefas agendadas.";
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: ScheduledJob job } box) return;

        var wanted = box.IsChecked == true;
        box.IsEnabled = false;

        try
        {
            var (ok, message) = await Scheduler.SetEnabledAsync(job, wanted);

            if (!ok)
            {
                job.Enabled = !wanted;
                box.IsChecked = !wanted;
                Ui.Warn(this, "Agendamento", message);
                return;
            }

            SummaryText.Text = message;
        }
        finally
        {
            box.IsEnabled = true;
        }
    }

    private void OnOpenScheduler(object sender, RoutedEventArgs e) => Scheduler.OpenTaskScheduler();
}
