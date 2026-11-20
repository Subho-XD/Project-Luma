using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace ProjectLuma.UI.Views;

/// <summary>Optional, non-focus-stealing, click-through desktop launch overlay confirming active enhancements.</summary>
public partial class LumaLaunchOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public LumaLaunchOverlayWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, exStyle | WsExTransparent | WsExToolWindow | WsExNoActivate);
    }

    public void ConfigureContent(string gameTitle, string profileLabel, string activeEnhancementsSummary)
    {
        GameTitleText.Text = gameTitle;
        ProfileStatusText.Text = $"{profileLabel} PROFILE ACTIVE";
        EnhancementsSummaryText.Text = activeEnhancementsSummary;

        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 28;
        Top = area.Top + 28;
    }

    public async Task ShowNotificationAsync(string gameTitle, string profileLabel, string activeEnhancementsSummary, int displayMilliseconds = 3500)
    {
        ConfigureContent(gameTitle, profileLabel, activeEnhancementsSummary);
        Show();

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240));
        BeginAnimation(OpacityProperty, fadeIn);

        await Task.Delay(displayMilliseconds);

        var tcs = new TaskCompletionSource();
        var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(380));
        fadeOut.Completed += (_, _) => { try { Close(); } catch { } tcs.TrySetResult(); };
        BeginAnimation(OpacityProperty, fadeOut);
        await tcs.Task;
    }
}
