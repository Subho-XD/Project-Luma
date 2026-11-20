using System.Windows;
using System.Windows.Controls;
using ProjectLuma.Core.Enums;

namespace ProjectLuma.UI.Controls;

public partial class CapabilityBadge : UserControl
{
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(CapabilityState), typeof(CapabilityBadge),
            new PropertyMetadata(CapabilityState.Unsupported, OnStateChanged));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(CapabilityBadge),
            new PropertyMetadata("Unsupported"));

    public CapabilityBadge()
    {
        InitializeComponent();
    }

    public CapabilityState State
    {
        get => (CapabilityState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CapabilityBadge badge && string.IsNullOrWhiteSpace(badge.Label))
        {
            badge.Label = e.NewValue?.ToString() ?? "Unsupported";
        }
    }
}
