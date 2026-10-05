using System.Windows;
using System.Windows.Controls;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard.Views;

public partial class HardwareView : UserControl
{
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _loadedOnce;

    public HardwareView()
    {
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            await ScanAsync();
        };
    }

    private async void OnScan(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy) return;

        _busy = true;
        ScanButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;

        try
        {
            var components = await Hardware.ScanAsync();
            ComponentList.ItemsSource = components;

            var attention = components.Count(c => c.NeedsAttention);

            SummaryText.Text = components.Count == 0
                ? "Não foi possível ler o inventário de hardware neste sistema."
                : attention == 0
                    ? $"{components.Count} componentes · todos sem avisos."
                    : $"{components.Count} componentes · {attention} a precisar de atenção.";
        }
        catch (Exception ex)
        {
            Logger.Error("Hardware", "Falha ao inventariar", ex);
            SummaryText.Text = "Não foi possível ler o inventário de hardware.";
        }
        finally
        {
            _busy = false;
            ScanButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnStress(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _cts?.Cancel();
            return;
        }

        if (!Ui.Confirm(this, "Teste de estresse",
                "O processador vai ficar a 100% durante 5 minutos.\n\n" +
                "O PC vai aquecer e ficar lento durante o teste. Feche o que estiver a fazer antes de começar.\n\n" +
                "Continuar?"))
            return;

        _busy = true;
        _cts = new CancellationTokenSource();
        StressButton.Content = "Parar teste";
        ScanButton.IsEnabled = false;
        StressText.Visibility = Visibility.Visible;
        Progress.Visibility = Visibility.Visible;

        try
        {
            var progress = new Progress<string>(text => StressText.Text = text);
            var summary = await Hardware.StressTestAsync(TimeSpan.FromMinutes(5), progress, _cts.Token);

            StressText.Text = summary;
            Ui.Inform(this, "Teste de estresse", summary);

            await ScanAsync();
        }
        catch (OperationCanceledException)
        {
            StressText.Text = "Teste interrompido.";
        }
        catch (Exception ex)
        {
            Logger.Error("Hardware", "Falha no teste de estresse", ex);
            StressText.Text = "O teste falhou. Consulte o registo.";
        }
        finally
        {
            _busy = false;
            _cts?.Dispose();
            _cts = null;
            StressButton.Content = "Teste de estresse";
            ScanButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }
}
