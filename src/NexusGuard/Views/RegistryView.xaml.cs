using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard.Views;

public partial class RegistryView : UserControl
{
    private readonly ObservableCollection<RegistryCategory> _categories = new();
    private ConsoleSink? _sink;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _loadedOnce;

    public RegistryView()
    {
        InitializeComponent();
        CategoryList.ItemsSource = _categories;

        foreach (var category in RegistryCleaner.BuildCategories())
        {
            category.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(RegistryCategory.Selected) or nameof(RegistryCategory.Count))
                    UpdateTotal();
            };
            _categories.Add(category);
        }

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
        FixButton.IsEnabled = !busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTotal()
    {
        var total = _categories.Where(c => c.Selected).Sum(c => c.Count);
        TotalText.Text = total == 0 ? "0" : Fmt.Count(total);

        FixButton.Content = total == 0 ? "Corrigir" : $"Corrigir {Fmt.Count(total)} entradas";

        if (MainWindow.Instance is { } main) main.Snapshot.RegistryIssues = _categories.Sum(c => c.Count);
    }

    private async void OnScan(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy) return;

        SetBusy(true);
        _cts = new CancellationTokenSource();
        StatusLine.Text = "analisando o registro…";

        try
        {
            var token = _cts.Token;
            await Task.Run(() => RegistryCleaner.Scan(_categories.ToList(), token), token);

            UpdateTotal();
            StatusLine.Text = "análise concluída";

            if (!Fmt.IsAdmin) State.ShowNeedsAdmin("Corrigir entradas do sistema");
            else State.Hide();
        }
        catch (OperationCanceledException)
        {
            StatusLine.Text = "análise cancelada";
        }
        catch (Exception ex)
        {
            Logger.Error("Registro", "Falha na análise", ex);
            StatusLine.Text = "a análise falhou";
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async void OnFix(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var chosen = _categories.Where(c => c.Selected && c.Count > 0).ToList();
        var total = chosen.Sum(c => c.Count);

        if (total == 0)
        {
            Ui.Inform(this, "Registro", "Não há entradas inválidas selecionadas.");
            return;
        }

        if (!Ui.Confirm(this, "Corrigir registro",
                $"Serão removidas {Fmt.Count(total)} entradas inválidas em {chosen.Count} categoria(s).\n\n" +
                "Antes de remover, é exportado um arquivo .reg para a quarentena — pode reverter tudo " +
                "pelo Histórico.\n\nContinuar?"))
            return;

        SetBusy(true);
        ShowConsole(true);
        _cts = new CancellationTokenSource();
        StatusLine.Text = "corrigindo…";

        try
        {
            var (fixedCount, backup) = await RegistryCleaner.FixAsync(chosen, line => _sink?.Write(line), _cts.Token);

            StatusLine.Text = $"{Fmt.Count(fixedCount)} entradas corrigidas";

            await ScanAsync();

            Ui.Inform(this, "Registro corrigido",
                $"{Fmt.Count(fixedCount)} entradas removidas." +
                (backup is null ? "" : $"\n\nCópia de segurança: {backup}"));
        }
        catch (OperationCanceledException)
        {
            StatusLine.Text = "correção interrompida";
        }
        catch (Exception ex)
        {
            Logger.Error("Registro", "Falha ao corrigir", ex);
            StatusLine.Text = "a correção falhou";
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
