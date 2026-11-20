using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ProjectLuma.UI.ViewModels;
using DrawingIcon = System.Drawing.Icon;
using WinForms = System.Windows.Forms;

namespace ProjectLuma.App;

/// <summary>Manages the native Windows Notification Area icon, context menu, window hide/restore, and TaskbarCreated recovery.</summary>
public sealed class WindowsSystemTrayService : IDisposable
{
    private readonly TrayLifecycleController _controller;
    private readonly Func<bool> _getMinimizeToTray;
    private readonly Action _onExitShutdown;
    private Window? _window;
    private HwndSource? _hwndSource;
    private uint _taskbarCreatedMessageId;
    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ContextMenuStrip? _contextMenu;
    private DrawingIcon? _trayIconGraphic;
    private bool _disposed;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    public WindowsSystemTrayService(
        TrayLifecycleController controller,
        Func<bool> getMinimizeToTray,
        Action onExitShutdown)
    {
        _controller = controller;
        _getMinimizeToTray = getMinimizeToTray;
        _onExitShutdown = onExitShutdown;

        _controller.ShowRequested += RestoreWindowOnUi;
        _controller.HideRequested += HideWindowOnUi;
        _controller.ExitRequested += ExitOnUi;
        _controller.RecreateTrayIconRequested += RecreateNotifyIconOnUi;
    }

    public void Attach(Window mainWindow)
    {
        _window = mainWindow;
        _controller.SyncPreference(_getMinimizeToTray());
        EnsureNotifyIconCreated();
        SyncVisibility();

        mainWindow.SourceInitialized += (_, _) => HookWindowMessages(mainWindow);
        if (PresentationSource.FromVisual(mainWindow) is HwndSource existingSource)
            HookWindowMessages(mainWindow, existingSource);

        mainWindow.Closing += (_, e) =>
        {
            if (_controller.HandleWindowClosing(_getMinimizeToTray()))
                e.Cancel = true;
        };
    }

    public void RefreshPreferenceState()
    {
        if (_disposed) return;
        _controller.SyncPreference(_getMinimizeToTray());
        SyncVisibility();
    }

    private void HookWindowMessages(Window window, HwndSource? sourceOverride = null)
    {
        if (_hwndSource != null) return;
        _taskbarCreatedMessageId = RegisterWindowMessage("TaskbarCreated");
        _hwndSource = sourceOverride ?? (PresentationSource.FromVisual(window) as HwndSource);
        _hwndSource?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessageId != 0 && (uint)msg == _taskbarCreatedMessageId)
            _controller.HandleTaskbarCreated();
        return IntPtr.Zero;
    }

    private void EnsureNotifyIconCreated()
    {
        if (_disposed || _notifyIcon != null) return;

        _trayIconGraphic ??= LoadTrayIcon();
        _contextMenu ??= BuildContextMenu();

        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "Project Luma — AMD Gaming Enhancement",
            Icon = _trayIconGraphic,
            ContextMenuStrip = _contextMenu,
            Visible = _controller.TrayIconVisible
        };

        _notifyIcon.DoubleClick += (_, _) => _controller.RequestRestore();
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
                _controller.RequestRestore();
        };
    }

    private WinForms.ContextMenuStrip BuildContextMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        var openItem = new WinForms.ToolStripMenuItem("Open Project Luma")
        {
            Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold)
        };
        openItem.Click += (_, _) => _controller.RequestRestore();

        var exitItem = new WinForms.ToolStripMenuItem("Exit Project Luma");
        exitItem.Click += (_, _) => _controller.RequestExplicitExit();

        menu.Items.Add(openItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);
        return menu;
    }

    private void SyncVisibility()
    {
        if (_disposed) return;
        if (_notifyIcon == null && _controller.TrayIconVisible)
            EnsureNotifyIconCreated();
        if (_notifyIcon != null)
            _notifyIcon.Visible = _controller.TrayIconVisible;
    }

    private void HideWindowOnUi()
    {
        if (_window == null) return;
        _window.Dispatcher.Invoke(() =>
        {
            _window.ShowInTaskbar = false;
            _window.Hide();
            SyncVisibility();
        });
    }

    private void RestoreWindowOnUi()
    {
        if (_window == null) return;
        _window.Dispatcher.Invoke(() =>
        {
            _window.ShowInTaskbar = true;
            if (!_window.IsVisible)
                _window.Show();
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            _window.Activate();
            _window.Topmost = true;
            _window.Topmost = false;
            _window.Focus();
            SyncVisibility();
        });
    }

    private void RecreateNotifyIconOnUi()
    {
        if (_disposed || _window == null) return;
        _window.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            EnsureNotifyIconCreated();
            SyncVisibility();
        });
    }

    private void ExitOnUi()
    {
        void DoExit()
        {
            DisposeNotifyIconOnly();
            _onExitShutdown();
        }

        if (_window != null && !_window.Dispatcher.CheckAccess())
            _window.Dispatcher.BeginInvoke(DoExit);
        else
            DoExit();
    }

    private static DrawingIcon LoadTrayIcon()
    {
        string icoPath = Path.Combine(AppContext.BaseDirectory, "project-luma.ico");
        if (File.Exists(icoPath))
            return new DrawingIcon(icoPath);

        string? procPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(procPath) && File.Exists(procPath))
        {
            var extracted = DrawingIcon.ExtractAssociatedIcon(procPath);
            if (extracted != null) return extracted;
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void DisposeNotifyIconOnly()
    {
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        DisposeNotifyIconOnly();
        _contextMenu?.Dispose();
        _contextMenu = null;
        _trayIconGraphic?.Dispose();
        _trayIconGraphic = null;
    }
}
