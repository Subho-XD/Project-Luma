using System.Windows;
using System.Windows.Controls;

namespace ProjectLuma.UI.Views;

public partial class HomeView : UserControl
{
    public static readonly DependencyProperty ThumbHeightProperty =
        DependencyProperty.Register(nameof(ThumbHeight), typeof(double), typeof(HomeView), new PropertyMetadata(156d));

    public double ThumbHeight
    {
        get => (double)GetValue(ThumbHeightProperty);
        set => SetValue(ThumbHeightProperty, value);
    }

    public HomeView() => InitializeComponent();

    private void OnHomeContainerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = e.NewSize.Width;
        if (w <= 0) return;

        // Scale hero & 16:9 thumbnails cleanly so at 1280x720 the Hero + Resume my games strip fit without clipping,
        // while at 1080p/1440p the panoramic Hero dominates cleanly (~420px).
        if (w < 1020)
        {
            HeroBannerContainer.Height = 280;
            ThumbHeight = 112;
        }
        else if (w < 1260)
        {
            HeroBannerContainer.Height = 336;
            ThumbHeight = 136;
        }
        else
        {
            HeroBannerContainer.Height = 420;
            ThumbHeight = 160;
        }
    }
}
