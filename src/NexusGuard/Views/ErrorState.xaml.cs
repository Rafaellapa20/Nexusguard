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

    /// <summary>
    /// Arquivos que ficaram onde estavam. Com a quarentena ligada nada é apagado sem volta, por
    /// isso isto não é uma perda: é só espaço que continua ocupado até à próxima limpeza.
    /// </summary>
    public void ShowLockedFiles(int locked, int total) => Show(
        "Limpeza",
        "Alguns arquivos não foram movidos",
        $"{locked} de {total} arquivos estavam em uso por outro programa e ficaram onde estavam. " +
        "Nada foi perdido. Feche esses programas, ou reinicie o PC, e limpe outra vez.",
        StateSeverity.Warning,
        secondary: $"Ver os {locked} arquivos");

    /// <summary>
    /// Arquivos que o Windows vai apagar no arranque seguinte. Só acontece com a quarentena
    /// desligada, e dizer "serão tratados" escondia que a remoção é definitiva.
    /// </summary>
    public void ShowScheduledForReboot(int scheduled) => Show(
        "Limpeza",
        $"{scheduled} arquivos saem no próximo arranque",
        "Estavam presos por outro programa e não puderam ser apagados agora. O Windows remove-os " +
        "ao arrancar, antes de os voltar a abrir. Como a quarentena está desligada, a remoção é " +
        "definitiva e não há como repô-los.",
        StateSeverity.Warning,
        secondary: "Ver os arquivos");

    public void ShowNotEnoughSpace(string drive, string free, string needed) => Show(
        "Backup",
        "Destino sem espaço suficiente",
        $"{drive} tem {free} livres, mas o backup precisa de {needed}. Escolha outro disco ou " +
        "reduza o que está incluído.",
        StateSeverity.Danger,
        "Escolher outro destino", "Ajustar conteúdo");

    /// <summary>
    /// A quarentena não cabe no disco onde vive. Não é um erro da limpeza: é a limpeza a recusar
    /// encher o disco do sistema para guardar o que ia apagar.
    /// </summary>
    public void ShowQuarantineNoSpace(string detail) => Show(
        "Limpeza",
        "A quarentena não tem espaço",
        detail + " Nada foi apagado. Esvazie a quarentena no Histórico, liberte espaço nesse disco, " +
        "ou desligue a quarentena em Configurações — nesse caso a limpeza passa a ser definitiva.",
        StateSeverity.Danger,
        "Abrir o Histórico", "Abrir as Configurações");

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
