using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard.Views;

public partial class PerformanceView : UserControl
{
    private readonly ObservableCollection<Tweak> _tweaks = new();
    private bool _loadedOnce;
    private bool _busy;

    public PerformanceView()
    {
        InitializeComponent();

        DataContext = SystemMonitor.Instance;
        TweakList.ItemsSource = _tweaks;

        foreach (var tweak in Tweaks.Build()) _tweaks.Add(tweak);

        CpuInfo.Text = $"{SystemMonitor.Instance.CpuName} · {SystemMonitor.Instance.CpuCores} núcleos lógicos";
        UpdateModeHint();

        Loaded += async (_, _) =>
        {
            if (_loadedOnce) return;
            _loadedOnce = true;

            await Task.WhenAll(RefreshProcessesAsync(), RefreshTweaksAsync());
        };
    }

    private PerformanceMode Mode =>
        ModeGaming.IsChecked == true ? PerformanceMode.Gaming :
        ModeWork.IsChecked == true ? PerformanceMode.Work :
        PerformanceMode.Balanced;

    private void UpdateModeHint()
    {
        ModeHint.Text = Mode switch
        {
            PerformanceMode.Gaming =>
                "Jogos: alto desempenho, sem transparências e com os aplicativos em segundo plano travados.",
            PerformanceMode.Work =>
                "Trabalho: alto desempenho e aplicativos em segundo plano limitados, mantendo os efeitos visuais.",
            _ => "Equilibrado: o Windows decide tudo sozinho. É o estado de fábrica."
        };
    }

    private async Task RefreshTweaksAsync()
    {
        try
        {
            await Tweaks.ReadStateAsync(_tweaks);

            var active = _tweaks.Count(t => t.Enabled);
            PlanStatus.Text = $"{active} de {_tweaks.Count} ajustes aplicados.";
        }
        catch (Exception ex)
        {
            Logger.Error("Performance", "Falha ao ler o estado dos ajustes", ex);
            PlanStatus.Text = "Não foi possível ler o estado dos ajustes.";
        }
    }

    private async void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        UpdateModeHint();

        if (_busy) return;
        _busy = true;

        try
        {
            PlanStatus.Text = "aplicando o modo…";
            PlanStatus.Text = await Tweaks.ApplyModeAsync(Mode, _tweaks);
            await RefreshTweaksAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("Performance", "Falha ao aplicar o modo", ex);
            PlanStatus.Text = "Não foi possível aplicar o modo.";
        }
        finally
        {
            _busy = false;
        }
    }

    private async void OnTweak(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: Tweak tweak } box) return;

        var wanted = box.IsChecked == true;
        box.IsEnabled = false;

        try
        {
            var (ok, message) = await Tweaks.ApplyAsync(tweak, wanted);

            if (!ok)
            {
                tweak.Enabled = !wanted;
                box.IsChecked = !wanted;
                Ui.Warn(this, "Performance", message);
                return;
            }

            PlanStatus.Text = message;
        }
        finally
        {
            box.IsEnabled = true;
        }
    }

    // ---------------- Memória ----------------

    private async void OnFreeMemory(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        _busy = true;
        FreeButton.IsEnabled = false;

        try
        {
            var progress = new Progress<string>(text => FreeResult.Text = text);
            var result = await MemoryOptimizer.OptimizeAsync(aggressive: true, progress);

            FreeResult.Text = result.Message;
            History.Add("Performance", "Memória liberada", Fmt.Bytes(result.FreedBytes));

            await RefreshProcessesAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("Memória", "Falha ao liberar memória", ex);
            FreeResult.Text = "Não foi possível liberar memória.";
        }
        finally
        {
            _busy = false;
            FreeButton.IsEnabled = true;
        }
    }

    // ---------------- Processos ----------------

    private async void OnRefreshProcesses(object sender, RoutedEventArgs e) => await RefreshProcessesAsync();

    private async Task RefreshProcessesAsync()
    {
        RefreshButton.IsEnabled = false;
        ProcHint.Text = "coletando…";

        try
        {
            var snapshot = await SystemMonitor.SampleProcessesAsync(600);

            var top = snapshot
                .Where(p => p.Pid > 4)
                .OrderByDescending(p => p.CpuPercent)
                .ThenByDescending(p => p.WorkingSet)
                .Take(40)
                .ToList();

            ProcessList.ItemsSource = top;
            ProcHint.Text = $"{snapshot.Count} processos ativos";
        }
        catch (Exception ex)
        {
            Logger.Error("Performance", "Falha ao listar processos", ex);
            ProcHint.Text = "não foi possível listar os processos";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async void OnTrim(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int pid }) return;

        ProcHint.Text = MemoryOptimizer.TrimProcess(pid)
            ? $"PID {pid} compactado"
            : $"não foi possível compactar o PID {pid}";

        await RefreshProcessesAsync();
    }

    private async void OnKill(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int pid }) return;

        var item = (ProcessList.ItemsSource as IEnumerable<ProcessSnapshot>)?.FirstOrDefault(p => p.Pid == pid);
        var name = item?.Name ?? $"PID {pid}";

        if (!Ui.Confirm(this, "Terminar processo",
                $"Terminar «{name}» (PID {pid})?\n\nTrabalho não salvo nesse aplicativo será perdido."))
            return;

        if (MemoryOptimizer.KillProcess(pid, out var message)) await RefreshProcessesAsync();
        else Ui.Warn(this, "Terminar processo", message);
    }
}
