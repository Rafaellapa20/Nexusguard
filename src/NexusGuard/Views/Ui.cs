using System.Text;
using System.Windows;
using System.Windows.Controls;
using NexusGuard.Core;

namespace NexusGuard.Views;

/// <summary>Pequenos auxiliares partilhados pelas vistas: diálogos e consola de saída.</summary>
public static class Ui
{
    public static bool Confirm(DependencyObject? owner, string title, string message)
    {
        var window = owner is null ? Application.Current.MainWindow : Window.GetWindow(owner);

        var result = window is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(window, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);

        return result == MessageBoxResult.Yes;
    }

    public static void Inform(DependencyObject? owner, string title, string message)
    {
        var window = owner is null ? Application.Current.MainWindow : Window.GetWindow(owner);

        if (window is null) MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(window, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static void Warn(DependencyObject? owner, string title, string message)
    {
        var window = owner is null ? Application.Current.MainWindow : Window.GetWindow(owner);

        if (window is null) MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(window, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Escolhe uma pasta (usado para destinos de cópia e analises personalizadas).</summary>
    public static string? PickFolder(string title, string? initial = null)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initial)) dialog.InitialDirectory = initial;

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

/// <summary>
/// Acumula linhas de saída de processos e escreve-as na UI em lotes, para que milhares de linhas
/// não bloqueiem a interface.
/// </summary>
public sealed class ConsoleSink
{
    private readonly TextBox _box;
    private readonly StringBuilder _pending = new();
    private readonly object _gate = new();
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private int _lines;

    public ConsoleSink(TextBox box)
    {
        _box = box;
        _timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _timer.Tick += (_, _) => Flush();
        _timer.Start();
    }

    public void Write(string line)
    {
        lock (_gate)
        {
            _pending.AppendLine(line);
        }
    }

    public void Clear()
    {
        lock (_gate) _pending.Clear();
        _lines = 0;
        _box.Clear();
    }

    private void Flush()
    {
        string chunk;

        lock (_gate)
        {
            if (_pending.Length == 0) return;
            chunk = _pending.ToString();
            _pending.Clear();
        }

        _lines += chunk.Count(c => c == '\n');

        // Mantém a consola num tamanho razoavel.
        if (_lines > 3000)
        {
            _box.Clear();
            _lines = 0;
            _box.AppendText("[…linhas anteriores removidas…]" + Environment.NewLine);
        }

        _box.AppendText(chunk);
        _box.ScrollToEnd();
    }
}
