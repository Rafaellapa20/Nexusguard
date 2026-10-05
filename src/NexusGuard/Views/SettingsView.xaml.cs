using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NexusGuard.Core;

namespace NexusGuard.Views;

public partial class SettingsView : UserControl
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "NexusGuard";

    private bool _loading = true;
    private NexusGuard.Modules.UpdateInfo? _pendingUpdate;
    private CancellationTokenSource? _updateCts;

    public SettingsView()
    {
        InitializeComponent();

        QuarantineDays.ItemsSource = new[] { 3, 7, 14, 30, 60, 90 };
        BackupDays.ItemsSource = new[] { 7, 14, 30, 60, 90, 180, 365 };

        Loaded += (_, _) =>
        {
            _loading = true;

            var s = Settings.Current;

            OptStartup.IsChecked = s.StartWithWindows;
            OptTray.IsChecked = s.MinimizeToTray;
            OptNotify.IsChecked = s.Notifications;
            OptRestore.IsChecked = s.CreateRestorePoint;
            OptQuarantine.IsChecked = s.UseQuarantine;
            OptConfirm.IsChecked = s.ConfirmEachChange;
            OptWhql.IsChecked = s.WhqlDriversOnly;
            OptDryRun.IsChecked = s.DryRun;
            OptBeta.IsChecked = s.BetaChannel;
            OptTelemetry.IsChecked = s.Telemetry;

            QuarantineDays.SelectedItem = s.QuarantineDays;
            BackupDays.SelectedItem = s.BackupRetentionDays;

            PathsText.Text = $"Dados: {Paths.SharedRoot}    ·    Configuração: {Paths.UserRoot}";

            OptCheckUpdates.IsChecked = s.CheckUpdatesOnStart;
            VersionText.Text = "v" + NexusGuard.Modules.Updater.CurrentVersion;
            ReleasesButton.IsEnabled = NexusGuard.Modules.Updater.IsConfigured;

            if (!NexusGuard.Modules.Updater.IsConfigured)
            {
                CheckUpdateButton.IsEnabled = false;
                UpdateStatus.Text = "Esta compilação não tem repositório de atualizações configurado.";
            }

            _loading = false;
        };
    }

    private void OnStartWithWindows(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var enable = OptStartup.IsChecked == true;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

            if (key is null) throw new InvalidOperationException("chave Run indisponível");

            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe)) throw new InvalidOperationException("caminho do executável desconhecido");
                key.SetValue(RunValue, $"\"{exe}\" --tray");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }

            Settings.Current.StartWithWindows = enable;
        }
        catch (Exception ex)
        {
            OptStartup.IsChecked = !enable;
            Ui.Warn(this, "Definições", $"Não foi possível alterar o arranque automático: {ex.Message}");
        }
    }

    private void OnTray(object sender, RoutedEventArgs e)
    {
        if (!_loading) Settings.Current.MinimizeToTray = OptTray.IsChecked == true;
    }

    private void OnNotify(object sender, RoutedEventArgs e)
    {
        if (!_loading) Settings.Current.Notifications = OptNotify.IsChecked == true;
    }

    private void OnTestNotification(object sender, RoutedEventArgs e)
    {
        var tray = App.Tray;

        if (tray is not { IsVisible: true })
        {
            Ui.Warn(this, "Notificações",
                "O ícone da bandeja não está disponível nesta sessão, por isso não há como notificar.");
            return;
        }

        tray.Notify("NexusGuard",
            "É assim que ficam os avisos das tarefas agendadas.");
    }

    private void OnRestorePoint(object sender, RoutedEventArgs e)
    {
        if (!_loading) Settings.Current.CreateRestorePoint = OptRestore.IsChecked == true;
    }

    private void OnQuarantine(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var enable = OptQuarantine.IsChecked == true;

        if (!enable && !Ui.Confirm(this, "Desligar a quarentena",
                "Sem quarentena, a limpeza apaga os ficheiros de forma definitiva e deixa de haver «Desfazer».\n\n" +
                "Tem certeza?"))
        {
            OptQuarantine.IsChecked = true;
            return;
        }

        Settings.Current.UseQuarantine = enable;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (!_loading) Settings.Current.ConfirmEachChange = OptConfirm.IsChecked == true;
    }

    private void OnWhql(object sender, RoutedEventArgs e)
    {
        if (!_loading) Settings.Current.WhqlDriversOnly = OptWhql.IsChecked == true;
    }

    private void OnDryRun(object sender, RoutedEventArgs e)
    {
        if (!_loading) Settings.Current.DryRun = OptDryRun.IsChecked == true;
    }

    private void OnBeta(object sender, RoutedEventArgs e)
    {
        if (!_loading) Settings.Current.BetaChannel = OptBeta.IsChecked == true;
    }

    private void OnTelemetry(object sender, RoutedEventArgs e)
    {
        if (!_loading) Settings.Current.Telemetry = OptTelemetry.IsChecked == true;
    }

    private void OnQuarantineDays(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && QuarantineDays.SelectedItem is int days) Settings.Current.QuarantineDays = days;
    }

    private void OnBackupDays(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && BackupDays.SelectedItem is int days) Settings.Current.BackupRetentionDays = days;
    }

    private void OnOpenData(object sender, RoutedEventArgs e) => Shell.OpenExternal(Paths.SharedRoot);

    private void OnOpenLogs(object sender, RoutedEventArgs e) => Shell.OpenExternal(Paths.Logs);

    /// <summary>
    /// Escreve os avisos de licenca dos componentes de terceiros e abre-os. O texto viaja embutido
    /// no executavel: as licencas MPL-2.0 obrigam a disponibilizar estes avisos a quem recebe o
    /// binario, e nao apenas a quem tem acesso ao repositorio.
    /// </summary>
    private void OnThirdParty(object sender, RoutedEventArgs e)
    {
        try
        {
            using var stream = typeof(SettingsView).Assembly
                .GetManifestResourceStream("NexusGuard.Terceiros.md");

            if (stream is null)
            {
                Ui.Warn(this, "Componentes de terceiros",
                    "Os avisos de licenca nao foram encontrados nesta compilacao.");
                return;
            }

            using var reader = new StreamReader(stream);
            var path = Path.Combine(Paths.SharedRoot, "Componentes-de-terceiros.txt");
            File.WriteAllText(path, reader.ReadToEnd(), new UTF8Encoding(false));
            Shell.OpenExternal(path);
        }
        catch (Exception ex)
        {
            Logger.Error("Configuracoes", "nao foi possivel abrir os avisos de terceiros", ex);
            Ui.Warn(this, "Componentes de terceiros", "Nao foi possivel abrir os avisos de licenca.");
        }
    }

    private void OnRerunOnboarding(object sender, RoutedEventArgs e)
    {
        var window = new OnboardingWindow { Owner = Window.GetWindow(this) };
        window.ShowDialog();

        _loading = true;
        OptRestore.IsChecked = Settings.Current.CreateRestorePoint;
        OptQuarantine.IsChecked = Settings.Current.UseQuarantine;
        OptConfirm.IsChecked = Settings.Current.ConfirmEachChange;
        OptWhql.IsChecked = Settings.Current.WhqlDriversOnly;
        OptTelemetry.IsChecked = Settings.Current.Telemetry;
        _loading = false;
    }

    // ---------------- Atualizações ----------------

    private void OnCheckUpdatesToggle(object sender, RoutedEventArgs e)
    {
        if (!_loading) Settings.Current.CheckUpdatesOnStart = OptCheckUpdates.IsChecked == true;
    }

    private void OnOpenReleases(object sender, RoutedEventArgs e) =>
        Shell.OpenExternal(NexusGuard.Modules.Updater.ReleasesUrl);

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        NotesBox.Visibility = Visibility.Collapsed;
        UpdateStatus.Text = "A procurar…";

        _updateCts = new CancellationTokenSource();

        try
        {
            var check = await NexusGuard.Modules.Updater.CheckAsync(_updateCts.Token);

            UpdateStatus.Text = check.Message;
            _pendingUpdate = check.Update;

            if (check.State != NexusGuard.Modules.UpdateState.Available || check.Update is null) return;

            InstallUpdateButton.Content = $"Transferir e instalar {check.Update.Version}";
            InstallUpdateButton.Visibility = Visibility.Visible;

            if (!string.IsNullOrWhiteSpace(check.Update.Notes))
            {
                NotesText.Text = check.Update.Notes;
                NotesBox.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatus.Text = "Procura cancelada.";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
            _updateCts?.Dispose();
            _updateCts = null;
        }
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is not { } update) return;

        if (!Ui.Confirm(this, "Atualizar o NexusGuard",
                $"Versão {update.Version} · {update.SizeText}\n\n" +
                "O ficheiro é transferido do GitHub, conferido contra o hash publicado e instalado por cima " +
                "desta versão.\n\n" +
                "O NexusGuard fecha-se para o instalador poder substituir o executável. Continuar?"))
            return;

        InstallUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;

        _updateCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<double>(p => UpdateProgress.Value = p);

            var setup = await NexusGuard.Modules.Updater.DownloadAsync(
                update, progress, line => UpdateStatus.Text = line, _updateCts.Token);

            if (setup is null)
            {
                Ui.Warn(this, "Atualização",
                    "O download falhou ou o ficheiro não corresponde ao hash publicado. Nada foi instalado.");
                return;
            }

            if (!NexusGuard.Modules.Updater.Install(setup, out var message))
            {
                UpdateStatus.Text = message;
                Ui.Warn(this, "Atualização", message);
                return;
            }

            // O instalador não consegue substituir o executável com ele em uso.
            if (Application.Current is App app) app.ExitApplication();
        }
        catch (OperationCanceledException)
        {
            UpdateStatus.Text = "Download cancelado.";
        }
        finally
        {
            UpdateProgress.Visibility = Visibility.Collapsed;
            InstallUpdateButton.IsEnabled = true;
            CheckUpdateButton.IsEnabled = true;
            _updateCts?.Dispose();
            _updateCts = null;
        }
    }
}
