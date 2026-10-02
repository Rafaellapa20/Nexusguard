using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace NexusGuard.Core;

/// <summary>Base mínima de notificacao para os modelos de vista.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Levanta a notificação na thread da interface. Os módulos trabalham em threads de fundo e,
    /// sem este reencaminhamento, as linhas das listas ficavam presas no último valor conhecido.
    /// </summary>
    protected void Raise([CallerMemberName] string? name = null)
    {
        var handler = PropertyChanged;
        if (handler is null) return;

        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
            handler(this, new PropertyChangedEventArgs(name));
        else
            dispatcher.BeginInvoke(() => handler(this, new PropertyChangedEventArgs(name)));
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>Comando simples com suporte para handlers assincronos e bloqueio durante a execução.</summary>
public sealed class Cmd : ICommand
{
    private readonly Func<object?, Task> _run;
    private readonly Func<object?, bool>? _can;
    private bool _busy;

    public Cmd(Action<object?> run, Func<object?, bool>? can = null)
    {
        _run = p => { run(p); return Task.CompletedTask; };
        _can = can;
    }

    public Cmd(Func<Task> run, Func<object?, bool>? can = null)
    {
        _run = _ => run();
        _can = can;
    }

    public Cmd(Func<object?, Task> run, Func<object?, bool>? can = null)
    {
        _run = run;
        _can = can;
    }

    public event EventHandler? CanExecuteChanged;

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    public bool CanExecute(object? parameter) => !_busy && (_can?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        _busy = true;
        Refresh();

        try
        {
            await _run(parameter).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Logger.Warn("Comando", "Operação cancelada pelo usuário.");
        }
        catch (Exception ex)
        {
            Logger.Error("Comando", "Falha ao executar a acao", ex);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }
}
