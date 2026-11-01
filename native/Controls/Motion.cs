using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EtherlightingEvolved.Controls;

public static class Motion
{
    public static readonly DependencyProperty FadeToProperty = DependencyProperty.RegisterAttached(
        "FadeTo", typeof(double), typeof(Motion), new PropertyMetadata(0d, OnFadeTo));
    public static void SetFadeTo(DependencyObject target, double value) => target.SetValue(FadeToProperty, value);
    public static double GetFadeTo(DependencyObject target) => (double)target.GetValue(FadeToProperty);

    private static void OnFadeTo(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not UIElement element) return;
        double before = element.Opacity, value = Math.Clamp((double)args.NewValue, 0, 1);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = value;
        if (SystemParameters.ClientAreaAnimation && element.IsVisible)
            element.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(before, value, TimeSpan.FromMilliseconds(125))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnEnabled));
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);

    private static void OnEnabled(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is ButtonBase button && args.NewValue is true) Attach(button);
    }

    private static readonly DependencyProperty AttachedProperty = DependencyProperty.RegisterAttached(
        "Attached", typeof(bool), typeof(Motion), new PropertyMetadata(false));

    public static void Attach(ButtonBase button)
    {
        if ((bool)button.GetValue(AttachedProperty)) return;
        button.SetValue(AttachedProperty, true);
        var scale = new ScaleTransform(1, 1);
        button.RenderTransformOrigin = new Point(.5, .5);
        button.RenderTransform = scale;
        void Scale(double value)
        {
            if (!SystemParameters.ClientAreaAnimation) return;
            var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(value < 1 ? 75 : 140))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
        button.PreviewMouseLeftButtonDown += (_, _) => Scale(.99);
        button.PreviewMouseLeftButtonUp += (_, _) => Scale(1);
        button.LostMouseCapture += (_, _) => Scale(1);
        button.MouseLeave += (_, _) => Scale(1);
        button.PreviewKeyDown += (_, e) => { if (e.Key is Key.Space or Key.Enter) Scale(.99); };
        button.PreviewKeyUp += (_, _) => Scale(1);
    }

    public static void Enter(FrameworkElement element)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        var translation = new TranslateTransform();
        element.RenderTransform = translation;
        translation.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(220))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
