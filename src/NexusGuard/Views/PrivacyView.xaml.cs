using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard.Views;

public partial class PrivacyView : UserControl
{
    private readonly ObservableCollection<PrivacyToggle> _toggles = new();
    private bool _loadedOnce;
    private bool _applying;

    public PrivacyView()
    {
        InitializeComponent();
        ToggleList.ItemsSource = _toggles;

        State.PrimaryClicked += (_, _) => App.RestartElevated();

        Loaded += (_, _) =>
        {
            if (_loadedOnce) return;
            _loadedOnce = true;
            Reload();
        };
    }

    private void Reload()
    {
        _toggles.Clear();
        foreach (var toggle in Privacy.Build()) _toggles.Add(toggle);

        Privacy.ReadState(_toggles);
        UpdateSummary();

        if (!Fmt.IsAdmin)
        {
            State.ShowNeedsAdmin("Algumas definições são de todo o computador e");
        }
        else
        {
            State.Hide();
        }
    }

    private void UpdateSummary()
    {
        var on = _toggles.Count(t => t.Enabled);
        SummaryText.Text = $"{on} de {_toggles.Count} proteções ativas.";
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_applying) return;
        if (sender is not CheckBox { Tag: PrivacyToggle toggle } box) return;

        var wanted = box.IsChecked == true;

        if (!Privacy.Apply(toggle, wanted, out var message))
        {
            // Reverte visualmente quando a alteração não foi aplicada.
            toggle.Enabled = !wanted;
            box.IsChecked = !wanted;
            Ui.Warn(this, "Privacidade", message);
            return;
        }

        UpdateSummary();
    }

    private void OnApplyRecommended(object sender, RoutedEventArgs e)
    {
        var pending = _toggles.Where(t => !t.Enabled && (!t.NeedsAdmin || Fmt.IsAdmin)).ToList();

        if (pending.Count == 0)
        {
            Ui.Inform(this, "Privacidade", "Todas as proteções disponíveis já estão ativas.");
            return;
        }

        if (!Ui.Confirm(this, "Aplicar recomendadas",
                $"Serão ativadas {pending.Count} proteções:\n\n" +
                string.Join("\n", pending.Select(t => $"• {t.Name}")) +
                "\n\nTodas ficam no Histórico e podem ser revertidas. Continuar?"))
            return;

        _applying = true;
        var applied = 0;

        try
        {
            foreach (var toggle in pending)
                if (Privacy.Apply(toggle, true, out _)) applied++;
        }
        finally
        {
            _applying = false;
        }

        UpdateSummary();
        Ui.Inform(this, "Privacidade", $"{applied} proteções ativadas.");
    }

    private void OnOpenWindows(object sender, RoutedEventArgs e) => Privacy.OpenWindowsPrivacy();
}
