using System.Windows;
using System.Windows.Controls;
using NexusGuard.Core;
using NexusGuard.Modules;

namespace NexusGuard.Views;

public partial class StartupView : UserControl
{
    private bool _loadedOnce;

    public StartupView()
    {
        InitializeComponent();

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
        try
        {
            var items = StartupManager.Load();
            StartupList.ItemsSource = items;

            var enabled = items.Count(i => i.Enabled);

            TotalText.Text = Fmt.Count(items.Count);
            SubText.Text = items.Count == 0
                ? "Nenhum programa configurado para arrancar com o Windows."
                : $"{enabled} ativos · {items.Count - enabled} desligados. Desligar é reversível a qualquer momento.";

            if (MainWindow.Instance is { } main) main.Snapshot.StartupItems = enabled;

            if (!Fmt.IsAdmin && items.Any(i => i.NeedsAdmin))
                State.ShowNeedsAdmin("Alterar itens de inicialização de todos os utilizadores");
            else
                State.Hide();
        }
        catch (Exception ex)
        {
            Logger.Error("Arranque", "Falha ao ler os programas de arranque", ex);
            SubText.Text = "Não foi possível ler os programas de arranque.";
        }
    }

    private void OnReload(object sender, RoutedEventArgs e) => Reload();

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: StartupItem item } box) return;

        var wanted = box.IsChecked == true;

        if (!StartupManager.SetEnabled(item, wanted, out var message))
        {
            item.Enabled = !wanted;
            box.IsChecked = !wanted;
            Ui.Warn(this, "Programas no arranque", message);
            return;
        }

        SubText.Text = message;

        if (MainWindow.Instance is { } main && StartupList.ItemsSource is IEnumerable<StartupItem> items)
            main.Snapshot.StartupItems = items.Count(i => i.Enabled);
    }

    private void OnOpenTaskManager(object sender, RoutedEventArgs e) => Shell.OpenExternal("taskmgr.exe");
}
