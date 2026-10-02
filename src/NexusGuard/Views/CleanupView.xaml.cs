using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard.Views;

public partial class CleanupView : UserControl
{
    private readonly ObservableCollection<CleanTarget> _targets = new();
    private readonly ObservableCollection<QuarantineBatch> _batches = new();
    private ConsoleSink? _sink;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _scanned;

    public CleanupView()
    {
        InitializeComponent();

        TargetList.ItemsSource = _targets;
        BatchList.ItemsSource = _batches;

        foreach (var target in DiskCleaner.BuildTargets())
        {
            target.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(CleanTarget.Selected) or nameof(CleanTarget.Size)) UpdateTotal();
            };
            _targets.Add(target);
        }

        Loaded += async (_, _) =>
        {
            _sink ??= new ConsoleSink(Output);
            RefreshQuarantine();

            if (_scanned) return;
            _scanned = true;
            await ScanAsync();
        };
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy;
        CleanButton.IsEnabled = !busy;
        SelectAllButton.IsEnabled = !busy;
        DismButton.IsEnabled = !busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTotal()
    {
        var selected = _targets.Where(t => t.Selected && t.Size > 0).ToList();
        var total = selected.Sum(t => t.Size);
        var files = selected.Sum(t => (long)t.FileCount);

        TotalText.Text = total > 0 ? Fmt.Bytes(total) : "0 B";
        TotalDetail.Text = total > 0
            ? $"{Fmt.Count(files)} arquivos em {selected.Count} categoria(s) selecionada(s)."
            : "Nada selecionado com conteúdo a remover.";

        if (MainWindow.Instance is { } main) main.Snapshot.RecoverableBytes = _targets.Sum(t => Math.Max(0, t.Size));
    }

    // ---------------- Análise ----------------

    private async void OnScan(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_busy) return;

        SetBusy(true);
        _cts = new CancellationTokenSource();
        StatusLine.Text = "analisando…";

        try
        {
            foreach (var target in _targets)
            {
                _cts.Token.ThrowIfCancellationRequested();

                if (target.NeedsAdmin && !Fmt.IsAdmin)
                {
                    target.Status = "requer administrador";
                    target.Size = 0;
                    continue;
                }

                StatusLine.Text = $"analisando: {target.Name}";
                await DiskCleaner.ScanAsync(target, _cts.Token);
                UpdateTotal();
            }

            StatusLine.Text = "análise concluída";

            if (_targets.All(t => t.Size <= 0))
                State.ShowNothingToClean(LastCleanText());
            else
                State.Hide();
        }
        catch (OperationCanceledException)
        {
            StatusLine.Text = "análise cancelada";
        }
        catch (Exception ex)
        {
            Logger.Error("Limpeza", "Falha na análise", ex);
            StatusLine.Text = "a análise falhou";
        }
        finally
        {
            UpdateTotal();
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static string LastCleanText()
    {
        History.EnsureLoaded();

        var last = History.Entries.FirstOrDefault(e => e.Module == "Limpeza");
        if (last is null) return "ainda não houve nenhuma";

        var hours = (int)(DateTime.Now - last.When).TotalHours;
        return hours < 1 ? "há menos de uma hora" : hours < 24 ? $"há {hours} h" : $"há {hours / 24} dia(s)";
    }

    // ---------------- Limpeza ----------------

    private async void OnClean(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var chosen = _targets.Where(t => t.Selected).ToList();

        if (chosen.Count == 0)
        {
            Ui.Inform(this, "Limpeza", "Selecione pelo menos uma categoria.");
            return;
        }

        SetBusy(true);
        StatusLine.Text = "preparando a pré-visualização…";

        List<(CleanTarget target, List<CleanFile> files)> groups;

        try
        {
            var token = (_cts = new CancellationTokenSource()).Token;

            groups = await Task.Run(() => chosen
                .Where(t => !t.NeedsAdmin || Fmt.IsAdmin)
                .Select(t => (target: t, files: DiskCleaner.Enumerate(t, token)))
                .Where(g => g.files.Count > 0)
                .ToList(), token);
        }
        catch (OperationCanceledException)
        {
            StatusLine.Text = "cancelado";
            SetBusy(false);
            return;
        }
        catch (Exception ex)
        {
            Logger.Error("Limpeza", "Falha ao preparar a pré-visualização", ex);
            StatusLine.Text = "não foi possível preparar a limpeza";
            SetBusy(false);
            return;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }

        if (groups.Count == 0)
        {
            SetBusy(false);
            State.ShowNothingToClean(LastCleanText());
            return;
        }

        List<string>? accepted = null;
        var makeRestorePoint = false;

        if (Settings.Current.ConfirmEachChange)
        {
            var preview = new CleanPreviewWindow(groups) { Owner = Window.GetWindow(this) };

            if (preview.ShowDialog() != true)
            {
                StatusLine.Text = "cancelado pelo usuário";
                SetBusy(false);
                return;
            }

            accepted = preview.Accepted;
            makeRestorePoint = preview.CreateRestorePoint;
        }
        else
        {
            makeRestorePoint = Settings.Current.CreateRestorePoint;
        }

        ShowConsole(true);
        _cts = new CancellationTokenSource();

        long freed = 0;
        var files = 0;
        var locked = 0;

        try
        {
            if (makeRestorePoint && Fmt.IsAdmin)
            {
                StatusLine.Text = "criando ponto de restauração…";
                var (ok, message) = await BackupManager.CreateRestorePointAsync(
                    $"NexusGuard {DateTime.Now:yyyy-MM-dd HH:mm}", line => _sink?.Write(line), _cts.Token);

                _sink?.Write(message);
                if (!ok) _sink?.Write("A limpeza continua — o ponto de restauração é opcional.");
            }

            var progress = new Progress<string>(line => _sink?.Write(line));

            foreach (var (target, _) in groups)
            {
                _cts.Token.ThrowIfCancellationRequested();

                StatusLine.Text = $"limpando: {target.Name}";
                _sink?.Write($"=== {target.Name} ===");

                var outcome = await DiskCleaner.CleanAsync(target, progress, _cts.Token, accepted);

                freed += outcome.BytesFreed;
                files += outcome.FilesDeleted;
                locked += outcome.Locked;

                _sink?.Write($"  {Fmt.Bytes(outcome.BytesFreed)} · {outcome.FilesDeleted} arquivos · " +
                             $"{outcome.Locked} em uso · {outcome.ProtectedSkipped} protegidos");
            }

            StatusLine.Text = $"concluído — {Fmt.Bytes(freed)}";
            SystemMonitor.Instance.RefreshDrives();
            RefreshQuarantine();

            if (locked > 0) State.ShowLockedFiles(locked, files + locked);
            else State.Hide();

            Ui.Inform(this, "Limpeza concluída",
                $"{Fmt.Bytes(freed)} liberados · {Fmt.Count(files)} arquivos.\n\n" +
                (Settings.Current.UseQuarantine
                    ? $"Ficaram em quarentena durante {Settings.Current.QuarantineDays} dias — pode repô-los no Histórico."
                    : "Os arquivos foram apagados de forma definitiva."));
        }
        catch (OperationCanceledException)
        {
            StatusLine.Text = "limpeza cancelada";
        }
        catch (Exception ex)
        {
            Logger.Error("Limpeza", "Falha na limpeza", ex);
            StatusLine.Text = "a limpeza falhou";
        }
        finally
        {
            UpdateTotal();
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusLine.Text = "cancelando…";
    }

    private void OnSelectSafe(object sender, RoutedEventArgs e)
    {
        foreach (var target in _targets) target.Selected = target.Risk == CleanRisk.Safe;
        UpdateTotal();
    }

    // ---------------- Quarentena ----------------

    private void RefreshQuarantine()
    {
        _batches.Clear();
        foreach (var batch in Quarantine.List()) _batches.Add(batch);

        var bytes = _batches.Sum(b => b.Bytes);

        QuarantineSummary.Text = _batches.Count == 0
            ? "A quarentena está vazia."
            : $"{_batches.Count} lote(s) · {Fmt.Bytes(bytes)} · purga automática aos {Settings.Current.QuarantineDays} dias.";

        QuarantineEmpty.Visibility = _batches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyAllButton.IsEnabled = _batches.Count > 0;
    }

    private void OnRestoreBatch(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: QuarantineBatch batch }) return;

        if (!Ui.Confirm(this, "Restaurar lote",
                $"{batch.Label}\n{batch.Summary}\n\nOs arquivos voltam aos locais originais. Continuar?"))
            return;

        var (restored, skipped) = Quarantine.Restore(batch);
        RefreshQuarantine();

        Ui.Inform(this, "Restauro",
            restored > 0
                ? $"{Fmt.Count(restored)} arquivos repostos" + (skipped > 0 ? $" ({skipped} ignorados)." : ".")
                : "Nada foi reposto — os arquivos já não estão na quarentena ou os destinos já existem.");
    }

    private void OnDeleteBatch(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: QuarantineBatch batch }) return;

        if (!Ui.Confirm(this, "Apagar definitivamente",
                $"{batch.Label}\n{batch.Summary}\n\nIsto apaga os arquivos de vez. Não há como voltar atrás. Continuar?"))
            return;

        Quarantine.Delete(batch);
        RefreshQuarantine();
    }

    private void OnEmptyQuarantine(object sender, RoutedEventArgs e)
    {
        if (_batches.Count == 0) return;

        if (!Ui.Confirm(this, "Esvaziar quarentena",
                $"{_batches.Count} lote(s) · {Fmt.Bytes(_batches.Sum(b => b.Bytes))}\n\n" +
                "Tudo vai ser apagado de forma definitiva. Continuar?"))
            return;

        var (count, bytes) = Quarantine.EmptyAll();
        RefreshQuarantine();

        Ui.Inform(this, "Quarentena", $"{count} lote(s) apagados · {Fmt.Bytes(bytes)} liberados.");
    }

    private void OnOpenQuarantine(object sender, RoutedEventArgs e) => Shell.OpenExternal(Paths.Quarantine);

    // ---------------- Avançado ----------------

    private async void OnComponentCleanup(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!Fmt.IsAdmin)
        {
            State.ShowNeedsAdmin("Compactar o armazenamento de componentes");
            return;
        }

        if (!Ui.Confirm(this, "Compactar componentes",
                "O DISM vai remover versões antigas de componentes do Windows.\n\n" +
                "Pode levar 10 a 30 minutos e impede desinstalar atualizações já instaladas. Continuar?"))
            return;

        SetBusy(true);
        ShowConsole(true);
        _cts = new CancellationTokenSource();
        StatusLine.Text = "compactando componentes (DISM)…";

        try
        {
            await DiskCleaner.RunWindowsComponentCleanupAsync(line => _sink?.Write(line), _cts.Token);
            StatusLine.Text = "DISM concluído";
            SystemMonitor.Instance.RefreshDrives();
        }
        catch (OperationCanceledException)
        {
            StatusLine.Text = "DISM cancelado";
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnOpenCleanmgr(object sender, RoutedEventArgs e) => Shell.OpenExternal("cleanmgr.exe");

    private void OnOpenStorage(object sender, RoutedEventArgs e) => Shell.OpenExternal("ms-settings:storagesense");

    private void OnToggleConsole(object sender, RoutedEventArgs e) =>
        ShowConsole(Output.Visibility != Visibility.Visible);

    private void ShowConsole(bool show)
    {
        Output.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ToggleConsole.Content = show ? "Ocultar" : "Mostrar";
    }
}
