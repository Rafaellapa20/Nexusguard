using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NexusGuard.Core;

namespace NexusGuard.Views;

/// <summary>
/// Configuração de primeira execução: boas-vindas, o que a app faz e nunca faz, proteções e
/// telemetria. Nada é alterado no PC até o usuário confirmar.
/// </summary>
public partial class OnboardingWindow : Window
{
    private static readonly string[] Does =
    {
        "Cria ponto de restauração antes de alterar",
        "Move arquivos para quarentena (7 dias)",
        "Instala só drivers assinados pela Microsoft",
        "Mostra pré-visualização antes de apagar",
        "Registra tudo com opção de desfazer"
    };

    private static readonly string[] Never =
    {
        "Apaga documentos, fotos ou senhas",
        "Envia dados pessoais para a internet",
        "Instala programas extras ou barras",
        "Altera o sistema sem a sua confirmação",
        "Roda como administrador sem necessidade"
    };

    private int _step = 1;

    /// <summary>True quando o usuário pediu para analisar o PC no fim.</summary>
    public bool StartScan { get; private set; }

    public OnboardingWindow()
    {
        InitializeComponent();

        BuildList(DoesList, Does, (Brush)FindResource("B.Ok"));
        BuildList(NeverList, Never, (Brush)FindResource("B.Danger"));

        var settings = Settings.Current;
        OptRestore.IsChecked = settings.CreateRestorePoint;
        OptQuarantine.IsChecked = settings.UseQuarantine;
        OptConfirm.IsChecked = settings.ConfirmEachChange;
        OptWhql.IsChecked = settings.WhqlDriversOnly;
        OptTelemetry.IsChecked = settings.Telemetry;

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Apply();
    }

    private void BuildList(Panel host, IEnumerable<string> items, Brush dot)
    {
        foreach (var text in items)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            var bullet = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = dot,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 0, 0)
            };

            var label = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                Margin = new Thickness(10, 0, 0, 0),
                Foreground = (Brush)FindResource("B.Text")
            };

            Grid.SetColumn(label, 1);
            row.Children.Add(bullet);
            row.Children.Add(label);
            host.Children.Add(row);
        }
    }

    private void Apply()
    {
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        var accent = (Brush)FindResource("B.Accent");
        var idle = (Brush)FindResource("B.Border");

        Seg1.Background = _step >= 1 ? accent : idle;
        Seg2.Background = _step >= 2 ? accent : idle;
        Seg3.Background = _step >= 3 ? accent : idle;
        Seg4.Background = _step >= 4 ? accent : idle;

        BackButton.Visibility = _step > 1 ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;

        NextButton.Content = _step switch
        {
            1 => "Começar",
            2 => "Entendi",
            3 => "Continuar",
            _ => "Analisar o meu PC"
        };
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_step <= 1) return;
        _step--;
        Apply();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step < 4)
        {
            _step++;
            Apply();
            return;
        }

        Persist();
        StartScan = true;
        DialogResult = true;
        Close();
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        Persist();
        DialogResult = true;
        Close();
    }

    private void Persist()
    {
        var settings = Settings.Current;

        settings.CreateRestorePoint = OptRestore.IsChecked == true;
        settings.UseQuarantine = OptQuarantine.IsChecked == true;
        settings.ConfirmEachChange = OptConfirm.IsChecked == true;
        settings.WhqlDriversOnly = OptWhql.IsChecked == true;
        settings.Telemetry = OptTelemetry.IsChecked == true;
        settings.FirstRunCompleted = true;
        settings.Save();

        Logger.Ok("Configuração", "Configuração inicial concluída.");
        History.Add("NexusGuard", "Configuração inicial concluída",
            settings.Telemetry ? "telemetria ligada" : "telemetria desligada");
    }
}
