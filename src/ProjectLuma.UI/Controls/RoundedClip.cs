using System.Windows;
using System.Windows.Media;

namespace ProjectLuma.UI.Controls;

/// <summary>
/// Applies an exact rounded-rectangle Clip geometry to any FrameworkElement (including Borders wrapping
/// full-bleed Images), preventing rectangular artwork or child surfaces from bleeding past CornerRadius.
/// </summary>
public static class RoundedClip
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius",
        typeof(CornerRadius),
        typeof(RoundedClip),
        new PropertyMetadata(new CornerRadius(0), OnCornerRadiusChanged));

    public static readonly DependencyProperty InsetProperty = DependencyProperty.RegisterAttached(
        "Inset",
        typeof(double),
        typeof(RoundedClip),
        new PropertyMetadata(0.5d, OnCornerRadiusChanged));

    public static CornerRadius GetCornerRadius(DependencyObject obj) => (CornerRadius)obj.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject obj, CornerRadius value) => obj.SetValue(CornerRadiusProperty, value);

    public static double GetInset(DependencyObject obj) => (double)obj.GetValue(InsetProperty);
    public static void SetInset(DependencyObject obj, double value) => obj.SetValue(InsetProperty, value);

    public static RectangleGeometry CreateClipGeometry(double width, double height, CornerRadius cornerRadius, double inset = 0.5)
    {
        if (width <= 0 || height <= 0 || double.IsNaN(width) || double.IsNaN(height))
        {
            var empty = new RectangleGeometry(Rect.Empty);
            empty.Freeze();
            return empty;
        }

        double safeInset = (inset > 0 && width > inset * 2 + 2 && height > inset * 2 + 2) ? inset : 0;
        double innerW = Math.Max(0, width - safeInset * 2);
        double innerH = Math.Max(0, height - safeInset * 2);
        double maxRadius = Math.Max(0, Math.Min(cornerRadius.TopLeft - safeInset, Math.Min(innerW / 2.0, innerH / 2.0)));

        var geo = new RectangleGeometry(new Rect(safeInset, safeInset, innerW, innerH), maxRadius, maxRadius);
        geo.Freeze();
        return geo;
    }

    private static void OnCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;

        el.SizeChanged -= OnElementSizeOrLoadChanged;
        el.Loaded -= OnElementSizeOrLoadChanged;

        if (GetCornerRadius(el).TopLeft > 0)
        {
            el.SizeChanged += OnElementSizeOrLoadChanged;
            el.Loaded += OnElementSizeOrLoadChanged;
            ApplyClip(el);
        }
        else
        {
            el.Clip = null;
        }
    }

    private static void OnElementSizeOrLoadChanged(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el)
            ApplyClip(el);
    }

    private static void ApplyClip(FrameworkElement el)
    {
        var cr = GetCornerRadius(el);
        double inset = GetInset(el);
        el.Clip = cr.TopLeft > 0
            ? CreateClipGeometry(el.ActualWidth, el.ActualHeight, cr, inset)
            : null;
    }
}
