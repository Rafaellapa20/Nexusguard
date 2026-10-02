using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard;

public partial class App : Application
{
    private TrayIcon? _tray;
    private ContextMenu? _trayMenu;
    private bool _exiting;

    /// <summary>Ícone da bandeja, quando existe. As vistas usam-no para notificar.</summary>
    public static TrayIcon? Tray => (Current as App)?._tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Datas e números em português em toda a interface.
        var culture = Fmt.Pt;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;

        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Logger.Error("App", "Exceção não tratada", ex);
        };

        base.OnStartup(e);

        if (e.Args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a == "/?"))
        {
            MessageBox.Show(Cli.Usage, "NexusGuard", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        var job = Cli.ParseJob(e.Args);

        if (job != JobKind.None)
        {
            RunHeadless(job);
            return;
        }

        Logger.Info("App", $"NexusGuard iniciado {(Fmt.IsAdmin ? "como administrador" : "sem elevação")}.");
        SystemMonitor.Instance.Start();

        // A janela só fecha a aplicação quando o utilizador o pede — com a bandeja ligada,
        // fechar a janela apenas a esconde.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        CreateTray();

        var window = new MainWindow();
        MainWindow = window;

        if (Cli.StartInTray(e.Args) && _tray is { IsVisible: true })
        {
            Logger.Info("App", "Arrancou minimizado na bandeja.");
            _tray.Notify("NexusGuard", "A correr em segundo plano. Clique no ícone para abrir.");
        }
        else
        {
            window.Show();
        }
    }

    // ---------------- Execução sem interface ----------------

    /// <summary>
    /// Modo usado pelas tarefas agendadas: nenhuma janela, o trabalho corre, notifica e sai com
    /// código 0 ou 1 para o Agendador de Tarefas poder registar o resultado.
    /// </summary>
    private async void RunHeadless(JobKind job)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Logger.Info("App", $"Modo sem interface: {Cli.Describe(job)}.");

        var result = await Jobs.RunAsync(job);

        if (Settings.Current.Notifications)
        {
            using var tray = new TrayIcon("NexusGuard");
            tray.Notify(result.Title, result.Message, result.Level);

            // O balão precisa de um momento com bombagem de mensagens para chegar a aparecer.
            await Task.Delay(TimeSpan.FromSeconds(6));
        }

        Shutdown(result.Success ? 0 : 1);
    }

    // ---------------- Bandeja ----------------

    private void CreateTray()
    {
        try
        {
            _tray = new TrayIcon("NexusGuard — manutenção do Windows");
            _tray.Activated += (_, _) => ShowMainWindow();
            _tray.ContextRequested += (_, _) => ShowTrayMenu();
        }
        catch (Exception ex)
        {
            Logger.Warn("Bandeja", $"Sem ícone na bandeja: {ex.Message}");
            _tray = null;
        }
    }

    private void ShowTrayMenu()
    {
        _trayMenu ??= BuildTrayMenu();

        // Sem isto o menu fica aberto quando o utilizador clica fora dele.
        if (_tray is not null) Native.SetForegroundWindow(_tray.Handle);

        _trayMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _trayMenu.IsOpen = true;
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "Abrir o NexusGuard", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => ShowMainWindow();

        var scan = new MenuItem { Header = "Analisar o PC agora" };
        scan.Click += async (_, _) =>
        {
            ShowMainWindow();
            if (MainWindow is MainWindow main) await main.ScanFromTrayAsync();
        };

        var exit = new MenuItem { Header = "Sair" };
        exit.Click += (_, _) => ExitApplication();

        menu.Items.Add(open);
        menu.Items.Add(scan);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        return menu;
    }

    public void ShowMainWindow()
    {
        if (MainWindow is null) return;

        MainWindow.Show();

        if (MainWindow.WindowState == WindowState.Minimized)
            MainWindow.WindowState = WindowState.Normal;

        MainWindow.Activate();
    }

    /// <summary>True quando a janela deve apenas esconder-se em vez de encerrar a aplicação.</summary>
    public bool ShouldHideOnClose => !_exiting && Settings.Current.MinimizeToTray && _tray is { IsVisible: true };

    public void NotifyHiddenToTray() =>
        _tray?.Notify("NexusGuard", "Continua a correr na bandeja. Clique no ícone para voltar.");

    public void ExitApplication()
    {
        _exiting = true;
        Shutdown(0);
    }

    // ---------------- Erros e encerramento ----------------

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("App", "Erro inesperado na interface", e.Exception);

        MessageBox.Show(
            $"Ocorreu um erro inesperado:\n\n{e.Exception.Message}\n\nA aplicação continua a funcionar. " +
            $"O detalhe ficou registado em:\n{Logger.LogFile}",
            "NexusGuard", MessageBoxButton.OK, MessageBoxImage.Warning);

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _tray = null;

        SystemMonitor.Instance.Stop();
        Logger.Info("App", "NexusGuard encerrado.");

        base.OnExit(e);
    }

    /// <summary>Reinicia a aplicação com pedido de elevação (UAC).</summary>
    public static bool RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Logger.Warn("App", "Caminho do executável indisponível; não foi possível reiniciar elevado.");
                return false;
            }

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });

            if (Current is App app) app.ExitApplication();
            else Current.Shutdown();

            return true;
        }
        catch (Exception ex)
        {
            // O utilizador pode ter recusado o pedido do UAC.
            Logger.Warn("App", $"Elevação recusada ou indisponível: {ex.Message}");
            return false;
        }
    }
}
