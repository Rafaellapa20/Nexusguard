using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard.Views;

public partial class AppsView : UserControl
{
    private readonly ObservableCollection<AppUpgrade> _apps = new();
    private ConsoleSink? _sink;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _loadedOnce;

    public AppsView()
    {
        InitializeComponent();
        AppList.ItemsSource = _apps;

        Loaded += async (_, _) =>
        {
            _sink ??= new ConsoleSink(Output);

            if (_loadedOnce) return;
            _loadedOnce = true;

            if (!AppUpdater.IsAvailable)
            {
                WingetWarning.Visibility = Visibility.Visible;
                CountText.Text = "—";
                SubText.Text = "Instale o «Programa de Instalação de Aplicativos» para ativar esta secção.";
                ScanButton.IsEnabled = false;
                UpdateButton.IsEnabled = false;
                return;
            }

            await ScanAsync();
        };
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy && AppUpdater.IsAvailable;
        UpdateButton.IsEnabled = !busy && AppUpdater.IsAvailable;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- Procura ----------------

    private async void OnScan(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy) return;

        SetBusy(true);
        StatusLine.Text = "consultando o winget…";
        _cts = new CancellationTokenSource();

        try
        {
            var list = await AppUpdater.ListUpgradesAsync(line => _sink?.Write(line), _cts.Token);

            _apps.Clear();
            foreach (var a in list) _apps.Add(a);

            CountText.Text = list.Count.ToString(Fmt.Pt);
            SubText.Text = list.Count == 0
                ? "Todas as aplicativos conhecidas pelo winget estão atualizadas."
                : $"{list.Count} aplicativo(ões) com versão mais recente disponível.";

            EmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = "Nada para atualizar — está tudo em dia.";
            StatusLine.Text = "procura concluída";
        }
        catch (OperationCanceledException)
        {
            StatusLine.Text = "procura cancelada";
        }
        catch (Exception ex)
        {
            Logger.Error("Aplicativos", "Falha ao procurar atualizações", ex);
            StatusLine.Text = "a procura falhou";
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ---------------- Atualizar ----------------

    private async void OnUpdateSelected(object sender, RoutedEventArgs e)
    {
        var chosen = _apps.Where(a => a.Selected && !a.Done).ToList();

        if (chosen.Count == 0)
        {
            Ui.Inform(this, "Atualizar aplicativos", "Selecione pelo menos um aplicativo.");
            return;
        }

        if (!Ui.Confirm(this, "Atualizar aplicativos",
                $"Serão atualizadas {chosen.Count} aplicativo(ões):\n\n" +
                string.Join("\n", chosen.Take(10).Select(a => $"• {a.Name}")) +
                (chosen.Count > 10 ? $"\n… e mais {chosen.Count - 10}." : "") +
                "\n\nFeche-os antes de continuar, para evitar instalações falhadas. Continuar?"))
            return;

        await RunUpdateAsync(chosen);
    }

    private async void OnUpdateOne(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AppUpgrade app }) return;
        await RunUpdateAsync(new List<AppUpgrade> { app });
    }

    private async Task RunUpdateAsync(List<AppUpgrade> apps)
    {
        if (_busy) return;

        SetBusy(true);
        ShowConsole(true);
        _cts = new CancellationTokenSource();
        StatusLine.Text = "atualizando…";

        try
        {
            var ok = await AppUpdater.UpgradeManyAsync(apps, line => _sink?.Write(line), _cts.Token);

            StatusLine.Text = $"{ok} de {apps.Count} atualizadas";

            Ui.Inform(this, "Atualizações concluídas",
                ok == apps.Count
                    ? $"{ok} aplicativo(ões) atualizadas com sucesso."
                    : $"{ok} de {apps.Count} atualizadas. Veja a saída do winget para as que falharam.");

            await ScanAsync();
        }
        catch (OperationCanceledException)
        {
            StatusLine.Text = "atualização interrompida";
        }
        catch (Exception ex)
        {
            Logger.Error("Aplicativos", "Falha ao atualizar", ex);
            StatusLine.Text = "a atualização falhou";
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusLine.Text = "interrompendo…";
    }

    private void OnInstallWinget(object sender, RoutedEventArgs e) => AppUpdater.OpenWingetInstall();

    private void OnToggleConsole(object sender, RoutedEventArgs e) =>
        ShowConsole(Output.Visibility != Visibility.Visible);

    private void ShowConsole(bool show)
    {
        Output.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ToggleConsole.Content = show ? "Ocultar" : "Mostrar";
    }
}
