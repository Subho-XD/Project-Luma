namespace ProjectLuma.UI.ViewModels;

/// <summary>Testable state machine coordinating minimize-to-tray close interception, restore, explicit exit, and Explorer restart recovery.</summary>
public sealed class TrayLifecycleController
{
    public bool IsExplicitExitRequested { get; private set; }
    public bool IsWindowHiddenToTray { get; private set; }
    public bool TrayIconVisible { get; private set; }
    public int ExplorerRestartRecoveryCount { get; private set; }

    public event Action? ShowRequested;
    public event Action? HideRequested;
    public event Action? ExitRequested;
    public event Action? RecreateTrayIconRequested;

    public void SyncPreference(bool minimizeToTrayEnabled)
    {
        TrayIconVisible = minimizeToTrayEnabled || IsWindowHiddenToTray;
    }

    /// <summary>Returns true if closing should be cancelled and the window hidden to the system tray instead of terminating.</summary>
    public bool HandleWindowClosing(bool minimizeToTrayEnabled)
    {
        if (IsExplicitExitRequested || !minimizeToTrayEnabled)
        {
            IsWindowHiddenToTray = false;
            TrayIconVisible = false;
            return false;
        }

        IsWindowHiddenToTray = true;
        TrayIconVisible = true;
        HideRequested?.Invoke();
        return true;
    }

    public void RequestRestore()
    {
        IsWindowHiddenToTray = false;
        ShowRequested?.Invoke();
    }

    public void RequestExplicitExit()
    {
        IsExplicitExitRequested = true;
        IsWindowHiddenToTray = false;
        TrayIconVisible = false;
        ExitRequested?.Invoke();
    }

    public void HandleTaskbarCreated()
    {
        if (!TrayIconVisible && !IsWindowHiddenToTray)
            return;

        ExplorerRestartRecoveryCount++;
        RecreateTrayIconRequested?.Invoke();
    }
}
