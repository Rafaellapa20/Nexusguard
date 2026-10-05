using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard.Views;

/// <summary>Um grupo do catálogo, só para a vista poder mostrar cabeçalhos.</summary>
public sealed record CatalogSection(string Name, List<CatalogApp> Apps);

public partial class InstallView : UserControl
{
    private readonly List<CatalogApp> _all = Catalog.All();
    private readonly ObservableCollection<CatalogApp> _queue = new();

    private ConsoleSink? _sink;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public InstallView()
    {
        InitializeComponent();

        QueueList.ItemsSource = _queue;

        GroupList.ItemsSource = _all
            .GroupBy(a => a.Group)
            .Select(g => new CatalogSection(g.Key, g.ToList()))
            .ToList();

        BuildPresets();

        Loaded += (_, _) =>
        {
            _sink ??= new ConsoleSink(LogBox);

            if (!AppUpdater.IsAvailable)
            {
                WingetWarning.Visibility = Visibility.Visible;
                InstallButton.IsEnabled = false;
            }
        };
    }

    // ---------------- conjuntos prontos ----------------

    private void BuildPresets()
    {
        foreach (var (name, ids) in Catalog.Presets)
        {
            var chip = new ToggleButton
            {
                Content = name,
                Style = (Style)FindResource("Chip"),
                Tag = ids
            };

            chip.Click += OnPreset;
            PresetPanel.Children.Add(chip);
        }
    }

    /// <summary>
    /// Um conjunto substitui a seleção em vez de a acumular: clicar em "Escritório" depois de
    /// "Desenvolvedor" deve dar o escritório, não a soma dos dois.
    /// </summary>
    private void OnPreset(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not ToggleButton chip || chip.Tag is not string[] ids) return;

        foreach (ToggleButton other in PresetPanel.Children.OfType<ToggleButton>())
            other.IsChecked = ReferenceEquals(other, chip) && chip.IsChecked == true;

        var wanted = chip.IsChecked == true ? ids : Array.Empty<string>();

        foreach (var app in _all)
            app.Selected = wanted.Contains(app.Id, StringComparer.OrdinalIgnoreCase);

        RefreshQueue();
    }

    // ---------------- seleção ----------------

    private void OnPick(object sender, RoutedEventArgs e)
    {
        // Marcar à mão deixa de corresponder a um conjunto; manter o chip aceso seria mentira.
        foreach (ToggleButton chip in PresetPanel.Children.OfType<ToggleButton>())
            chip.IsChecked = false;

        RefreshQueue();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        foreach (var app in _all) app.Selected = false;
        foreach (ToggleButton chip in PresetPanel.Children.OfType<ToggleButton>()) chip.IsChecked = false;

        RefreshQueue();
    }

    private void RefreshQueue()
    {
        var picked = _all.Where(a => a.Selected).ToList();

        _queue.Clear();
        foreach (var app in picked) _queue.Add(app);

        CountText.Text = picked.Count switch
        {
            0 => "Nenhum selecionado",
            1 => "1 programa selecionado",
            _ => $"{picked.Count} programas selecionados"
        };

        QueueCount.Text = picked.Count == 0 ? "vazia" : $"{picked.Count} na fila";

        InstallButton.Content = picked.Count switch
        {
            0 => "Instalar",
            1 => "Instalar 1 programa",
            _ => $"Instalar {picked.Count} programas"
        };

        InstallButton.IsEnabled = !_busy && picked.Count > 0 && AppUpdater.IsAvailable;
        SaveButton.IsEnabled = picked.Count > 0;
    }

    // ---------------- instalação ----------------

    private void SetBusy(bool busy)
    {
        _busy = busy;

        InstallButton.IsEnabled = !busy && _queue.Count > 0 && AppUpdater.IsAvailable;
        ClearButton.IsEnabled = !busy;
        LoadButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy && _queue.Count > 0;
        ShutdownWhenDone.IsEnabled = !busy;

        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        foreach (ToggleButton chip in PresetPanel.Children.OfType<ToggleButton>())
            chip.IsEnabled = !busy;
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_busy || _queue.Count == 0) return;

        var apps = _queue.ToList();
        var shutdown = ShutdownWhenDone.IsChecked == true;

        var message = $"Instalar {apps.Count} programas, um a um, sem mais perguntas?"
                    + (shutdown ? "\n\nO PC desliga-se no fim." : "");

        if (!Ui.Confirm(this, "Instalar em lote", message)) return;

        _cts = new CancellationTokenSource();
        SetBusy(true);
        LogExpander.IsExpanded = true;

        Progress.Maximum = apps.Count;
        Progress.Value = 0;

        var progress = new Progress<(int Done, int Total)>(p =>
        {
            Progress.Value = p.Done;
            QueueCount.Text = $"{p.Done} de {p.Total}";
        });

        try
        {
            var result = await Catalog.InstallAsync(apps, _sink!.Write, progress, _cts.Token);

            QueueCount.Text = result.Summary;

            if (result.Failed > 0)
                Ui.Warn(this, "Instalação em lote", result.Summary + "\n\nVeja os detalhes para saber quais.");
            else
                Ui.Inform(this, "Instalação em lote", result.Summary);

            History.Add("Instalação em lote", result.Summary);

            if (shutdown && result.Failed == 0) await ShutdownAsync();
        }
        catch (OperationCanceledException)
        {
            QueueCount.Text = "parada";
            _sink?.Write("=== Parada a pedido. O programa em curso termina sozinho. ===");
        }
        catch (Exception ex)
        {
            Logger.Error("Programas", "a instalação em lote falhou", ex);
            Ui.Warn(this, "Instalação em lote", "A instalação falhou. Veja os detalhes.");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            RefreshQueue();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>
    /// Desliga com um minuto de atraso em vez de imediatamente, para haver como desistir. O comando
    /// para abortar fica escrito no registo, que é onde quem está a ver vai procurar.
    /// </summary>
    private async Task ShutdownAsync()
    {
        _sink?.Write("=== O PC desliga-se dentro de 1 minuto. Para cancelar: shutdown /a ===");
        await Shell.RunAsync("shutdown.exe", "/s /t 60 /c \"NexusGuard: instalacao em lote concluida\"");
    }

    // ---------------- perfis ----------------

    private void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        var picked = _all.Where(a => a.Selected).ToList();
        if (picked.Count == 0) return;

        var dialog = new SaveFileDialog
        {
            Title = "Guardar perfil de instalação",
            FileName = "perfil-nexusguard.json",
            Filter = "Perfil do NexusGuard (*.json)|*.json",
            DefaultExt = ".json"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            Catalog.SaveProfile(dialog.FileName, picked);
            Ui.Inform(this, "Perfil guardado",
                $"{picked.Count} programas guardados. Leve o ficheiro para outro PC e carregue-o lá.");
        }
        catch (Exception ex)
        {
            Logger.Error("Programas", "não foi possível guardar o perfil", ex);
            Ui.Warn(this, "Perfil", "Não foi possível guardar o perfil nesse local.");
        }
    }

    private void OnLoadProfile(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var dialog = new OpenFileDialog
        {
            Title = "Carregar perfil de instalação",
            Filter = "Perfil do NexusGuard (*.json)|*.json",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true) return;

        string[] ids;

        try
        {
            ids = Catalog.LoadProfile(dialog.FileName);
        }
        catch (IOException ex)
        {
            Logger.Error("Programas", "não foi possível ler o perfil", ex);
            Ui.Warn(this, "Perfil", "Não foi possível ler esse ficheiro.");
            return;
        }

        if (ids.Length == 0)
        {
            Ui.Warn(this, "Perfil", "O ficheiro não tem nenhum programa reconhecível.");
            return;
        }

        foreach (var app in _all)
            app.Selected = ids.Contains(app.Id, StringComparer.OrdinalIgnoreCase);

        foreach (ToggleButton chip in PresetPanel.Children.OfType<ToggleButton>())
            chip.IsChecked = false;

        RefreshQueue();

        // Um perfil feito noutra versão pode trazer ids que este catálogo já não tem. Dizê-lo é
        // melhor do que instalar menos do que o perfil pedia sem avisar.
        var missing = ids.Length - _all.Count(a => a.Selected);

        if (missing > 0)
        {
            Ui.Warn(this, "Perfil",
                $"{missing} dos programas do perfil não estão neste catálogo e ficaram de fora.");
        }
    }
}
