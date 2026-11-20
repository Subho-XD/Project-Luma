using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ProjectLuma.Setup;

public partial class SetupWindow : Window
{
    private readonly string? _payloadSourceOverride;
    private int _currentStep = 1;
    private bool _isInstalling;
    private bool _isInstalled;
    private CancellationTokenSource? _installCts;
    private InstallerSystemCheckReport? _compatReport;

    public SetupWindow(string? payloadSourceOverride = null, string? installDirOverride = null)
    {
        InitializeComponent();
        _payloadSourceOverride = payloadSourceOverride;
        InstallDirBox.Text = string.IsNullOrWhiteSpace(installDirOverride)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "ProjectLuma")
            : Path.GetFullPath(installDirOverride);

        Loaded += async (_, _) => await RunCompatibilityScanAsync();
        UpdateStepVisuals();
    }

    private async Task RunCompatibilityScanAsync()
    {
        _compatReport = await InstallerEngine.RunCompatibilityCheckAsync(InstallDirBox.Text);
        CompatSummaryTitle.Text = _compatReport.SummaryTitle;
        CompatSummaryDetail.Text = _compatReport.SummaryDetail;
        CompatItemsList.ItemsSource = _compatReport.Items;
        SidebarGpuText.Text = string.IsNullOrWhiteSpace(_compatReport.GpuSummary)
            ? "AMD Radeon Hardware"
            : _compatReport.GpuSummary;

        var readyBg = CreateBrush("#13251B");
        var readyBorder = CreateBrush("#276B45");
        var readyFg = CreateBrush("#55C78A");
        var warnBg = CreateBrush("#2A1E10");
        var warnBorder = CreateBrush("#8C6220");
        var warnFg = CreateBrush("#E7B45B");

        CompatSummaryBanner.Background = _compatReport.IsAmdGpu ? readyBg : warnBg;
        CompatSummaryBanner.BorderBrush = _compatReport.IsAmdGpu ? readyBorder : warnBorder;
        CompatSummaryTitle.Foreground = _compatReport.IsAmdGpu ? readyFg : warnFg;
    }

    private void OnLicenseChanged(object sender, RoutedEventArgs e)
    {
        if (_currentStep == 1 && NextBtn != null)
            NextBtn.IsEnabled = AcceptLicenseCheck.IsChecked == true;
    }

    private void OnToggleLicenseDetailsClick(object sender, RoutedEventArgs e)
    {
        LicenseDetailsBox.Visibility = LicenseDetailsBox.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private async void OnRecheckSystemClick(object sender, RoutedEventArgs e)
        => await RunCompatibilityScanAsync();

    private void OnInstallDirChanged(object sender, TextChangedEventArgs e)
    {
        if (DiskSpaceText == null) return;
        double freeGb = InstallerEngine.GetAvailableDiskSpaceGb(InstallDirBox.Text);
        DiskSpaceText.Text = $"Required: ~220 MB · Available on target drive: {freeGb:0.1} GB";
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose Project Luma Installation Folder" };
        if (dlg.ShowDialog() == true)
            InstallDirBox.Text = Path.Combine(dlg.FolderName, "ProjectLuma");
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_isInstalling || _isInstalled || _currentStep <= 1) return;
        _currentStep--;
        UpdateStepVisuals();
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_currentStep == 1)
        {
            _currentStep = 2;
            if (_compatReport == null)
                await RunCompatibilityScanAsync();
            UpdateStepVisuals();
            return;
        }

        if (_currentStep == 2)
        {
            _currentStep = 3;
            OnInstallDirChanged(InstallDirBox, null!);
            UpdateStepVisuals();
            return;
        }

        if (_currentStep == 3 && !_isInstalled)
        {
            await ExecuteInstallAsync();
            return;
        }

        if (_currentStep == 3 && _isInstalled)
        {
            if (FinishDesktopShortcutCheck.IsChecked == true && DesktopShortcutCheck.IsChecked != true)
                InstallerEngine.EnsureDesktopShortcut(InstallDirBox.Text.Trim());

            if (LaunchAfterFinishCheck.IsChecked == true)
            {
                string exe = Path.Combine(InstallDirBox.Text.Trim(), "ProjectLuma.exe");
                if (File.Exists(exe))
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
            Close();
        }
    }

    private async Task ExecuteInstallAsync()
    {
        if (_isInstalling) return;
        _isInstalling = true;
        _installCts = new CancellationTokenSource();

        InstallErrorText.Visibility = Visibility.Collapsed;
        InstallDirBox.IsEnabled = false;
        BrowseBtn.IsEnabled = false;
        BackBtn.IsEnabled = false;
        NextBtn.IsEnabled = false;
        CancelBtn.Content = "Cancel Install";

        string targetDir = InstallDirBox.Text.Trim();
        bool desktop = DesktopShortcutCheck.IsChecked == true;
        bool startMenu = StartMenuShortcutCheck.IsChecked == true;

        var progress = new Progress<(int Percent, string Message)>(p =>
        {
            InstallProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
            InstallPercentText.Text = $"{Math.Clamp(p.Percent, 0, 100)}%";
            InstallPhaseText.Text = p.Message;
        });

        try
        {
            IProgress<(int Percent, string Message)> scaledInstallProgress =
                new Progress<(int Percent, string Message)>(p =>
                {
                    int scaled = 30 + (Math.Clamp(p.Percent, 0, 100) * 70) / 100;
                    ((IProgress<(int, string)>)progress).Report((scaled, p.Message));
                });

            await Task.Run(() =>
            {
                ((IProgress<(int, string)>)progress).Report((2, "Preparing installer payload..."));
                string payloadDir = App.ResolvePayloadDirectory(_payloadSourceOverride, progress, _installCts.Token);
                InstallerEngine.Install(
                    payloadDir,
                    targetDir,
                    desktop,
                    scaledInstallProgress,
                    _installCts.Token,
                    startMenu,
                    registerInWindows: true);
            }, _installCts.Token);

            _isInstalled = true;
            FinishDesktopShortcutCheck.IsChecked = desktop;
            CompletePathSummary.Text = $"Project Luma v{InstallerEngine.ProductVersion} installed to {targetDir}";
            UpdateStepVisuals();
        }
        catch (OperationCanceledException)
        {
            InstallPhaseText.Text = "Installation cancelled. Any partial files were rolled back.";
            InstallProgressBar.Value = 0;
            InstallPercentText.Text = "0%";
            NextBtn.Content = "Retry Install";
            NextBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            InstallPhaseText.Text = "Installation could not complete.";
            InstallErrorText.Text = $"Error: {ex.Message}";
            InstallErrorText.Visibility = Visibility.Visible;
            NextBtn.Content = "Retry Install";
            NextBtn.IsEnabled = true;
        }
        finally
        {
            _isInstalling = false;
            InstallDirBox.IsEnabled = !_isInstalled;
            BrowseBtn.IsEnabled = !_isInstalled;
            BackBtn.IsEnabled = !_isInstalled && _currentStep > 1;
            CancelBtn.Content = "Cancel";
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_isInstalling)
        {
            _installCts?.Cancel();
            return;
        }
        Close();
    }

    private void UpdateStepVisuals()
    {
        Stage1Panel.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        Stage2Panel.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        Stage3Panel.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;

        InstallShortcutsCard.Visibility = _isInstalled ? Visibility.Collapsed : Visibility.Visible;
        FinishOptionsCard.Visibility = _isInstalled ? Visibility.Visible : Visibility.Collapsed;
        Stage3TitleText.Text = _isInstalled ? "Installation Complete" : "Install Project Luma";
        Stage3SubtitleText.Text = _isInstalled
            ? "Project Luma is ready to detect your games and manage enhancements."
            : "Choose your installation folder and launch Project Luma when ready.";

        BackBtn.Visibility = _currentStep > 1 && !_isInstalled ? Visibility.Visible : Visibility.Collapsed;
        CancelBtn.Visibility = _isInstalled ? Visibility.Collapsed : Visibility.Visible;

        NextBtn.Content = _currentStep switch
        {
            1 => "Install Project Luma",
            2 => "Continue",
            3 => _isInstalled ? "Finish" : "Install Now",
            _ => "Continue"
        };
        NextBtn.IsEnabled = _currentStep != 1 || AcceptLicenseCheck.IsChecked == true;

        (Border Badge, TextBlock Label)[] rails =
        {
            (StepBadge1, StepText1),
            (StepBadge2, StepText2),
            (StepBadge3, StepText3)
        };

        var activeBg = CreateBrush("#251618");
        var activeBorder = CreateBrush("#ED1C24");
        var activeFg = CreateBrush("#F6F6F6");
        var doneFg = CreateBrush("#55C78A");
        var mutedFg = CreateBrush("#8E8E8E");

        for (int i = 0; i < rails.Length; i++)
        {
            int stepNum = i + 1;
            if (stepNum == _currentStep && !(_isInstalled && stepNum == 3))
            {
                rails[i].Badge.Background = activeBg;
                rails[i].Badge.BorderBrush = activeBorder;
                rails[i].Label.Foreground = activeFg;
                rails[i].Label.FontWeight = FontWeights.SemiBold;
            }
            else if (stepNum < _currentStep || (_isInstalled && stepNum == 3))
            {
                rails[i].Badge.Background = stepNum == _currentStep ? activeBg : Brushes.Transparent;
                rails[i].Badge.BorderBrush = stepNum == _currentStep ? CreateBrush("#276B45") : Brushes.Transparent;
                rails[i].Label.Foreground = doneFg;
                rails[i].Label.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                rails[i].Badge.Background = Brushes.Transparent;
                rails[i].Badge.BorderBrush = Brushes.Transparent;
                rails[i].Label.Foreground = mutedFg;
                rails[i].Label.FontWeight = FontWeights.Medium;
            }
        }
    }

    public async Task CaptureAllScreensAsync(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        await RunCompatibilityScanAsync();

        string[] fileNames =
        {
            "Installer_Stage1_Welcome.png",
            "Installer_Stage2_SystemCheck.png",
            "Installer_Stage3_InstallFinish.png"
        };

        for (int step = 1; step <= 3; step++)
        {
            _currentStep = step;
            _isInstalled = step == 3;
            if (step == 3)
            {
                OnInstallDirChanged(InstallDirBox, null!);
                InstallProgressBar.Value = 100;
                InstallPercentText.Text = "100%";
                InstallPhaseText.Text = $"Project Luma v{InstallerEngine.ProductVersion} installed cleanly.";
                CompletePathSummary.Text = $"Project Luma v{InstallerEngine.ProductVersion} installed to {InstallDirBox.Text}";
            }

            UpdateStepVisuals();
            await RenderWindowToPngAsync(this, Path.Combine(outputDir, fileNames[step - 1]));
        }
    }

    internal static async Task RenderWindowToPngAsync(Window window, string outputPath)
    {
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (window.Content is FrameworkElement root)
        {
            root.Measure(new Size(window.Width, window.Height));
            root.Arrange(new Rect(0, 0, window.Width, window.Height));
            root.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            var rtb = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            await using var fs = File.Create(outputPath);
            encoder.Save(fs);
        }
    }

    private static SolidColorBrush CreateBrush(string hex)
        => new((Color)ColorConverter.ConvertFromString(hex));
}

public sealed class StatusToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value as string) switch
        {
            "Ready" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#132B1E")),
            "Optional improvement" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2B2010")),
            "Action required" or "Not supported" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D1416")),
            _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#202020"))
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class StatusToBorderConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value as string) switch
        {
            "Ready" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#276B45")),
            "Optional improvement" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8C6220")),
            "Action required" or "Not supported" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E252C")),
            _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#383838"))
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class StatusToForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value as string) switch
        {
            "Ready" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#55C78A")),
            "Optional improvement" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E7B45B")),
            "Action required" or "Not supported" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EA6666")),
            _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B8B8B8"))
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
