using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard.Views;

public partial class DriversView : UserControl
{
    private enum Tab { Drivers, Windows, Devices }

    private readonly ObservableCollection<WindowsUpdateItem> _driverUpdates = new();
    private readonly ObservableCollection<WindowsUpdateItem> _windowsUpdates = new();
    private readonly ObservableCollection<DeviceProblem> _devices = new();

    private ConsoleSink? _sink;
    private CancellationTokenSource? _cts;
    private Tab _tab = Tab.Drivers;
    private bool _busy;
    private bool _loadedOnce;
    private readonly HashSet<Tab> _scanned = new();

    public DriversView()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        UpdateList.ItemsSource = _driverUpdates;

        Loaded += async (_, _) =>
        {
            _sink ??= new ConsoleSink(Output);

            if (_loadedOnce) return;
            _loadedOnce = true;
            await ScanAsync();
        };
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy && _tab != Tab.Devices;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- Separadores ----------------

    private async void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        _tab = TabWindows.IsChecked == true ? Tab.Windows
            : TabDevices.IsChecked == true ? Tab.Devices
            : Tab.Drivers;

        ApplyTab();

        if (_scanned.Add(_tab)) await ScanAsync();
        else UpdateHead();
    }

    private void ApplyTab()
    {
        var devices = _tab == Tab.Devices;

        UpdateScroll.Visibility = devices ? Visibility.Collapsed : Visibility.Visible;
        DeviceScroll.Visibility = devices ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = !devices && !_busy;
        InstallButton.Visibility = devices ? Visibility.Collapsed : Visibility.Visible;

        UpdateList.ItemsSource = _tab == Tab.Windows ? _windowsUpdates : _driverUpdates;
        UpdateHead();
    }

    private void UpdateHead()
    {
        switch (_tab)
        {
            case Tab.Drivers:
                HeadTitle.Text = _driverUpdates.Count.ToString(Fmt.Pt);
                HeadSub.Text = _driverUpdates.Count == 0
                    ? "Nenhuma atualização de driver pendente no Windows Update."
                    : "Drivers com versão mais recente oferecida pelo Windows Update.";
                EmptyText.Text = "Nenhum driver por atualizar.\n\nOs drivers da placa gráfica são muitas vezes " +
                                 "distribuídos apenas pelo fabricante (NVIDIA, AMD, Intel).";
                EmptyText.Visibility = _driverUpdates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                break;

            case Tab.Windows:
                HeadTitle.Text = _windowsUpdates.Count.ToString(Fmt.Pt);
                HeadSub.Text = _windowsUpdates.Count == 0
                    ? "O Windows está atualizado."
                    : "Atualizações de segurança e funcionalidades pendentes.";
                EmptyText.Text = "O Windows está atualizado.";
                EmptyText.Visibility = _windowsUpdates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                break;

            default:
                HeadTitle.Text = _devices.Count.ToString(Fmt.Pt);
                HeadSub.Text = _devices.Count == 0
                    ? "Nenhum dispositivo com erro ou sem driver."
                    : "Dispositivos com erro, degradados ou sem driver instalado.";
                EmptyText.Text = "Todos os dispositivos estão a funcionar.";
                EmptyText.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                break;
        }
    }

    // ---------------- Procura ----------------

    private async void OnScan(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy) return;

        SetBusy(true);
        _cts = new CancellationTokenSource();
        _scanned.Add(_tab);

        EmptyText.Text = _tab == Tab.Devices
            ? "Inventariando dispositivos…"
            : "Consultando o Windows Update…\n\nEsta procura costuma demorar entre 20 segundos e um minuto.";
        EmptyText.Visibility = Visibility.Visible;

        try
        {
            if (_tab == Tab.Devices)
            {
                StatusLine.Text = "inventariando dispositivos…";
                var problems = await UpdateAgent.GetDeviceProblemsAsync(_cts.Token);

                _devices.Clear();
                foreach (var d in problems) _devices.Add(d);

                StatusLine.Text = $"{problems.Count} dispositivos com problemas";
            }
            else
            {
                var drivers = _tab == Tab.Drivers;
                StatusLine.Text = drivers ? "a procurar drivers…" : "a procurar atualizações do Windows…";
                _sink?.Write(StatusLine.Text);

                var (items, error) = await UpdateAgent.SearchAsync(drivers, _cts.Token);
                var target = drivers ? _driverUpdates : _windowsUpdates;

                target.Clear();
                foreach (var u in items) target.Add(u);

                if (error is not null)
                {
                    _sink?.Write($"Windows Update: {error}");
                    StatusLine.Text = "a procura devolveu um erro — ver detalhe";
                    ShowConsole(true);
                }
                else
                {
                    StatusLine.Text = $"{items.Count} encontradas";
                }
            }

            UpdateHead();
        }
        catch (OperationCanceledException)
        {
            StatusLine.Text = "procura cancelada";
        }
        catch (Exception ex)
        {
            Logger.Error("Drivers", "Falha na procura", ex);
            StatusLine.Text = "a procura falhou";
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ---------------- Instalação ----------------

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_busy || _tab == Tab.Devices) return;

        var drivers = _tab == Tab.Drivers;
        var source = drivers ? _driverUpdates : _windowsUpdates;
        var chosen = source.Where(u => u.Selected).ToList();

        if (chosen.Count == 0)
        {
            Ui.Inform(this, "Instalar", "Selecione pelo menos um item.");
            return;
        }

        if (!Fmt.IsAdmin)
        {
            Ui.Warn(this, "Privilégios necessários",
                "Instalar atualizações exige administrador. Use «Reiniciar como administrador» na barra lateral.");
            return;
        }

        var label = drivers ? "driver(s)" : "atualização(ões) do Windows";

        if (!Ui.Confirm(this, "Instalar",
                $"Vão ser transferidos e instalados {chosen.Count} {label}:\n\n" +
                string.Join("\n", chosen.Take(8).Select(u => $"• {u.Title}")) +
                (chosen.Count > 8 ? $"\n… e mais {chosen.Count - 8}." : "") +
                "\n\nGuarde o seu trabalho — pode ser necessário reiniciar. Continuar?"))
            return;

        SetBusy(true);
        ShowConsole(true);
        _cts = new CancellationTokenSource();
        StatusLine.Text = "a instalar…";

        foreach (var u in chosen) u.Status = "a instalar…";

        try
        {
            var result = await UpdateAgent.InstallAsync(chosen, drivers, line => _sink?.Write(line), _cts.Token);

            foreach (var u in chosen) u.Status = result.Installed > 0 ? "processada" : "não instalada";

            StatusLine.Text = result.Message;

            Ui.Inform(this, "Instalação concluída",
                result.Message + (result.RebootRequired
                    ? "\n\nÉ necessário reiniciar o computador para concluir."
                    : ""));

            await ScanAsync();
        }
        catch (OperationCanceledException)
        {
            StatusLine.Text = "instalação interrompida";
            foreach (var u in chosen) u.Status = "interrompida";
        }
        catch (Exception ex)
        {
            Logger.Error("Drivers", "Falha ao instalar", ex);
            StatusLine.Text = "a instalação falhou";
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
        StatusLine.Text = "a interromper…";
    }

    private void OnOpenDeviceManager(object sender, RoutedEventArgs e) => UpdateAgent.OpenDeviceManager();

    private void OnOpenWindowsUpdate(object sender, RoutedEventArgs e) => UpdateAgent.OpenWindowsUpdate();

    private void OnToggleConsole(object sender, RoutedEventArgs e) =>
        ShowConsole(Output.Visibility != Visibility.Visible);

    private void ShowConsole(bool show)
    {
        Output.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ToggleConsole.Content = show ? "Ocultar" : "Mostrar";
    }
}
