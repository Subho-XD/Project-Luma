using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectLuma.Core.Enums;

namespace ProjectLuma.UI.Converters;

public sealed class CapabilityStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool bg = string.Equals(parameter as string, "bg", StringComparison.OrdinalIgnoreCase);
        var state = value is CapabilityState s ? s : CapabilityState.Unsupported;

        string hex = (state, bg) switch
        {
            (CapabilityState.Supported, false) => "#55C78A",
            (CapabilityState.Supported, true) => "#112419",
            (CapabilityState.Experimental or CapabilityState.SupportedWithLimitations, false) => "#E7B45B",
            (CapabilityState.Experimental or CapabilityState.SupportedWithLimitations, true) => "#292010",
            (CapabilityState.RequiresDependency or CapabilityState.RequiresDriver or CapabilityState.RequiresGameIntegration, false) => "#E7B45B",
            (CapabilityState.RequiresDependency or CapabilityState.RequiresDriver or CapabilityState.RequiresGameIntegration, true) => "#292010",
            (_, false) => "#EA6666",
            (_, true) => "#2B1414"
        };

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class FriendlyStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool bg = string.Equals(parameter as string, "bg", StringComparison.OrdinalIgnoreCase);
        var status = value is FriendlyGameStatus s ? s : FriendlyGameStatus.ReadyToEnhance;

        string hex = (status, bg) switch
        {
            (FriendlyGameStatus.Enhanced, false) => "#FF5A60",
            (FriendlyGameStatus.Enhanced, true) => "#CC2B1012",
            (FriendlyGameStatus.ReadyToEnhance, false) => "#55C78A",
            (FriendlyGameStatus.ReadyToEnhance, true) => "#CC112419",
            (FriendlyGameStatus.SetupRequired, false) => "#E7B45B",
            (FriendlyGameStatus.SetupRequired, true) => "#CC292010",
            (FriendlyGameStatus.LimitedSupport, false) => "#B8B8B8",
            (FriendlyGameStatus.LimitedSupport, true) => "#CC202020",
            (_, false) => "#EA6666",
            (_, true) => "#CC2B1414"
        };

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SetupStepBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var st = value is SetupStepState s ? s : SetupStepState.Pending;
        string hex = st switch
        {
            SetupStepState.Complete => "#55C78A",
            SetupStepState.InProgress => "#ED1C24",
            SetupStepState.AttentionRequired => "#E7B45B",
            SetupStepState.Failed => "#EA6666",
            _ => "#6E6E6E"
        };
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class CapabilityStateLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is CapabilityState state
            ? state switch
            {
                CapabilityState.Supported => "Ready",
                CapabilityState.SupportedWithLimitations => "Available with Limits",
                CapabilityState.Experimental => "Experimental",
                CapabilityState.RequiresDependency => "One Setup Step Remaining",
                CapabilityState.RequiresDriver => "Driver Update Recommended",
                CapabilityState.RequiresGameIntegration => "Requires DirectX 12",
                _ => "Not Available"
            }
            : "Not Available";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class FilePathToBitmapConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, BitmapImage> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        int decodeWidth = int.TryParse(parameter as string, out int w) ? w : 320;
        string cacheKey = $"{path}|{decodeWidth}";

        if (Cache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.EndInit();
            bmp.Freeze();

            if (Cache.Count > 500) Cache.Clear();
            Cache[cacheKey] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
        bool b = value switch
        {
            bool flag => flag,
            int count => count > 0,
            string str => !string.IsNullOrWhiteSpace(str),
            null => false,
            _ => true
        };
        if (invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null && parameter != null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class StringEqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2) return false;
        string? a = values[0]?.ToString();
        string? b = values[1]?.ToString();
        return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => new[] { Binding.DoNothing, Binding.DoNothing };
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

public sealed class ObjectReferenceEqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2 || values[0] == null || values[1] == null)
            return false;
        return ReferenceEquals(values[0], values[1]) || Equals(values[0], values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => new[] { Binding.DoNothing, Binding.DoNothing };
}

public sealed class ComponentStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool bg = string.Equals(parameter as string, "bg", StringComparison.OrdinalIgnoreCase);
        var state = value is ComponentInstallState s ? s : ComponentInstallState.ReadyToInstall;

        string hex = (state, bg) switch
        {
            (ComponentInstallState.Installed, false) => "#55C78A",
            (ComponentInstallState.Installed, true) => "#112419",
            (ComponentInstallState.ReadyToInstall, false) => "#F6F6F6",
            (ComponentInstallState.ReadyToInstall, true) => "#202020",
            (ComponentInstallState.UpdateAvailable, false) => "#FF5A60",
            (ComponentInstallState.UpdateAvailable, true) => "#2B1012",
            (ComponentInstallState.Downloading or ComponentInstallState.Verifying or ComponentInstallState.Installing, false) => "#FF5A60",
            (ComponentInstallState.Downloading or ComponentInstallState.Verifying or ComponentInstallState.Installing, true) => "#2A1113",
            (ComponentInstallState.DriverManaged, false) => "#55C78A",
            (ComponentInstallState.DriverManaged, true) => "#112419",
            (ComponentInstallState.Experimental or ComponentInstallState.RequiresSetup or ComponentInstallState.ExternalAcquisitionRequired, false) => "#E7B45B",
            (ComponentInstallState.Experimental or ComponentInstallState.RequiresSetup or ComponentInstallState.ExternalAcquisitionRequired, true) => "#292010",
            (ComponentInstallState.HardwareIncompatible or ComponentInstallState.Failed, false) => "#EA6666",
            (ComponentInstallState.HardwareIncompatible or ComponentInstallState.Failed, true) => "#2B1414",
            (_, false) => "#B8B8B8",
            (_, true) => "#202020"
        };

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NeuralActivationBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool bg = string.Equals(parameter as string, "bg", StringComparison.OrdinalIgnoreCase);
        var state = value is NeuralRenderingActivationState s ? s : NeuralRenderingActivationState.Unsupported;

        string hex = (state, bg) switch
        {
            (NeuralRenderingActivationState.Active or NeuralRenderingActivationState.Available or NeuralRenderingActivationState.ReadyToConfigure, false) => "#55C78A",
            (NeuralRenderingActivationState.Active or NeuralRenderingActivationState.Available or NeuralRenderingActivationState.ReadyToConfigure, true) => "#112419",
            (NeuralRenderingActivationState.InstallationInProgress, false) => "#FF5A60",
            (NeuralRenderingActivationState.InstallationInProgress, true) => "#2A1113",
            (NeuralRenderingActivationState.DependencyMissing or NeuralRenderingActivationState.RuntimeMissing, false) => "#E7B45B",
            (NeuralRenderingActivationState.DependencyMissing or NeuralRenderingActivationState.RuntimeMissing, true) => "#292010",
            (_, false) => "#EA6666",
            (_, true) => "#2B1414"
        };

        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
