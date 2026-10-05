using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard.Views;

public partial class BackupView : UserControl
{
    private readonly ObservableCollection<BackupSource> _sources = new();
    private readonly ObservableCollection<BackupEntry> _backups = new();

    private ConsoleSink? _sink;
    private CancellationTokenSource? _cts;
    private string? _destination;
    private bool _busy;
    private bool _loadedOnce;

    public BackupView()
    {
        InitializeComponent();

        SourceList.ItemsSource = _sources;
        BackupList.ItemsSource = _backups;

        foreach (var s in BackupManager.DefaultSources())
        {
            s.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(BackupSource.Selected) or nameof(BackupSource.Size)) UpdateTotal();
            };
            _sources.Add(s);
        }

        foreach (var radio in new[] { ModeIncremental, ModeMirror, ModeSnapshot })
            radio.Checked += (_, _) => UpdateModeHint();

        Loaded += (_, _) =>
        {
            _sink ??= new ConsoleSink(Output);

            if (_loadedOnce) return;
            _loadedOnce = true;

            RestoreSavedChoices();
            LoadDrives();
            UpdateTotal();
        };
    }

    /// <summary>Repõe o destino e as pastas usados da última vez.</summary>
    private void RestoreSavedChoices()
    {
        var saved = Settings.Current.BackupSources;

        if (saved.Length > 0)
        {
            foreach (var source in _sources)
                source.Selected = saved.Contains(source.Path, StringComparer.OrdinalIgnoreCase);
        }

        SkipCloud.IsChecked = Settings.Current.BackupSkipCloudOnly;
    }

    private BackupMode Mode =>
        ModeMirror.IsChecked == true ? BackupMode.Mirror :
        ModeSnapshot.IsChecked == true ? BackupMode.Snapshot :
        BackupMode.Incremental;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RunButton.IsEnabled = !busy;
        MeasureButton.IsEnabled = !busy;
        RestorePointButton.IsEnabled = !busy;
        SystemImageButton.IsEnabled = !busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTotal()
    {
        var chosen = _sources.Where(s => s.Selected).ToList();
        var known = chosen.Where(s => s.Size >= 0).ToList();
        var total = known.Sum(s => s.Size);

        TotalText.Text = known.Count == 0 ? "—" : Fmt.Bytes(total);

        TotalSub.Text = chosen.Count == 0
            ? "Nenhuma pasta selecionada."
            : known.Count == chosen.Count
                ? $"{chosen.Count} pasta(s) selecionada(s)."
                : $"{chosen.Count} pasta(s) selecionada(s) — clique em «Medir tamanhos» para o total.";
    }

    private void UpdateModeHint()
    {
        ModeHint.Text = Mode switch
        {
            BackupMode.Mirror =>
                "Espelho: o destino fica exatamente igual à origem. Ficheiros que apagar no PC também desaparecem da cópia.",
            BackupMode.Snapshot =>
                "Instantâneo: cria uma pasta nova com a data de hoje e mantém as cópias anteriores intactas. Ocupa mais espaço.",
            _ =>
                "Incremental: copia só o que mudou desde a última vez e nunca apaga nada no destino."
        };
    }

    // ---------------- Destino ----------------

    private void LoadDrives()
    {
        var drives = BackupManager.CandidateDestinations();
        DriveCombo.ItemsSource = drives;

        var removable = drives.FirstOrDefault(d => d.Type == DriveType.Removable);
        DriveCombo.SelectedItem = removable ?? drives.FirstOrDefault();

        if (drives.Count == 0)
        {
            DestText.Text = "Nenhuma unidade disponível além do disco do Windows. " +
                            "Ligue um disco externo ou use «Procurar…» para escolher uma pasta.";
            _destination = null;
        }
    }

    private void OnDriveChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveCombo.SelectedItem is not DriveInfoSnapshot drive) return;
        SetDestination(drive.Letter + "\\");
    }

    private void OnPickDestination(object sender, RoutedEventArgs e)
    {
        var folder = Ui.PickFolder("Escolha a pasta de destino da cópia de segurança", _destination);
        if (folder is null) return;

        SetDestination(folder);
    }

    private void SetDestination(string path)
    {
        _destination = path;

        // Guardado para a tarefa agendada «--backup» saber para onde copiar.
        Settings.Current.BackupDestination = path;

        try
        {
            var root = Path.GetPathRoot(path);
            if (root is not null && Native.GetDiskFreeSpaceEx(root, out var free, out var total, out _))
            {
                DestText.Text = $"{path} — {Fmt.Bytes((long)free)} livres de {Fmt.Bytes((long)total)}. " +
                                $"A cópia fica em {BackupManager.RootFolderName}\\{Environment.MachineName}.";
            }
            else
            {
                DestText.Text = path;
            }
        }
        catch
        {
            DestText.Text = path;
        }

        LoadBackups();
    }

    private void LoadBackups()
    {
        _backups.Clear();

        if (_destination is null)
        {
            BackupsEmpty.Visibility = Visibility.Visible;
            return;
        }

        foreach (var entry in BackupManager.ListBackups(_destination)) _backups.Add(entry);

        BackupsEmpty.Visibility = _backups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BackupsEmpty.Text = "Ainda não há cópias do NexusGuard neste destino.";
    }

    // ---------------- Medição ----------------

    private async void OnMeasure(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        SetBusy(true);
        _cts = new CancellationTokenSource();

        try
        {
            foreach (var source in _sources.Where(s => s.Selected))
            {
                _cts.Token.ThrowIfCancellationRequested();
                await BackupManager.MeasureAsync(source, _cts.Token);
                UpdateTotal();
            }
        }
        catch (OperationCanceledException)
        {
            RunStatus.Text = "medição cancelada";
        }
        catch (Exception ex)
        {
            Logger.Error("Backup", "Falha ao medir as pastas", ex);
        }
        finally
        {
            UpdateTotal();
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var folder = Ui.PickFolder("Escolha uma pasta a incluir na cópia");
        if (folder is null) return;

        if (_sources.Any(s => string.Equals(s.Path, folder, StringComparison.OrdinalIgnoreCase)))
        {
            Ui.Inform(this, "Cópia de segurança", "Essa pasta já está na lista.");
            return;
        }

        var item = new BackupSource
        {
            Name = new DirectoryInfo(folder).Name,
            Path = folder,
            Selected = true
        };

        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BackupSource.Selected) or nameof(BackupSource.Size)) UpdateTotal();
        };

        _sources.Add(item);
        UpdateTotal();
    }

    // ---------------- Execução ----------------

    private async void OnRunBackup(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_destination is null)
        {
            Ui.Warn(this, "Cópia de segurança", "Escolha primeiro a unidade ou pasta de destino.");
            return;
        }

        var chosen = _sources.Where(s => s.Selected && s.Exists).ToList();

        if (chosen.Count == 0)
        {
            Ui.Warn(this, "Cópia de segurança", "Selecione pelo menos uma pasta.");
            return;
        }

        var estimate = chosen.Where(s => s.Size > 0).Sum(s => s.Size);
        var problem = BackupManager.ValidateDestination(_destination, chosen, estimate);

        if (problem is not null)
        {
            Ui.Warn(this, "Destino inválido", problem);
            return;
        }

        var modeWarning = Mode == BackupMode.Mirror
            ? "\n\nATENÇÃO — modo espelho: tudo o que estiver no destino e já não exista na origem será APAGADO."
            : "";

        if (!Ui.Confirm(this, "Iniciar cópia de segurança",
                $"Origem: {chosen.Count} pasta(s)" +
                (estimate > 0 ? $" (~{Fmt.Bytes(estimate)})" : "") +
                $"\nDestino: {BackupManager.BuildBackupRoot(_destination, Mode)}" +
                $"\nModo: {Mode}" +
                modeWarning +
                "\n\nNão desligue o disco externo durante a cópia. Continuar?"))
            return;

        Settings.Current.BackupSources = chosen.Select(s => s.Path).ToArray();
        Settings.Current.BackupSkipCloudOnly = SkipCloud.IsChecked == true;

        SetBusy(true);
        ShowConsole(true);
        _cts = new CancellationTokenSource();
        RunStatus.Text = "copiando…";

        try
        {
            var progress = new Progress<string>(text => RunStatus.Text = text);

            var result = await BackupManager.RunAsync(_sources, _destination, Mode,
                SkipCloud.IsChecked == true, line => _sink?.Write(line), progress, _cts.Token);

            RunStatus.Text = result.Message;
            LoadBackups();

            if (result.Success)
                Ui.Inform(this, "Cópia concluída", result.Message);
            else
                Ui.Warn(this, "Cópia com erros", result.Message);
        }
        catch (OperationCanceledException)
        {
            RunStatus.Text = "cópia interrompida";
            _sink?.Write("=== Cópia interrompida pelo utilizador ===");
        }
        catch (Exception ex)
        {
            Logger.Error("Backup", "Falha na cópia de segurança", ex);
            RunStatus.Text = "a cópia falhou — consulte o registo";
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
        RunStatus.Text = "a interromper…";
    }

    // ---------------- Restauro ----------------

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not Button { Tag: BackupEntry entry } || _destination is null) return;

        var backupRoot = Path.Combine(_destination, BackupManager.RootFolderName,
            Environment.MachineName, entry.Name);

        if (!Directory.Exists(backupRoot))
        {
            Ui.Warn(this, "Restaurar", "A pasta desta cópia já não existe no destino.");
            return;
        }

        var target = Ui.PickFolder("Escolha onde colocar os ficheiros restaurados");
        if (target is null) return;

        if (!Ui.Confirm(this, "Restaurar cópia",
                $"Origem: {backupRoot}\nDestino: {target}\n\n" +
                "Ficheiros mais recentes no destino não são substituídos e nada é apagado. Continuar?"))
            return;

        SetBusy(true);
        ShowConsole(true);
        _cts = new CancellationTokenSource();
        RunStatus.Text = "a restaurar…";

        try
        {
            var result = await BackupManager.RestoreAsync(backupRoot, target,
                line => _sink?.Write(line), _cts.Token);

            RunStatus.Text = result.Message;

            if (result.Success) Ui.Inform(this, "Restauro concluído", result.Message);
            else Ui.Warn(this, "Restauro", result.Message);
        }
        catch (OperationCanceledException)
        {
            RunStatus.Text = "restauro interrompido";
        }
        catch (Exception ex)
        {
            Logger.Error("Backup", "Falha no restauro", ex);
            RunStatus.Text = "o restauro falhou";
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    // ---------------- Proteção do sistema ----------------

    private async void OnCreateRestorePoint(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!Fmt.IsAdmin)
        {
            Ui.Warn(this, "Privilégios necessários",
                "Criar um ponto de restauro exige administrador. Use «Reiniciar como administrador» na barra lateral.");
            return;
        }

        SetBusy(true);
        ShowConsole(true);
        _cts = new CancellationTokenSource();
        RunStatus.Text = "a criar ponto de restauro…";

        try
        {
            var (ok, message) = await BackupManager.CreateRestorePointAsync(
                $"NexusGuard {DateTime.Now:yyyy-MM-dd HH:mm}", line => _sink?.Write(line), _cts.Token);

            RunStatus.Text = message;

            if (ok) Ui.Inform(this, "Ponto de restauro", message);
            else Ui.Warn(this, "Ponto de restauro", message);
        }
        catch (Exception ex)
        {
            Logger.Error("Backup", "Falha ao criar o ponto de restauro", ex);
            RunStatus.Text = "não foi possível criar o ponto de restauro";
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async void OnSystemImage(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!Fmt.IsAdmin)
        {
            Ui.Warn(this, "Privilégios necessários",
                "A imagem do sistema exige administrador. Use «Reiniciar como administrador» na barra lateral.");
            return;
        }

        if (_destination is null)
        {
            Ui.Warn(this, "Imagem do sistema", "Escolha primeiro a unidade de destino.");
            return;
        }

        var letter = Path.GetPathRoot(_destination)?.TrimEnd('\\', ':');

        if (string.IsNullOrEmpty(letter))
        {
            Ui.Warn(this, "Imagem do sistema", "O destino tem de ser uma unidade local (ex.: E:).");
            return;
        }

        if (!Ui.Confirm(this, "Imagem completa do sistema",
                $"Será criada uma imagem completa do Windows em {letter}:\n\n" +
                "• pode demorar várias horas\n" +
                "• a unidade tem de estar formatada em NTFS\n" +
                "• o conteúdo da pasta WindowsImageBackup será substituído\n\n" +
                "Continuar?"))
            return;

        SetBusy(true);
        ShowConsole(true);
        _cts = new CancellationTokenSource();
        RunStatus.Text = "a criar imagem do sistema…";

        try
        {
            var (ok, message) = await BackupManager.CreateSystemImageAsync(letter,
                line => _sink?.Write(line), _cts.Token);

            RunStatus.Text = message;

            if (ok) Ui.Inform(this, "Imagem do sistema", message);
            else Ui.Warn(this, "Imagem do sistema", message);
        }
        catch (OperationCanceledException)
        {
            RunStatus.Text = "imagem interrompida";
        }
        catch (Exception ex)
        {
            Logger.Error("Backup", "Falha ao criar a imagem do sistema", ex);
            RunStatus.Text = "a imagem do sistema falhou";
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnOpenSystemProtection(object sender, RoutedEventArgs e) => BackupManager.OpenSystemProtection();

    private void OnToggleConsole(object sender, RoutedEventArgs e) =>
        ShowConsole(Output.Visibility != Visibility.Visible);

    private void ShowConsole(bool show)
    {
        Output.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ToggleConsole.Content = show ? "Ocultar saída" : "Ver saída";
    }
}
