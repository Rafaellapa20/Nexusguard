using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace NexusGuard.Core;

public enum NotifyLevel { Info, Warning, Error }

/// <summary>
/// Ícone na área de notificação, feito directamente sobre Shell_NotifyIcon. Serve também para as
/// notificações: o balão da bandeja é o que o Windows mostra como toast, sem exigir que a
/// aplicação seja empacotada nem trazer o WinForms só por causa de um ícone.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int IconId = 1;
    private const int WsPopup = unchecked((int)0x80000000);

    private readonly HwndSource _source;
    private IntPtr _icon;
    private bool _added;
    private bool _disposed;

    /// <summary>Clique com o botão esquerdo.</summary>
    public event EventHandler? Activated;

    /// <summary>Clique com o botão direito.</summary>
    public event EventHandler? ContextRequested;

    public TrayIcon(string tooltip)
    {
        // Janela invisível que existe só para receber o callback do ícone.
        _source = new HwndSource(new HwndSourceParameters("NexusGuard.Tray")
        {
            Width = 1,
            Height = 1,
            WindowStyle = WsPopup
        });

        _source.AddHook(OnMessage);

        _icon = LoadAppIcon();

        var data = Build(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP);
        data.szTip = Trim(tooltip, 127);

        _added = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);

        if (_added) Logger.Info("Bandeja", "Ícone criado na área de notificação.");
        else Logger.Warn("Bandeja", "Não foi possível criar o ícone na área de notificação.");
    }

    public bool IsVisible => _added;

    public IntPtr Handle => _source.Handle;

    /// <summary>Mostra uma notificação do Windows a partir do ícone.</summary>
    public void Notify(string title, string message, NotifyLevel level = NotifyLevel.Info)
    {
        if (!_added) return;

        var data = Build(Native.NIF_INFO);
        data.szInfoTitle = Trim(title, 63);
        data.szInfo = Trim(message, 255);
        data.dwInfoFlags = level switch
        {
            NotifyLevel.Warning => Native.NIIF_WARNING,
            NotifyLevel.Error => Native.NIIF_ERROR,
            _ => Native.NIIF_INFO
        };

        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
    }

    public void UpdateTooltip(string tooltip)
    {
        if (!_added) return;

        var data = Build(Native.NIF_TIP);
        data.szTip = Trim(tooltip, 127);

        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
    }

    private Native.NOTIFYICONDATA Build(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = _source.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = Native.WM_TRAYICON,
        hIcon = _icon,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_TRAYICON) return IntPtr.Zero;

        switch ((int)lParam)
        {
            case Native.WM_LBUTTONUP:
            case Native.WM_LBUTTONDBLCLK:
                Activated?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;

            case Native.WM_RBUTTONUP:
                ContextRequested?.Invoke(this, EventArgs.Empty);
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>O ícone vem do próprio executável, onde já está embutido pelo ApplicationIcon.</summary>
    private static IntPtr LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return IntPtr.Zero;

            if (Native.ExtractIconEx(exe, 0, out var large, out var small, 1) > 0)
            {
                if (large != IntPtr.Zero) Native.DestroyIcon(large);
                return small;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Bandeja", $"Não foi possível ler o ícone: {ex.Message}");
        }

        return IntPtr.Zero;
    }

    private static string Trim(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added)
        {
            var data = Build(0);
            Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
            _added = false;
        }

        if (_icon != IntPtr.Zero)
        {
            Native.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        _source.RemoveHook(OnMessage);
        _source.Dispose();
    }
}
