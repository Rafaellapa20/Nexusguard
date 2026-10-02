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
            Ui.Warn(this, "Configurações", $"Não foi possível alterar o arranque automático: {ex.Message}");
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

    private void OnRestorePoint(object sender, RoutedEventArgs e)
    {
        if (!_loading) Settings.Current.CreateRestorePoint = OptRestore.IsChecked == true;
    }

    private void OnQuarantine(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var enable = OptQuarantine.IsChecked == true;

        if (!enable && !Ui.Confirm(this, "Desligar a quarentena",
                "Sem quarentena, a limpeza apaga os arquivos de forma definitiva e deixa de haver «Desfazer».\n\n" +
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
}
