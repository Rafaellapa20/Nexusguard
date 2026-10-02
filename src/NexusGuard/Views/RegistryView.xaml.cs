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

        State.PrimaryClicked += (_, _) => App.RestartElevated();
        State.SecondaryClicked += (_, _) => State.Hide();

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

            var needAdmin = RegistryCleaner.CountNeedingAdmin(_categories);

            if (!Fmt.IsAdmin && needAdmin > 0)
                State.Show("Registro", "Parte destas entradas precisa de administrador",
                    $"{Fmt.Count(needAdmin)} das entradas encontradas estão em HKLM ou HKCR. Sem elevação o " +
                    "Windows recusa alterá-las, e a correção só trata as restantes.",
                    StateSeverity.Info, "Continuar como administrador", "Agora não");
            else
                State.Hide();
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

        var needAdmin = RegistryCleaner.CountNeedingAdmin(chosen);

        var warning = !Fmt.IsAdmin && needAdmin > 0
            ? $"\n\nAtenção: {Fmt.Count(needAdmin)} dessas entradas são do sistema (HKLM/HKCR) e vão ser " +
              "recusadas pelo Windows sem privilégios de administrador."
            : "";

        if (!Ui.Confirm(this, "Corrigir registro",
                $"Serão removidas {Fmt.Count(total)} entradas inválidas em {chosen.Count} categoria(s)." +
                warning +
                "\n\nAntes de remover, é exportado um arquivo .reg — pode reverter tudo pelo Histórico." +
                "\n\nContinuar?"))
            return;

        SetBusy(true);
        ShowConsole(true);
        _cts = new CancellationTokenSource();
        StatusLine.Text = "corrigindo…";

        try
        {
            var result = await RegistryCleaner.FixAsync(chosen, line => _sink?.Write(line), _cts.Token);

            StatusLine.Text = result.Failed > 0
                ? $"{Fmt.Count(result.Fixed)} removidas · {Fmt.Count(result.Failed)} recusadas"
                : $"{Fmt.Count(result.Fixed)} entradas removidas";

            await ScanAsync();

            // Se alguma coisa foi recusada, o utilizador tem de ver porquê sem ir procurar.
            if (result.Failed > 0)
            {
                ShowConsole(true);

                if (!Fmt.IsAdmin)
                    State.Show("Registro", "Entradas do sistema recusadas",
                        $"{Fmt.Count(result.Failed)} entradas vivem em HKLM ou HKCR e o Windows não deixa " +
                        "alterá-las sem elevação. Reinicie como administrador para tratar dessas.",
                        StateSeverity.Warning, "Continuar como administrador", "Agora não");
                else
                    State.Show("Registro", "Algumas entradas não foram removidas",
                        $"{Fmt.Count(result.Failed)} entradas resistiram à remoção, por estarem em uso ou " +
                        "protegidas. O detalhe está na saída abaixo.",
                        StateSeverity.Warning);
            }

            var detail = new System.Text.StringBuilder();
            detail.AppendLine($"Removidas: {Fmt.Count(result.Fixed)}");

            if (result.Failed > 0) detail.AppendLine($"Recusadas pelo Windows: {Fmt.Count(result.Failed)}");
            if (result.Vanished > 0) detail.AppendLine($"Já não existiam: {Fmt.Count(result.Vanished)}");
            if (result.BackupFile is not null) detail.Append($"\nCópia de segurança: {result.BackupFile}");

            Ui.Inform(this, result.AnythingDone ? "Registro corrigido" : "Nada foi alterado",
                detail.ToString());
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
