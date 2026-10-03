using System.Windows;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard.Views;

/// <summary>
/// Aviso de versão nova, mostrado ao abrir a aplicação.
///
/// Antes disto, uma atualização disponível produzia só um ponto no menu lateral e um balão na
/// bandeja que desaparece em segundos — duas coisas fáceis de nunca ver. Quem abre o programa deve
/// saber que há versão nova sem ter de ir procurar.
///
/// Nada é baixado sem o usuário carregar no botão.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _update;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public UpdateWindow(UpdateInfo update)
    {
        InitializeComponent();
        _update = update;

        FromText.Text = Updater.CurrentVersion.ToString(3);
        ToText.Text = update.Version.ToString(3);
        SizeText.Text = $"{update.SizeText} · instala por cima desta versão, sem desinstalar nada";

        NotesText.Text = string.IsNullOrWhiteSpace(update.Notes)
            ? "Esta versão não traz notas publicadas."
            : update.Notes.Trim();

        Closing += (_, e) =>
        {
            // Fechar a meio de um download deixaria um arquivo por confirmar e a aplicação a meio
            // caminho de uma atualização. Mais vale cancelar e deixar a janela fechar depois.
            if (_busy)
            {
                e.Cancel = true;
                _cts?.Cancel();
                return;
            }

            RememberIfSkipped();
        };
    }

    private void RememberIfSkipped()
    {
        if (SkipThis.IsChecked == true)
            Settings.Current.SkippedUpdateVersion = _update.Version.ToString();
    }

    private void OnOpenReleases(object sender, RoutedEventArgs e) =>
        Shell.OpenExternal(Updater.ReleasesUrl);

    private void OnLater(object sender, RoutedEventArgs e)
    {
        RememberIfSkipped();
        DialogResult = false;
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        SetBusy(true);
        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<double>(p => Progress.Value = p);

            var setup = await Updater.DownloadAsync(_update, progress,
                line => StatusText.Text = line, _cts.Token);

            if (setup is null)
            {
                StatusText.Text = "O download falhou ou o arquivo não corresponde ao hash publicado. " +
                                  "Nada foi instalado.";
                return;
            }

            if (!Updater.Install(setup, out var message))
            {
                StatusText.Text = message;
                return;
            }

            // O instalador não consegue substituir o executável com ele em uso.
            if (Application.Current is App app) app.ExitApplication();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Download cancelado.";
        }
        catch (Exception ex)
        {
            Logger.Error("Atualização", "a atualização falhou", ex);
            StatusText.Text = "Não foi possível concluir a atualização. Veja os registros.";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;

        InstallButton.IsEnabled = !busy;
        LaterButton.IsEnabled = !busy;
        SkipThis.IsEnabled = !busy;

        InstallButton.Content = busy ? "Baixando…" : "Baixar e instalar";
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Visibility = busy ? Visibility.Visible : StatusText.Visibility;
    }
}
