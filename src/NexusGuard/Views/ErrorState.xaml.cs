using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NexusGuard.Views;

public enum StateSeverity { Info, Warning, Danger, Empty }

/// <summary>
/// Cartão de estado padronizado (sem permissões, sem internet, nada a fazer, …), com os textos
/// e ações definidos no handoff. Usado por todos os módulos em vez de mensagens soltas.
/// </summary>
public partial class ErrorState : UserControl
{
    public event EventHandler? PrimaryClicked;
    public event EventHandler? SecondaryClicked;

    public ErrorState()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    /// <summary>Preenche e mostra o cartão.</summary>
    public void Show(string module, string title, string body, StateSeverity severity = StateSeverity.Warning,
        string? primary = null, string? secondary = null)
    {
        ModuleText.Text = module.ToUpperInvariant();
        TitleText.Text = title;
        BodyText.Text = body;

        var (background, stroke, icon) = severity switch
        {
            StateSeverity.Danger => ("B.DangerSoft", "B.Danger", "I.Alert"),
            StateSeverity.Info => ("B.AccentSoft", "B.Accent", "I.Admin"),
            StateSeverity.Empty => ("B.OkSoft", "B.Ok", "I.Check"),
            _ => ("B.WarnSoft", "B.Warn", "I.Alert")
        };

        IconBox.Background = (Brush)FindResource(background);
        IconPath.Stroke = (Brush)FindResource(stroke);
        IconPath.Data = (Geometry)FindResource(icon);

        PrimaryButton.Content = primary;
        PrimaryButton.Visibility = string.IsNullOrWhiteSpace(primary) ? Visibility.Collapsed : Visibility.Visible;

        SecondaryButton.Content = secondary;
        SecondaryButton.Visibility = string.IsNullOrWhiteSpace(secondary) ? Visibility.Collapsed : Visibility.Visible;

        Actions.Visibility = PrimaryButton.Visibility == Visibility.Visible ||
                             SecondaryButton.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

        Visibility = Visibility.Visible;
    }

    public void Hide() => Visibility = Visibility.Collapsed;

    // ---------------- Estados prontos a usar ----------------

    public void ShowNeedsAdmin(string what) => Show(
        "Permissões",
        "Esta ação precisa de administrador",
        $"{what} exige elevação. O Windows vai pedir confirmação (UAC) só desta vez.",
        StateSeverity.Info,
        "Continuar como administrador", "Agora não");

    public void ShowNoWinget() => Show(
        "Aplicativos",
        "winget não está instalado",
        "O gerenciador de pacotes do Windows é necessário para atualizar apps. " +
        "É gratuito e oficial da Microsoft (Instalador de Aplicativos).",
        StateSeverity.Warning,
        "Instalar winget", "Saber mais");

    public void ShowOffline() => Show(
        "Aplicativos",
        "Sem conexão com a internet",
        "Não foi possível consultar atualizações. Limpeza, memória e inicialização continuam " +
        "funcionando offline.",
        StateSeverity.Warning,
        "Tentar novamente", "Trabalhar offline");

    public void ShowNothingToClean(string since) => Show(
        "Limpeza",
        "Nada para limpar",
        $"O seu PC já está limpo — a última limpeza foi {since}. Volte amanhã ou deixe o " +
        "agendamento cuidar disso.",
        StateSeverity.Empty,
        "Ver agendamento");

    public void ShowLockedFiles(int locked, int total) => Show(
        "Limpeza",
        "Alguns arquivos não foram movidos",
        $"{locked} de {total} arquivos estão em uso por outro programa. Serão tratados na próxima " +
        "inicialização, antes do Windows bloqueá-los.",
        StateSeverity.Warning,
        "Agendar para a inicialização", $"Ver os {locked} arquivos");

    public void ShowNotEnoughSpace(string drive, string free, string needed) => Show(
        "Backup",
        "Destino sem espaço suficiente",
        $"{drive} tem {free} livres, mas o backup precisa de {needed}. Escolha outro disco ou " +
        "reduza o que está incluído.",
        StateSeverity.Danger,
        "Escolher outro destino", "Ajustar conteúdo");

    public void ShowUnsignedDriver(string name, string version) => Show(
        "Drivers",
        "Driver ignorado por segurança",
        $"«{name} {version}» não tem assinatura WHQL da Microsoft. Por segurança o NexusGuard não " +
        "o instala. Pode obtê-lo no site do fabricante.",
        StateSeverity.Warning,
        "Abrir site do fabricante", "Ignorar este driver");

    private void OnPrimary(object sender, RoutedEventArgs e) => PrimaryClicked?.Invoke(this, EventArgs.Empty);

    private void OnSecondary(object sender, RoutedEventArgs e) => SecondaryClicked?.Invoke(this, EventArgs.Empty);
}
