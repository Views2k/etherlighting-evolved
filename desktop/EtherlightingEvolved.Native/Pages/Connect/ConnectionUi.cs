using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Theme;
using EtherlightingEvolved.Native.Ui;
using UiKit = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Connect;

/// <summary>
/// Builders for the markup the connection views share (preview/connections.mjs: input, field, button), with the
/// sizes and colors from the connection rules in preview/style.css. The notice, state line, secret field and other
/// controls that Settings also uses are in Ui/FormControls.cs.
/// </summary>
public static class ConnectionUi
{
    /// <summary>
    /// Line heights from style.css at 13px: labels 18px, body 1.45, paragraphs 1.5, help text 1.55. Layout rounds
    /// each text block up to whole pixels, so the fractional heights are rounded down here to keep the same totals.
    /// </summary>
    public const double LabelLine = 18, BodyLine = 18.85, ParagraphLine = 19.5, HelpLine = 20;

    /// <summary>A text input (input()). Spell checking and capitalization are off, as in the browser workspace.</summary>
    public static TextBox Input(int maxLength = 0, string? placeholder = null, bool password = false)
    {
        var box = new TextBox { MaxLength = maxLength, Watermark = placeholder, AcceptsReturn = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (password) box.PasswordChar = '•';
        return box;
    }

    /// <summary>The .field wrapper: a muted label, the control, and an optional faint help line (field()).</summary>
    public static StackPanel Field(string label, Control control, string? help = null)
    {
        var field = new StackPanel { Spacing = 7, Classes = { "field" } };
        field.Children.Add(FieldLabel(label));
        field.Children.Add(control);
        if (help is not null) field.Children.Add(Small(help));
        return field;
    }

    public static TextBlock FieldLabel(string label) =>
        new TextBlock { Text = label, FontSize = 13, LineHeight = LabelLine, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "muted");

    /// <summary>A &lt;small&gt; inside a field: 13px in the faint color.</summary>
    public static TextBlock Small(string text = "") =>
        new TextBlock { Text = text, FontSize = 13, LineHeight = BodyLine, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "faint");

    /// <summary>The .help-text paragraph: 13px muted text at 1.55 line height, 5px below the previous block.</summary>
    public static TextBlock HelpText(string text = "") =>
        new TextBlock
            {
                Text = text, FontSize = 13, LineHeight = HelpLine, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0), MaxWidth = 64 * 7.3,
                HorizontalAlignment = HorizontalAlignment.Left,
            }
            .Bind(TextBlock.ForegroundProperty, "muted");

    /// <summary>A workspace button (button()): "primary", "quiet" or the default style.</summary>
    public static Button Button(string text, string? classes = null) => UiKit.Button(text, classes);

    /// <summary>The h2 used in connection headings: 20px semibold in the accent color.</summary>
    public static TextBlock Heading(string text) =>
        new TextBlock { Text = text, Classes = { "h2" }, LineHeight = 29, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "accent");

    public static Image Logo(double size)
    {
        var logo = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://EtherlightingEvolved/Assets/logo.png"));
            logo.Source = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, (int)(size * 2), Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        }
        catch (Exception) { }
        return logo;
    }

    /// <summary>Opens an address in the default browser, like a link with target _blank.</summary>
    public static async Task OpenLink(Visual owner, string url)
    {
        try
        {
            if (TopLevel.GetTopLevel(owner)?.Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception) { }
    }
}

/// <summary>
/// A wrapping row with a fixed gap between items, as the flex rows .connection-actions (10px) and inline buttons
/// (no gap) do. Hidden items take no space and add no gap. Items are centered in their line.
/// </summary>
public sealed class FlowRow : Panel
{
    public FlowRow(double gap = 10, params Control[] children)
    {
        Gap = gap;
        foreach (var child in children) Children.Add(child);
    }

    public double Gap { get; set; }

    IEnumerable<List<Control>> Lines(double width)
    {
        var line = new List<Control>();
        double x = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            var size = child.DesiredSize;
            if (line.Count > 0 && x + Gap + size.Width > width + 0.5)
            {
                yield return line;
                line = [];
                x = 0;
            }
            x += (line.Count > 0 ? Gap : 0) + size.Width;
            line.Add(child);
        }
        if (line.Count > 0) yield return line;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        double width = 0, height = 0;
        var first = true;
        foreach (var line in Lines(availableSize.Width))
        {
            width = Math.Max(width, line.Sum(child => child.DesiredSize.Width) + Gap * (line.Count - 1));
            height += (first ? 0 : Gap) + line.Max(child => child.DesiredSize.Height);
            first = false;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        var first = true;
        foreach (var line in Lines(finalSize.Width))
        {
            if (!first) y += Gap;
            first = false;
            var height = line.Max(child => child.DesiredSize.Height);
            double x = 0;
            foreach (var child in line)
            {
                var size = child.DesiredSize;
                child.Arrange(new Rect(x, y + (height - size.Height) / 2, size.Width, size.Height));
                x += size.Width + Gap;
            }
            y += height;
        }
        foreach (var child in Children)
            if (!child.IsVisible) child.Arrange(default);
        return finalSize;
    }
}

/// <summary>
/// Port of confirmAction (preview/confirmation.mjs): a sheet with the title, a muted message, and Cancel and the
/// accept action. One confirmation per owner at a time; asking again with the same text returns the open one.
/// </summary>
public static class Confirmation
{
    sealed record Pending(string Title, string Message, string Accept, CancellationToken Signal, Task<bool> Promise, Dialog Dialog);

    static readonly ConditionalWeakTable<object, Pending> Open = new();

    public static Task<bool> ConfirmAction(Overlay overlay, object owner, string title, string message, string accept, CancellationToken signal = default)
    {
        if (signal.IsCancellationRequested) return Task.FromResult(false);
        // A closed dialog no longer counts, even before its result is handed back.
        if (Open.TryGetValue(owner, out var pending) && pending.Dialog.IsOpen)
            return pending.Title == title && pending.Message == message && pending.Accept == accept && pending.Signal == signal
                ? pending.Promise : Task.FromResult(false);
        var previousFocus = TopLevel.GetTopLevel(overlay)?.FocusManager?.GetFocusedElement() as Control;
        var heading = new TextBlock { Text = title, Classes = { "h2" }, FontSize = 18, LineHeight = 26, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        var copy = new TextBlock { Text = message, FontSize = 13, LineHeight = ConnectionUi.ParagraphLine, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "muted");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var actions = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 14), Margin = new Thickness(0, 16, 0, 0), Child = buttons }
            .Bind(Border.BorderBrushProperty, "line");
        var sheet = new StackPanel { Spacing = 6, Children = { heading, copy, actions } };
        var dialog = overlay.Show(sheet, sheet: true);
        var cancel = ConnectionUi.Button("Cancel");
        var confirm = ConnectionUi.Button(accept, "primary");
        cancel.Click += (_, _) => dialog.Close("false");
        confirm.Click += (_, _) => dialog.Close("true");
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        var registration = signal.Register(() => Dispatcher.UIThread.Post(() => dialog.Close("false")));
        Pending? entry = null;
        var promise = Finish();
        entry = new Pending(title, message, accept, signal, promise, dialog);
        Open.AddOrUpdate(owner, entry);
        return promise;

        async Task<bool> Finish()
        {
            var result = await dialog.Result;
            if (Open.TryGetValue(owner, out var latest) && ReferenceEquals(latest, entry)) Open.Remove(owner);
            registration.Dispose();
            if (owner is not Visual visual || Attached(visual))
                if (previousFocus is { IsEffectivelyEnabled: true } && Attached(previousFocus)) previousFocus.Focus();
            return result == "true";
        }
    }

    static bool Attached(Visual visual) => TopLevel.GetTopLevel(visual) is not null;
}
