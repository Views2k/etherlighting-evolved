using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Theme;

namespace EtherlightingEvolved.Native.Ui;

/// <summary>An open modal dialog. Close it with a result; Escape closes it with "cancel" unless prevented.</summary>
public sealed class Dialog
{
    readonly TaskCompletionSource<string> done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Action<Dialog>? Remove;

    internal Dialog(Control content, Border frame) => (Content, Frame) = (content, frame);

    public Control Content { get; }

    public Border Frame { get; }

    public Task<string> Result => done.Task;

    public bool IsOpen => !done.Task.IsCompleted;

    /// <summary>Return false to keep the dialog open when Escape is pressed (the browser "cancel" event).</summary>
    public Func<bool>? Canceling { get; set; }

    public void Close(string result = "")
    {
        if (!IsOpen) return;
        Remove?.Invoke(this);
        done.TrySetResult(result);
    }
}

/// <summary>
/// The layer above the workspace for dialogs (with the dimmed, blurred backdrop) and the toast, matching the
/// browser workspace's &lt;dialog&gt; and .toast styles.
/// </summary>
public sealed class Overlay : Panel
{
    readonly List<(Dialog Dialog, Control Layer)> dialogs = [];
    readonly Border toast = new() { IsVisible = false, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12), MaxWidth = 580 };
    readonly TextBlock toastText = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 19, VerticalAlignment = VerticalAlignment.Center };
    readonly Button toastDismiss;
    DispatcherTimer? toastTimer;

    public Overlay()
    {
        ClipToBounds = false;
        toastDismiss = Ui.Button("Dismiss", "quiet", () => toast.IsVisible = false);
        toastDismiss.Margin = new Thickness(12, -6, -8, -6);
        var row = new DockPanel();
        DockPanel.SetDock(toastDismiss, Dock.Right);
        row.Children.Add(toastDismiss);
        row.Children.Add(toastText);
        toast.Child = row;
        toast.HorizontalAlignment = HorizontalAlignment.Center;
        toast.VerticalAlignment = VerticalAlignment.Bottom;
        toast.Margin = new Thickness(16, 0, 16, 43);
        toast.Bind(Border.BackgroundProperty, "toast").Bind(Border.BoxShadowProperty, "shadow");
        Children.Add(toast);
    }

    public bool HasDialog => dialogs.Count > 0;

    /// <summary>Shows a toast. Errors stay until dismissed; other messages close after 2.5 seconds.</summary>
    public void Toast(string message, bool error)
    {
        toastText.Text = message;
        toastText.Bind(TextBlock.ForegroundProperty, error ? "toast-error-text" : "text");
        toast.Bind(Border.BorderBrushProperty, error ? "toast-error-border" : "toast-border");
        toastDismiss.IsVisible = error;
        toast.IsVisible = true;
        Enter(toast, 5, 180);
        toastTimer?.Stop();
        if (error) return;
        toastTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(2500), DispatcherPriority.Background, (_, _) => { toastTimer?.Stop(); toast.IsVisible = false; });
        toastTimer.Start();
    }

    /// <summary>Opens a modal dialog with the browser workspace's dialog frame (580px wide at most unless given).</summary>
    public Dialog Show(Control content, double? width = null, double padding = 20, bool sheet = false)
    {
        var frame = new Border
        {
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(sheet ? 24 : padding),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = sheet ? 440 : width ?? 580, Margin = new Thickness(16, 24),
            Child = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        };
        if (width is not null || sheet) frame.Width = sheet ? 440 : width!.Value;
        frame.Bind(Border.BackgroundProperty, "dialog").Bind(Border.BorderBrushProperty, "line-strong").Bind(Border.BoxShadowProperty, "shadow");
        TextElementForeground(frame, "text");
        var backdrop = new Border { Background = Brushes.Transparent };
        backdrop.Bind(Border.BackgroundProperty, "backdrop");
        var layer = new Panel { Children = { backdrop, frame } };
        var dialog = new Dialog(content, frame);
        dialog.Remove = item =>
        {
            var index = dialogs.FindIndex(entry => entry.Dialog == item);
            if (index < 0) return;
            Children.Remove(dialogs[index].Layer);
            dialogs.RemoveAt(index);
        };
        dialogs.Add((dialog, layer));
        Children.Insert(Children.Count - 1, layer);
        Enter(frame, 4, 200);
        Dispatcher.UIThread.Post(() => FirstFocusable(content)?.Focus(), DispatcherPriority.Loaded);
        return dialog;
    }

    static void TextElementForeground(Control control, string token) =>
        control[!Avalonia.Controls.Documents.TextElement.ForegroundProperty] = Look.Res(token);

    static Control? FirstFocusable(Control root)
    {
        foreach (var child in root.GetVisualDescendantsSafe())
            if (child is InputElement { Focusable: true, IsEffectivelyEnabled: true, IsEffectivelyVisible: true } element and (Button or TextBox or ComboBox or CheckBox))
                return (Control)element;
        return null;
    }

    /// <summary>Escape cancels the top dialog.</summary>
    public bool HandleEscape()
    {
        if (dialogs.Count == 0) return false;
        var top = dialogs[^1].Dialog;
        if (top.Canceling?.Invoke() != false) top.Close("cancel");
        return true;
    }

    /// <summary>Closes every dialog, as resetWorkspace does with the result "stay".</summary>
    public void CloseAll(string result = "stay")
    {
        foreach (var (dialog, _) in dialogs.ToList()) dialog.Close(result);
    }

    /// <summary>The entrance used for views, popovers and toasts: fade in while rising a few pixels.</summary>
    public static void Enter(Control control, double rise = 4, double milliseconds = 200)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(milliseconds), Easing = Look.Ease, FillMode = FillMode.None,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.15), new Setter(TranslateTransform.YProperty, rise) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0), new Setter(TranslateTransform.YProperty, 0.0) } },
            },
        };
        control.RenderTransform ??= new TranslateTransform();
        _ = animation.RunAsync(control);
    }

    /// <summary>
    /// Port of confirmAction: a sheet with a title, a muted message, Cancel and the accept action (primary).
    /// True when accepted.
    /// </summary>
    public async Task<bool> Confirm(string title, string message, string accept)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var sheet = new StackPanel { Children = { Ui.Text(title, "h2", wrap: true), Ui.P(message, "muted"), actions } };
        sheet.Children[0].Margin = new Thickness(0, 0, 0, 16);
        ((TextBlock)sheet.Children[0]).FontSize = 18;
        var dialog = Show(sheet, sheet: true);
        actions.Children.Add(Ui.Button("Cancel", null, () => dialog.Close("false")));
        actions.Children.Add(Ui.Button(accept, "primary", () => dialog.Close("true")));
        return await dialog.Result == "true";
    }
}

static class VisualSafe
{
    public static IEnumerable<Visual> GetVisualDescendantsSafe(this Visual root)
    {
        foreach (var child in Avalonia.VisualTree.VisualExtensions.GetVisualChildren(root))
        {
            yield return child;
            foreach (var nested in child.GetVisualDescendantsSafe()) yield return nested;
        }
    }
}
