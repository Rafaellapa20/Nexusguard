using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard.Views;

public partial class OverviewView : UserControl
{
    private readonly SystemMonitor _mon = SystemMonitor.Instance;
    private bool _scanning;
    private bool _loadedOnce;

    public OverviewView()
    {
        InitializeComponent();

        DataContext = _mon;
        HeroMachine.Text = $"{_mon.MachineSummary} · {_mon.CpuName}";
        UptimeText.Text = _mon.Uptime;

        _mon.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemMonitor.Uptime)) UptimeText.Text = _mon.Uptime;
        };

        Loaded += async (_, _) =>
        {
            RefreshActivity();
            RefreshStats();

            if (_loadedOnce) return;
            _loadedOnce = true;

            // Na primeira execução o onboarding decide quando analisar.
            if (Settings.Current.FirstRunCompleted) await ScanAsync();
        };
    }

    private SystemSnapshot Snapshot => MainWindow.Instance?.Snapshot ?? new SystemSnapshot();

    // ---------------- Análise completa ----------------

    public async Task ScanAsync()
    {
        if (_scanning) return;

        _scanning = true;
        ScanButton.IsEnabled = false;
        FixAllButton.IsEnabled = false;
        ScanProgressPanel.Visibility = Visibility.Visible;

        var snapshot = Snapshot;

        try
        {
            void Step(string text, double percent)
            {
                ScanStep.Text = text;
                ScanProgress.Value = percent;
            }

            Step("A verificar a proteção antivírus…", 8);
            var defender = await SecurityManager.GetStatusAsync();
            var threats = await SecurityManager.GetThreatsAsync();
            snapshot.Threats = threats.Count(t => t.ThreatStatusID is 1 or 6);

            Step("A procurar atualizações de programas…", 26);
            var apps = AppUpdater.IsAvailable ? await AppUpdater.ListUpgradesAsync() : new List<AppUpgrade>();
            snapshot.OutdatedApps = apps.Count;

            Step("Medindo o espaço recuperável…", 48);
            long recoverable = 0;

            foreach (var target in DiskCleaner.BuildTargets()
                         .Where(t => t.Risk == CleanRisk.Safe && t.CustomSize is null)
                         .Where(t => !t.NeedsAdmin || Fmt.IsAdmin))
            {
                await DiskCleaner.ScanAsync(target);
                if (target.Size > 0) recoverable += target.Size;
            }

            recoverable += DiskCleaner.RecycleBinSize();
            snapshot.RecoverableBytes = recoverable;

            Step("A verificar os programas de inicialização…", 70);
            snapshot.StartupItems = StartupManager.Load().Count(i => i.Enabled);

            Step("A procurar drivers pendentes…", 84);
            var (drivers, _) = await UpdateAgent.SearchAsync(drivers: true);
            snapshot.PendingDrivers = drivers.Count;

            Step("A concluir…", 96);
            snapshot.LastScan = DateTime.Now;

            // Uma analise concluida e o unico momento em que a pontuacao significa alguma
            // coisa. E guardada para o relatorio poder comparar com medicoes reais.
            ScoreLog.Record(snapshot.Score);

            History.Add("Visão geral", "Análise completa do PC",
                $"saúde {snapshot.Score}", UndoKind.None);

            RefreshStats();
            RefreshActivity();

            ScanStep.Text = defender.Available && defender.RealTimeProtectionEnabled
                ? "Análise concluída. Proteção em tempo real ativa."
                : "Análise concluída. Atenção: a proteção em tempo real não está ativa.";

            ScanProgress.Value = 100;
        }
        catch (Exception ex)
        {
            Logger.Error("Visão geral", "A análise falhou", ex);
            ScanStep.Text = "A análise falhou. Consulte o registo técnico.";
        }
        finally
        {
            _scanning = false;
            ScanButton.IsEnabled = true;
            FixAllButton.IsEnabled = true;
        }
    }

    private async void OnScan(object sender, RoutedEventArgs e) => await ScanAsync();

    // ---------------- Cartões ----------------

    private void RefreshStats()
    {
        var s = Snapshot;

        StatApps.Text = s.LastScan is null ? "—" : s.OutdatedApps.ToString(Fmt.Pt);
        StatDrivers.Text = s.LastScan is null ? "—" : s.PendingDrivers.ToString(Fmt.Pt);
        StatSpace.Text = s.LastScan is null ? "—" : s.RecoverableText;
        StatRegistry.Text = s.RegistryIssues == 0 && s.LastScan is null ? "—" : s.RegistryIssues.ToString(Fmt.Pt);
        StatThreats.Text = s.LastScan is null ? "—" : s.Threats.ToString(Fmt.Pt);
        StatStartup.Text = s.LastScan is null ? "—" : s.StartupItems.ToString(Fmt.Pt);

        Paint(DotApps, s.OutdatedApps == 0, s.OutdatedApps >= 10);
        Paint(DotDrivers, s.PendingDrivers == 0, s.PendingDrivers >= 3);
        Paint(DotSpace, s.RecoverableBytes < 1L * 1024 * 1024 * 1024,
            s.RecoverableBytes > 10L * 1024 * 1024 * 1024);
        Paint(DotRegistry, s.RegistryIssues == 0, s.RegistryIssues > 300);
        Paint(DotThreats, s.Threats == 0, s.Threats > 0);
        Paint(DotStartup, s.StartupItems <= 6, s.StartupItems > 12);

        HeroSummary.Text = s.LastScan is null
            ? "Ainda não foi feita uma análise. São cerca de 40 segundos e nada é alterado."
            : s.AttentionCount == 0
                ? $"Última análise {s.LastScanText} · está tudo em ordem."
                : $"Última análise {s.LastScanText} · {s.AttentionCount} item(ns) precisam de atenção.";

        MainWindow.Instance?.RefreshScore();
    }

    private void Paint(Border dot, bool good, bool bad)
    {
        var key = Snapshot.LastScan is null ? "B.TextMuted" : bad ? "B.Danger" : good ? "B.Ok" : "B.Warn";
        dot.Background = (Brush)FindResource(key);
    }

    private void RefreshActivity()
    {
        History.EnsureLoaded();

        var recent = History.Entries.Take(5).ToList();
        ActivityList.ItemsSource = recent;
        ActivityEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnStatCard(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key }) MainWindow.Instance?.Navigate(key);
    }

    // ---------------- Corrigir tudo ----------------

    private async void OnFixAll(object sender, RoutedEventArgs e)
    {
        if (_scanning) return;

        var s = Snapshot;

        if (s.LastScan is null)
        {
            Ui.Inform(this, "Corrigir tudo", "Faça primeiro uma análise para saber o que há a corrigir.");
            return;
        }

        if (!Ui.Confirm(this, "Corrigir tudo",
                "Será feito o seguinte:\n\n" +
                "• limpar os ficheiros temporários e caches seguras (para a quarentena)\n" +
                "• esvaziar a reciclagem\n" +
                "• liberar memória\n\n" +
                "Documentos, fotos e perfis de navegador nunca são tocados.\n\n" +
                "As atualizações de programas e drivers ficam para si decidir, nas respetivas páginas. Continuar?"))
            return;

        _scanning = true;
        FixAllButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        ScanProgressPanel.Visibility = Visibility.Visible;
        ScanProgress.Value = 0;

        long freed = 0;

        try
        {
            var targets = DiskCleaner.BuildTargets()
                .Where(t => t.Risk != CleanRisk.Advanced)
                .Where(t => !t.NeedsAdmin || Fmt.IsAdmin)
                .ToList();

            var step = 0;

            foreach (var target in targets)
            {
                ScanStep.Text = $"A limpar: {target.Name}…";
                ScanProgress.Value = ++step * 80.0 / targets.Count;

                await DiskCleaner.ScanAsync(target);
                var outcome = await DiskCleaner.CleanAsync(target);
                freed += outcome.BytesFreed;
            }

            ScanStep.Text = "Liberando memória…";
            ScanProgress.Value = 92;
            var memory = await MemoryOptimizer.OptimizeAsync(aggressive: true);

            ScanProgress.Value = 100;
            ScanStep.Text = $"Concluído: {Fmt.Bytes(freed)} em disco e {Fmt.Bytes(memory.FreedBytes)} de memória.";

            _mon.RefreshDrives();
            Snapshot.RecoverableBytes = 0;

            RefreshStats();
            RefreshActivity();

            Ui.Inform(this, "Tudo corrigido",
                $"Disco: {Fmt.Bytes(freed)} liberados.\n" +
                $"Memória: {Fmt.Bytes(memory.FreedBytes)} liberados.\n\n" +
                (Settings.Current.UseQuarantine
                    ? $"Os ficheiros ficaram em quarentena durante {Settings.Current.QuarantineDays} dias."
                    : "Os ficheiros foram apagados de forma definitiva."));
        }
        catch (Exception ex)
        {
            Logger.Error("Visão geral", "A correção automática falhou", ex);
            ScanStep.Text = "A correção falhou. Consulte o registo técnico.";
        }
        finally
        {
            _scanning = false;
            FixAllButton.IsEnabled = true;
            ScanButton.IsEnabled = true;
        }
    }
}
