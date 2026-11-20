using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ProjectLuma.Setup;

public partial class UninstallWindow : Window
{
    private readonly string _installDir;
    private readonly string? _payloadSourceOverride;
    private bool _isBusy;

    public UninstallWindow(string installDir, string? payloadSourceOverride = null)
    {
        InitializeComponent();
        _installDir = string.IsNullOrWhiteSpace(installDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ProjectLuma")
            : installDir;
        _payloadSourceOverride = payloadSourceOverride;

        RefreshInspection();
    }

    private void RefreshInspection()
    {
        var summary = InstallerEngine.InspectInstallationState(_installDir);
        string ver = string.IsNullOrWhiteSpace(summary.InstalledVersion)
            ? InstallerEngine.ProductVersion
            : summary.InstalledVersion;
        VersionBadgeText.Text = $"v{ver}";
        InstallPathSubtitle.Text = summary.InstallDirectory;
        EnhancedGamesCountText.Text = summary.EnhancedGamesWithBackups == 1
            ? "1 game"
            : $"{summary.EnhancedGamesWithBackups} games";
        BackupsSizeText.Text = $"{summary.BackupsSizeMb:0.1} MB";
        CacheSizeText.Text = $"{summary.CacheAndLogsSizeMb:0.1} MB";
        RepairBtn.IsEnabled = App.HasAvailablePayloadSource(_payloadSourceOverride);
    }

    private void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export Project Luma Diagnostics",
            FileName = $"ProjectLuma-Diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.json",
            Filter = "JSON Report (*.json)|*.json|Text Files (*.txt)|*.txt"
        };
        if (dlg.ShowDialog() == true)
        {
            string saved = InstallerEngine.ExportUninstallDiagnostics(dlg.FileName, _installDir);
            StatusBannerText.Text = $"Saved diagnostic summary to {saved}";
            StatusBannerText.Visibility = Visibility.Visible;
        }
    }

    private async void OnRepairClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        _isBusy = true;
        SetInputsEnabled(false);
        StatusBannerText.Text = "Preparing installer files and repairing Project Luma...";
        StatusBannerText.Visibility = Visibility.Visible;

        try
        {
            await Task.Run(() =>
            {
                string payloadDir = App.ResolvePayloadDirectory(_payloadSourceOverride);
                InstallerEngine.Repair(payloadDir, _installDir, registerInWindows: true);
            });
            ShowCompletion(
                "Repair Complete",
                "Project Luma installation has been repaired.",
                "Missing or damaged application files were restored while keeping all user settings, profiles, and game backups intact.",
                $"Target directory: {_installDir}\nShortcuts & Installed Apps registration verified.");
        }
        catch (Exception ex)
        {
            StatusBannerText.Text = $"Repair failed: {ex.Message}";
            SetInputsEnabled(true);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        var confirm = MessageBox.Show(
            this,
            "Are you sure you want to uninstall Project Luma?",
            "Confirm Project Luma Uninstall",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        _isBusy = true;
        SetInputsEnabled(false);

        bool restoreGames = RestoreGamesCheck.IsChecked == true;
        bool preserveBackups = PreserveBackupsCheck.IsChecked == true;
        bool removePrefs = RemoveCachePrefsCheck.IsChecked == true;
        bool exportDiag = ExportDiagnosticsCheck.IsChecked == true;

        try
        {
            if (exportDiag)
            {
                string desktopReport = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    $"ProjectLuma-Uninstall-Diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.json");
                InstallerEngine.ExportUninstallDiagnostics(desktopReport, _installDir);
            }

            var result = await Task.Run(() => InstallerEngine.UninstallWithOptions(
                _installDir,
                restoreGameFiles: restoreGames,
                preserveBackups: preserveBackups,
                removeUserPreferencesAndCache: removePrefs,
                removeShortcuts: true,
                removeRegistryEntry: true));

            string detail = result.PreservedBackups
                ? "Project Luma was removed and your game backup archives remain safely preserved on disk."
                : "Project Luma, local cache/settings, and backup archives were cleaned up.";
            ShowCompletion(
                "Uninstall Complete",
                "Project Luma has been removed cleanly.",
                detail,
                string.IsNullOrWhiteSpace(result.Report)
                    ? $"Cleaned installation folder: {_installDir}"
                    : result.Report);
        }
        catch (Exception ex)
        {
            StatusBannerText.Text = $"Uninstall failed: {ex.Message}";
            StatusBannerText.Visibility = Visibility.Visible;
            SetInputsEnabled(true);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void ShowCompletion(string badge, string headline, string detail, string reportText)
    {
        CompletePillText.Text = badge;
        CompleteHeadlineText.Text = headline;
        CompleteDetailText.Text = detail;
        CompleteReportText.Text = reportText;

        MainOptionsPanel.Visibility = Visibility.Collapsed;
        CompletePanel.Visibility = Visibility.Visible;
        CancelBtn.Visibility = Visibility.Collapsed;
        ActionButtonsPanel.Visibility = Visibility.Collapsed;
        CloseAfterFinishBtn.Visibility = Visibility.Visible;
    }

    private void SetInputsEnabled(bool enabled)
    {
        RestoreGamesCheck.IsEnabled = enabled;
        PreserveBackupsCheck.IsEnabled = enabled;
        RemoveCachePrefsCheck.IsEnabled = enabled;
        ExportDiagnosticsCheck.IsEnabled = enabled;
        ExportDiagNowBtn.IsEnabled = enabled;
        RepairBtn.IsEnabled = enabled && App.HasAvailablePayloadSource(_payloadSourceOverride);
        UninstallBtn.IsEnabled = enabled;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
        => Close();

    public async Task CaptureAllScreensAsync(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        VersionBadgeText.Text = $"v{InstallerEngine.ProductVersion}";
        InstallPathSubtitle.Text = @"C:\Users\Player\AppData\Local\Programs\ProjectLuma";
        EnhancedGamesCountText.Text = "2 games";
        BackupsSizeText.Text = "48.6 MB";
        CacheSizeText.Text = "19.4 MB";
        MainOptionsPanel.Visibility = Visibility.Visible;
        CompletePanel.Visibility = Visibility.Collapsed;
        CancelBtn.Visibility = Visibility.Visible;
        ActionButtonsPanel.Visibility = Visibility.Visible;
        CloseAfterFinishBtn.Visibility = Visibility.Collapsed;

        await SetupWindow.RenderWindowToPngAsync(this, Path.Combine(outputDir, "Uninstaller_Main.png"));

        ShowCompletion(
            "Uninstall Complete",
            "Project Luma has been removed cleanly.",
            "Original game files were verified and restored via SHA-256 checksums, and all shortcuts and Windows Installed Apps entries were removed.",
            "Games restored: 2 (preserved conflict/user-modified: 0)\nRemoved application files: 24\nRemoved shortcuts: True · Registry cleaned: True");

        await SetupWindow.RenderWindowToPngAsync(this, Path.Combine(outputDir, "Uninstaller_Complete.png"));
    }
}
