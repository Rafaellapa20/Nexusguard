using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard.Views;

public partial class UninstallView : UserControl
{
    private readonly List<InstalledApp> _all = new();
    private readonly ObservableCollection<InstalledApp> _shown = new();
    private ConsoleSink? _sink;
    private CancellationTokenSource? _cts;
    private string _filter = "Todos";
    private bool _busy;
    private bool _loadedOnce;

    public UninstallView()
    {
        InitializeComponent();
        AppList.ItemsSource = _shown;
        BuildChips();

        Loaded += (_, _) =>
        {
            _sink ??= new ConsoleSink(Output);

            if (_loadedOnce) return;
            _loadedOnce = true;
            Reload();
        };
    }

    private void BuildChips()
    {
        foreach (var label in new[] { "Todos", "Bloatware", "Inseguro", "Redundante", "Em uso" })
        {
            var name = label;

            var chip = new ToggleButton
            {
                Content = name,
                Style = (Style)FindResource("Chip"),
                IsChecked = name == _filter
            };

            chip.Checked += (_, _) =>
            {
                _filter = name;

                foreach (var other in Chips.Children.OfType<ToggleButton>())
                    if (!ReferenceEquals(other, chip)) other.IsChecked = false;

                ApplyFilter();
            };

            chip.Unchecked += (_, _) => { if (_filter == name) chip.IsChecked = true; };

            Chips.Children.Add(chip);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ReloadButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy;
    }

    private void Reload()
    {
        SetBusy(true);
        StatusLine.Text = "lendo os programas instalados…";

        try
        {
            _all.Clear();

            foreach (var app in Uninstaller.Load())
            {
                app.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(InstalledApp.Selected)) UpdateTotals();
                };
                _all.Add(app);
            }

            ApplyFilter();
            StatusLine.Text = "pronto";
        }
        catch (Exception ex)
        {
            Logger.Error("Desinstalar", "Falha ao ler os programas", ex);
            StatusLine.Text = "não foi possível ler a lista";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplyFilter()
    {
        _shown.Clear();

        var items = _filter switch
        {
            "Bloatware" => _all.Where(a => a.Classification == AppClass.Bloatware),
            "Inseguro" => _all.Where(a => a.Classification == AppClass.Unsafe),
            "Redundante" => _all.Where(a => a.Classification == AppClass.Redundant),
            "Em uso" => _all.Where(a => a.Classification == AppClass.InUse),
            _ => _all
        };

        foreach (var app in items) _shown.Add(app);

        UpdateTotals();
    }

    private void UpdateTotals()
    {
        var selected = _all.Count(a => a.Selected);
        var flagged = _all.Count(a => a.Classification is AppClass.Bloatware or AppClass.Unsafe or AppClass.Redundant);

        TotalText.Text = Fmt.Count(_all.Count);

        SubText.Text = flagged == 0
            ? "Nada marcado como bloatware ou inseguro."
            : $"{flagged} programa(s) marcados como bloatware, inseguros ou redundantes.";

        UninstallButton.Content = selected == 0 ? "Desinstalar" : $"Desinstalar selecionados ({selected})";
        UninstallButton.IsEnabled = selected > 0 && !_busy;
    }

    private void OnReload(object sender, RoutedEventArgs e) => Reload();

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var chosen = _all.Where(a => a.Selected).ToList();
        if (chosen.Count == 0) return;

        var blocked = chosen.Where(a => !a.CanUninstall).ToList();

        var message = $"Serão desinstalados {chosen.Count} programa(s):\n\n" +
                      string.Join("\n", chosen.Take(10).Select(a => $"• {a.Name}")) +
                      (chosen.Count > 10 ? $"\n… e mais {chosen.Count - 10}." : "");

        if (blocked.Count > 0)
            message += $"\n\n{blocked.Count} não têm desinstalador registado e serão ignorados.";

        message += "\n\nOs resíduos vão para a quarentena e podem ser repostos pelo Histórico. Continuar?";

        if (!Ui.Confirm(this, "Desinstalar programas", message)) return;

        SetBusy(true);
        ShowConsole(true);
        _cts = new CancellationTokenSource();

        var removed = 0;

        try
        {
            foreach (var app in chosen)
            {
                _cts.Token.ThrowIfCancellationRequested();

                StatusLine.Text = $"a desinstalar {app.Name}…";

                if (!await Uninstaller.UninstallAsync(app, line => _sink?.Write(line), _cts.Token)) continue;

                removed++;

                var residues = Uninstaller.FindResidues(app);

                if (residues.Count > 0)
                {
                    _sink?.Write($"{app.Name}: {residues.Count} pasta(s) de resíduos → quarentena");
                    var result = Uninstaller.QuarantineResidues(app, residues, _cts.Token);
                    _sink?.Write($"  {result.Moved} ficheiros ({Fmt.Bytes(result.Bytes)})");
                }

                app.Selected = false;
            }

            StatusLine.Text = $"{removed} de {chosen.Count} removidos";

            Ui.Inform(this, "Desinstalação concluída",
                $"{removed} de {chosen.Count} programa(s) removidos.\n\n" +
                "Os resíduos ficaram em quarentena e podem ser repostos pelo Histórico.");

            Reload();
        }
        catch (OperationCanceledException)
        {
            StatusLine.Text = "desinstalação interrompida";
        }
        catch (Exception ex)
        {
            Logger.Error("Desinstalar", "Falha na desinstalação", ex);
            StatusLine.Text = "a desinstalação falhou";
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnToggleConsole(object sender, RoutedEventArgs e) =>
        ShowConsole(Output.Visibility != Visibility.Visible);

    private void ShowConsole(bool show)
    {
        Output.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ToggleConsole.Content = show ? "Ocultar" : "Mostrar";
    }
}
