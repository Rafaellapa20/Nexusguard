using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using NexusGuard.Core;
using NexusGuard.Modules;
using NexusGuard.Views;

namespace NexusGuard;

public sealed record NavEntry(string Key, string Title, string Icon, Func<UserControl> Factory);

public sealed record NavGroup(string Title, NavEntry[] Items);

public partial class MainWindow : Window
{
    public static MainWindow? Instance { get; private set; }

    /// <summary>Estado partilhado entre as páginas: alimenta a pontuação e os emblemas.</summary>
    public SystemSnapshot Snapshot { get; } = new();

    private readonly Dictionary<string, (NavEntry entry, Lazy<UserControl> view)> _pages = new();
    private readonly Dictionary<string, ToggleButton> _buttons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _badges = new(StringComparer.OrdinalIgnoreCase);
    private string _current = "dashboard";
    private bool _warnedAboutTray;

    private static readonly NavGroup[] Groups =
    {
        new("Início", new[]
        {
            new NavEntry("dashboard", "Visão geral", "I.Dashboard", () => new OverviewView())
        }),
        new("Atualizar", new[]
        {
            new NavEntry("apps", "Aplicativos", "I.Apps", () => new AppsView()),
            new NavEntry("install", "Instalar em lote", "I.Grid", () => new InstallView()),
            new NavEntry("drivers", "Drivers", "I.Driver", () => new DriversView()),
            new NavEntry("uninstall", "Desinstalar", "I.Trash", () => new UninstallView())
        }),
        new("Limpar e otimizar", new[]
        {
            new NavEntry("cleanup", "Limpeza", "I.Clean", () => new CleanupView()),
            new NavEntry("registry", "Registro", "I.Log", () => new RegistryView()),
            new NavEntry("performance", "Performance", "I.Performance", () => new PerformanceView()),
            new NavEntry("startup", "Inicialização", "I.Play", () => new StartupView())
        }),
        new("Proteger", new[]
        {
            new NavEntry("hardware", "Hardware", "I.Memory", () => new HardwareView()),
            new NavEntry("security", "Segurança", "I.Shield", () => new SecurityView()),
            new NavEntry("privacy", "Privacidade", "I.Admin", () => new PrivacyView()),
            new NavEntry("backup", "Backup", "I.Backup", () => new BackupView())
        }),
        new("Sistema", new[]
        {
            new NavEntry("schedule", "Agendamento", "I.Refresh", () => new ScheduleView()),
            new NavEntry("history", "Histórico", "I.Restore", () => new HistoryView()),
            new NavEntry("settings", "Configurações", "I.Folder", () => new SettingsView())
        })
    };

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;

        SystemMonitor.Instance.Start();
        BuildNav();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"v{version?.ToString(3) ?? "1.0.0"} · {Fmt.WindowsName}";
        MachineText.Text = Environment.MachineName;

        AdminBanner.Visibility = Fmt.IsAdmin ? Visibility.Collapsed : Visibility.Visible;
        AdminOk.Visibility = Fmt.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        DryRunPill.Visibility = Settings.Current.DryRun ? Visibility.Visible : Visibility.Collapsed;

        Settings.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Settings.DryRun))
                DryRunPill.Visibility = Settings.Current.DryRun ? Visibility.Visible : Visibility.Collapsed;
        };

        Snapshot.PropertyChanged += (_, _) => RefreshScore();

        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        StateChanged += (_, _) => UpdateChromeForState();

        Loaded += OnLoaded;

        // O onboarding só abre depois de a janela estar mesmo desenhada: chamar ShowDialog de
        // dentro do Loaded corre um ciclo de mensagens aninhado antes de o Show() terminar, e a
        // janela principal ficaria invisível por trás do assistente.
        ContentRendered += OnContentRendered;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Navigate("dashboard");
        RefreshScore();

        // A quarentena expira sozinha; não vale a pena esperar pelo resultado.
        _ = Task.Run(() => Quarantine.PurgeExpired());
    }

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;

        if (!Settings.Current.FirstRunCompleted) await RunOnboardingAsync();

        await CheckForUpdatesQuietlyAsync();
    }

    /// <summary>
    /// Consulta ao arrancar. Havendo versao nova, mostra-a numa janela por cima da aplicacao, que
    /// e o que quem abre o programa ve; arrancado para a bandeja, avisa pelo balao. Em qualquer dos
    /// casos marca Configuracoes. Nada e baixado nem instalado sem o utilizador pedir.
    /// </summary>
    private async Task CheckForUpdatesQuietlyAsync()
    {
        if (!Settings.Current.CheckUpdatesOnStart || !Updater.IsConfigured) return;

        Updater.CleanOldDownloads();

        try
        {
            var check = await Updater.CheckAsync();

            if (check.State != UpdateState.Available || check.Update is null) return;

            SetBadge("settings", 1, "ok");

            // Dispensada de proposito para esta versao: fica so o ponto em Configuracoes.
            if (string.Equals(Settings.Current.SkippedUpdateVersion, check.Update.Version.ToString(),
                    StringComparison.Ordinal))
                return;

            // Com a janela a tapar o ecra, uma caixa modal e o que se ve; arrancado para a bandeja,
            // seria uma janela a saltar sem ninguem ter aberto nada, e ai o balao e o correto.
            if (IsVisible && WindowState != WindowState.Minimized)
            {
                new UpdateWindow(check.Update) { Owner = this }.ShowDialog();
                return;
            }

            App.Tray?.Notify("NexusGuard",
                $"Versao {check.Update.Version} disponivel. Abra Configuracoes para instalar.");
        }
        catch (Exception ex)
        {
            Logger.Warn("Atualizacao", $"Verificacao de arranque falhou: {ex.Message}");
        }
    }

    private async Task RunOnboardingAsync()
    {
        var window = new OnboardingWindow { Owner = this };
        var result = window.ShowDialog();

        DryRunPill.Visibility = Settings.Current.DryRun ? Visibility.Visible : Visibility.Collapsed;

        if (result == true && window.StartScan && Host.Content is OverviewView overview)
            await overview.ScanAsync();
    }

    // ---------------- Navegação ----------------

    private void BuildNav()
    {
        foreach (var group in Groups)
        {
            NavHost.Children.Add(new TextBlock
            {
                Text = group.Title.ToUpperInvariant(),
                Style = (Style)FindResource("T.Group")
            });

            foreach (var entry in group.Items)
            {
                _pages[entry.Key] = (entry, new Lazy<UserControl>(entry.Factory));
                NavHost.Children.Add(BuildNavButton(entry));
            }
        }
    }

    private UIElement BuildNavButton(NavEntry entry)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Style = (Style)FindResource("Icon.Small"),
            Data = (Geometry)FindResource(entry.Icon),
            Width = 16,
            Height = 16
        };

        var label = new TextBlock
        {
            Text = entry.Title,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(11, 0, 0, 0)
        };

        var badgeText = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("F.Mono"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("B.Warn")
        };

        var badge = new Border
        {
            Style = (Style)FindResource("Pill"),
            Background = (Brush)FindResource("B.WarnSoft"),
            Padding = new Thickness(7, 2, 7, 2),
            Child = badgeText,
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        _badges[entry.Key] = badge;

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(label, 1);
        Grid.SetColumn(badge, 2);
        content.Children.Add(icon);
        content.Children.Add(label);
        content.Children.Add(badge);

        var button = new ToggleButton
        {
            Content = content,
            Template = (ControlTemplate)FindResource("NavItemTemplate"),
            Tag = entry.Key,
            Cursor = System.Windows.Input.Cursors.Hand,
            Foreground = (Brush)FindResource("B.TextDim"),
            Margin = new Thickness(0, 1, 0, 1)
        };

        button.Checked += (_, _) => Navigate(entry.Key);
        button.Unchecked += (_, _) => { if (_current == entry.Key) button.IsChecked = true; };

        _buttons[entry.Key] = button;
        return button;
    }

    public void Navigate(string key)
    {
        if (!_pages.TryGetValue(key, out var page)) return;

        _current = key;
        PageTitle.Text = page.entry.Title;
        Host.Content = page.view.Value;

        foreach (var (k, button) in _buttons)
        {
            var active = k.Equals(key, StringComparison.OrdinalIgnoreCase);
            button.IsChecked = active;
            button.Foreground = (Brush)FindResource(active ? "B.Text" : "B.TextDim");
        }
    }

    /// <summary>Emblema numérico à direita de um item da barra lateral.</summary>
    public void SetBadge(string key, int count, string severity = "warn")
    {
        if (!_badges.TryGetValue(key, out var badge)) return;

        if (count <= 0)
        {
            badge.Visibility = Visibility.Collapsed;
            return;
        }

        var (soft, strong) = severity switch
        {
            "ok" => ("B.OkSoft", "B.Ok"),
            "bad" => ("B.DangerSoft", "B.Danger"),
            _ => ("B.WarnSoft", "B.Warn")
        };

        badge.Background = (Brush)FindResource(soft);

        if (badge.Child is TextBlock text)
        {
            text.Text = count > 99 ? "99+" : count.ToString(Fmt.Pt);
            text.Foreground = (Brush)FindResource(strong);
        }

        badge.Visibility = Visibility.Visible;
    }

    // ---------------- Pontuação de saúde ----------------

    public void RefreshScore()
    {
        var score = Snapshot.Score;

        ScoreValue.Text = Snapshot.LastScan is null ? "—" : Snapshot.ScoreText;
        ScoreLabel.Text = Snapshot.LastScan is null
            ? "Analise o PC para calcular."
            : $"{Snapshot.ScoreLabel} · {Snapshot.LastScanText}";

        var brush = (Brush)FindResource(score >= 80 ? "B.Ok" : score >= 60 ? "B.Warn" : "B.Danger");
        ScoreValue.Foreground = brush;
        ScoreBar.Background = brush;

        // A barra ocupa a largura do cartão proporcionalmente à pontuação. Na primeira passagem
        // o contentor ainda não tem largura medida, por isso o cálculo repete-se quando ela chega.
        if (ScoreBar.Parent is Border track)
        {
            void Size() => ScoreBar.Width = Snapshot.LastScan is null
                ? 0
                : Math.Max(0, track.ActualWidth * score / 100.0);

            Size();

            if (track.ActualWidth <= 0)
                track.SizeChanged += (_, _) => Size();
        }

        SetBadge("apps", Snapshot.OutdatedApps);
        SetBadge("drivers", Snapshot.PendingDrivers);
        SetBadge("security", Snapshot.Threats, "bad");
        SetBadge("registry", Snapshot.RegistryIssues > 0 ? Snapshot.RegistryIssues : 0);
    }

    // ---------------- Janela ----------------

    private void ApplyDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var dark = 1;
            Native.DwmSetWindowAttribute(handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch
        {
            // Versões antigas do Windows ignoram o atributo.
        }
    }

    private void UpdateChromeForState()
    {
        var maximized = WindowState == WindowState.Maximized;

        RootBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        MaxIcon.Data = maximized
            ? Geometry.Parse("M2.5,0.5 H10.5 V8.5 M0.5,2.5 H8.5 V10.5 H0.5 Z")
            : Geometry.Parse("M0.5,0.5 H10.5 V10.5 H0.5 Z");
        MaxButton.ToolTip = maximized ? "Restaurar" : "Maximizar";
    }

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (!App.RestartElevated())
        {
            MessageBox.Show(this,
                "Não foi possível reiniciar com privilégios de administrador. " +
                "Feche o aplicativo e abra-a com o botão direito → «Executar como administrador».",
                "NexusGuard", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Com a bandeja ligada, fechar a janela apenas a esconde — a aplicação continua a
        // correr para as tarefas agendadas e para o acesso rápido pelo ícone.
        if (Application.Current is App app && app.ShouldHideOnClose)
        {
            e.Cancel = true;
            Hide();

            if (!_warnedAboutTray)
            {
                _warnedAboutTray = true;
                app.NotifyHiddenToTray();
            }

            return;
        }

        base.OnClosing(e);
    }

    /// <summary>Permite ao menu da bandeja disparar a análise sem duplicar a lógica.</summary>
    public async Task ScanFromTrayAsync()
    {
        Navigate("dashboard");

        if (Host.Content is Views.OverviewView overview) await overview.ScanAsync();
    }
}
