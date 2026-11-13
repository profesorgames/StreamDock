using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace StreamDock;
public sealed class SmoothProgress : ProgressBar
{
    public static readonly DependencyProperty AnimatedValueProperty = DependencyProperty.Register(nameof(AnimatedValue), typeof(double), typeof(SmoothProgress), new PropertyMetadata(0d, AnimatedValueChanged));
    public double AnimatedValue { get => (double)GetValue(AnimatedValueProperty); set => SetValue(AnimatedValueProperty, value); }
    private static void AnimatedValueChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var bar = (SmoothProgress)target; var value = Math.Clamp((double)e.NewValue, 0, 100);
        if (SystemParameters.ClientAreaAnimation) bar.BeginAnimation(ValueProperty, new DoubleAnimation(bar.Value, value, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else { bar.BeginAnimation(ValueProperty, null); bar.Value = value; }
    }
}
