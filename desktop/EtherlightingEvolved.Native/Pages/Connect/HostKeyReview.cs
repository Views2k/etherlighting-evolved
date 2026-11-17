using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EtherlightingEvolved.Native.Theme;
using EtherlightingEvolved.Native.Ui;
using UiKit = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Connect;

/// <summary>
/// The review of a changed SSH key in the login form (div.host-key-review in preview/connections.mjs): the saved and
/// new fingerprints, styled like the controller certificate panel, and the buttons that accept the new key or keep
/// the saved one. It only shows a problem; ConnectPage sends the requests.
/// </summary>
public sealed class HostKeyReview : Border
{
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New, monospace");
    readonly StackPanel body = new() { Spacing = 12 };
    // The heading takes focus when a review opens, as the browser's h2 with tabindex -1 does.
    readonly TextBlock heading = new()
    {
        Text = "The switch’s SSH key changed", Classes = { "h2", "danger" }, LineHeight = 29, TextWrapping = TextWrapping.Wrap, Focusable = true, IsTabStop = false,
    };
    readonly TextBlock address = Text("", "danger");
    readonly TextBlock newKeyLabel = Text("");
    readonly SelectableTextBlock saved = Fingerprint();
    readonly SelectableTextBlock presented = Fingerprint();
    readonly TextBlock note = Text(Connections.HostKeyRemoteNote);
    readonly Button accept = UiKit.Button("Accept new key");
    readonly Button keep = UiKit.Button("Keep saved key", "quiet");

    public HostKeyReview(Action<HostKeyProblem> accept, Action<HostKeyProblem> keep)
    {
        this.accept.Click += (_, _) =>
        {
            if (Problem is { } problem) accept(problem);
        };
        this.keep.Click += (_, _) =>
        {
            if (Problem is { } problem) keep(problem);
        };
        Buttons = [this.accept, this.keep];
        body.Children.Add(heading);
        body.Children.Add(address);
        body.Children.Add(Text("Lighting, schedules and port names saved for the previous switch stay saved but are not used with the new key.", "danger"));
        body.Children.Add(Text("Saved key"));
        body.Children.Add(saved);
        body.Children.Add(newKeyLabel);
        body.Children.Add(presented);
        body.Children.Add(note);
        body.Children.Add(new FlowRow(10, this.accept, this.keep) { Classes = { "connection-actions" } });
        Padding = new Thickness(14, 12);
        CornerRadius = new CornerRadius(7);
        BorderThickness = new Thickness(1);
        Child = body;
        this.Bind(BackgroundProperty, "error-surface").Bind(BorderBrushProperty, "error-border");
        IsVisible = false;
    }

    public HostKeyProblem? Problem { get; private set; }

    /// <summary>True on the computer running EE, false elsewhere, null until private access answers.</summary>
    public bool? CanAccept { get; private set; }

    public IReadOnlyList<Button> Buttons { get; }

    public void Show(HostKeyProblem problem)
    {
        Problem = problem;
        address.Text = $"The device at {problem.Address} presented a different SSH key than the one EE saved. This happens after the switch is reset or replaced. If you did not reset or replace it, another device may be using its address. In that case, keep the saved key and check your network.";
        newKeyLabel.Text = problem.KeyType is { } type ? $"New key ({type})" : "New key";
        saved.Text = problem.SavedFingerprint;
        presented.Text = problem.Fingerprint;
        CanAccept = null;
        accept.IsVisible = note.IsVisible = false;
        IsVisible = true;
        FocusHeading();
    }

    /// <summary>Shows Accept new key on the computer running EE; elsewhere the note says where the key can be accepted.</summary>
    public void SetCanAccept(bool canAccept)
    {
        CanAccept = canAccept;
        accept.IsVisible = canAccept;
        note.IsVisible = !canAccept;
    }

    public void FocusHeading() => heading.Focus();

    public void Hide()
    {
        Problem = null;
        IsVisible = false;
    }

    static TextBlock Text(string text, string tone = "muted") =>
        new TextBlock { Text = text, FontSize = 13, LineHeight = 20, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, tone);

    internal static SelectableTextBlock Fingerprint(string value = "") =>
        new SelectableTextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap, FontFamily = Mono }.Bind(TextBlock.ForegroundProperty, "muted");
}
