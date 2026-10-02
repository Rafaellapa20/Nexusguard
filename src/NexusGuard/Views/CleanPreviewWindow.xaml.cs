using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard.Views;

/// <summary>Uma linha da pré-visualização: um arquivo que será movido para quarentena.</summary>
public sealed class PreviewFile : Observable
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required long Size { get; init; }
    public required string Category { get; init; }

    private bool _selected = true;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    public string SizeText => Fmt.Bytes(Size);
}

/// <summary>
/// Mostra exactamente o que vai ser removido antes de mexer em alguma coisa. Os arquivos
/// protegidos (documentos, perfis de navegador) já vêm filtrados e são explicados no aviso.
/// </summary>
public partial class CleanPreviewWindow : Window
{
    private readonly List<PreviewFile> _all;
    private readonly ObservableCollection<PreviewFile> _shown = new();
    private string _filter = "Todos";

    /// <summary>Caminhos que o usuário manteve selecionados.</summary>
    public List<string> Accepted { get; private set; } = new();

    public bool CreateRestorePoint => MakeRestorePoint.IsChecked == true;

    public CleanPreviewWindow(IEnumerable<(CleanTarget target, List<CleanFile> files)> groups)
    {
        InitializeComponent();

        _all = groups
            .SelectMany(g => g.files
                .Where(f => !f.Protected)
                .Select(f => new PreviewFile
                {
                    Path = f.Path,
                    Name = f.Name,
                    Size = f.Size,
                    Category = g.target.Name
                }))
            .OrderByDescending(f => f.Size)
            .ToList();

        var protectedCount = groups.Sum(g => g.files.Count(f => f.Protected));

        foreach (var file in _all)
            file.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PreviewFile.Selected)) UpdateTotals(); };

        FileList.ItemsSource = _shown;

        BuildChips(groups.Select(g => g.target.Name).Distinct());
        ApplyFilter();

        var days = Settings.Current.QuarantineDays;
        QuarantinePill.Text = Settings.Current.UseQuarantine ? $"Quarentena {days} dias" : "Remoção definitiva";

        FooterNote.Text = Settings.Current.UseQuarantine
            ? $"Os arquivos ficam recuperáveis em Quarentena durante {days} dias."
            : "A quarentena está desligada nas configurações — os arquivos serão apagados.";

        if (protectedCount > 0)
        {
            ProtectedBanner.Visibility = Visibility.Visible;
            ProtectedText.Text = protectedCount == 1
                ? "1 item protegido foi excluído automaticamente — documentos, fotos e perfis de navegador nunca são tocados."
                : $"{protectedCount} itens protegidos foram excluídos automaticamente — documentos, fotos e perfis de navegador nunca são tocados.";
        }

        MakeRestorePoint.IsChecked = Settings.Current.CreateRestorePoint;
        MakeRestorePoint.IsEnabled = Fmt.IsAdmin;

        if (!Fmt.IsAdmin)
            MakeRestorePoint.Content = "Criar ponto de restauração antes (requer administrador)";

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        UpdateTotals();
    }

    private void BuildChips(IEnumerable<string> categories)
    {
        void AddChip(string label)
        {
            var chip = new ToggleButton
            {
                Content = label,
                Style = (Style)FindResource("Chip"),
                IsChecked = label == _filter
            };

            chip.Checked += (_, _) =>
            {
                _filter = label;

                foreach (var other in Chips.Children.OfType<ToggleButton>())
                    if (!ReferenceEquals(other, chip)) other.IsChecked = false;

                ApplyFilter();
            };

            // Sem este guarda, clicar no chip ativo deixava a lista sem filtro nenhum.
            chip.Unchecked += (_, _) => { if (_filter == label) chip.IsChecked = true; };

            Chips.Children.Add(chip);
        }

        AddChip("Todos");
        foreach (var category in categories) AddChip(category);
    }

    private void ApplyFilter()
    {
        _shown.Clear();

        var items = _filter == "Todos"
            ? _all
            : _all.Where(f => f.Category == _filter).ToList();

        // A lista pode ter dezenas de milhares de arquivos; mostrar os maiores chega para decidir.
        foreach (var file in items.Take(400)) _shown.Add(file);

        SelectionText.Text = items.Count > _shown.Count
            ? $"a mostrar os {_shown.Count} maiores de {Fmt.Count(items.Count)} arquivos"
            : $"{Fmt.Count(items.Count)} arquivos";

        UpdateTotals();
    }

    private void UpdateTotals()
    {
        var selected = _all.Where(f => f.Selected).ToList();
        var bytes = selected.Sum(f => f.Size);

        SubtitleText.Text = $"{Fmt.Count(selected.Count)} arquivos · {Fmt.Bytes(bytes)} · " +
                            (Settings.Current.UseQuarantine
                                ? "nada é apagado de forma permanente"
                                : "remoção definitiva");

        ConfirmButton.Content = Settings.Current.UseQuarantine
            ? $"Mover para quarentena ({Fmt.Bytes(bytes)})"
            : $"Apagar ({Fmt.Bytes(bytes)})";

        ConfirmButton.IsEnabled = selected.Count > 0;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        var value = SelectAll.IsChecked == true;

        foreach (var file in _shown) file.Selected = value;

        UpdateTotals();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        Accepted = _all.Where(f => f.Selected).Select(f => f.Path).ToList();
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
