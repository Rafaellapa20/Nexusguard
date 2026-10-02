using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using NexusGuard.Core;

namespace NexusGuard;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Datas e numeros em portugues de Portugal em toda a interface.
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

        Logger.Info("App", $"NexusGuard iniciado {(Fmt.IsAdmin ? "como administrador" : "sem elevação")}.");
        SystemMonitor.Instance.Start();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("App", "Erro inesperado na interface", e.Exception);

        MessageBox.Show(
            $"Ocorreu um erro inesperado:\n\n{e.Exception.Message}\n\nA app continua a funcionar. " +
            $"O detalhe ficou registado em:\n{Logger.LogFile}",
            "NexusGuard", MessageBoxButton.OK, MessageBoxImage.Warning);

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemMonitor.Instance.Stop();
        Logger.Info("App", "NexusGuard encerrado.");
        base.OnExit(e);
    }

    /// <summary>Reinicia a app com pedido de elevação (UAC).</summary>
    public static bool RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Logger.Warn("App", "Caminho do executavel indisponível; não foi possível reiniciar elevado.");
                return false;
            }

            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Current.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            // O usuário pode ter recusado o pedido do UAC.
            Logger.Warn("App", $"Elevacao recusada ou indisponível: {ex.Message}");
            return false;
        }
    }
}
