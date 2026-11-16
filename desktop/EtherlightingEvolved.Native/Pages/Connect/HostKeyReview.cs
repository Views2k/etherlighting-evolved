using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EtherlightingEvolved.Native.Theme;
using EtherlightingEvolved.Native.Ui;
using UiKit = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Connect;

/// <summary>
/// The SSH key review in the login form (div.host-key-review in preview/connections.mjs): the fingerprints of the key a
/// switch presented, styled like the controller certificate panel, and the buttons that accept or dismiss it. It only
/// shows a problem; ConnectPage sends the requests.
/// </summary>
public sealed class HostKeyReview : Border
{
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New, monospace");
    readonly StackPanel body = new() { Spacing = 12 };
    // The heading takes focus when a review opens, as the browser's h2 with tabindex -1 does.
    readonly TextBlock heading = new() { Classes = { "h2" }, LineHeight = 29, TextWrapping = TextWrapping.Wrap, Focusable = true, IsTabStop = false };
    readonly TextBlock note = Text("");
    readonly Button accept = UiKit.Button("Accept key", "primary");
    readonly Button dismiss = UiKit.Button("Cancel", "quiet");
    readonly FlowRow actions;

    public HostKeyReview(Action<HostKeyProblem> accept, Action<HostKeyProblem> dismiss)
    {
        this.accept.Click += (_, _) =>
        {
            if (Problem is { } problem) accept(problem);
        };
        this.dismiss.Click += (_, _) =>
        {
            if (Problem is { } problem) dismiss(problem);
        };
        actions = new FlowRow(10, this.accept, this.dismiss) { Classes = { "connection-actions" } };
        Buttons = [this.accept, this.dismiss];
        Padding = new Thickness(14, 12);
        CornerRadius = new CornerRadius(7);
        BorderThickness = new Thickness(1);
        Child = body;
        IsVisible = false;
    }

    public HostKeyProblem? Problem { get; private set; }

    /// <summary>True on the computer running EE, false elsewhere, null until private access answers.</summary>
    public bool? CanAccept { get; private set; }

    public IReadOnlyList<Button> Buttons { get; }

    public void Show(HostKeyProblem problem)
    {
        Problem = problem;
        var tone = problem.Changed ? "danger" : "muted";
        var type = problem.KeyType is { } name ? $" ({name})" : "";
        heading.Text = problem.Changed ? "The switch’s SSH key changed" : "Check the switch’s SSH key";
        heading.Classes.Set("danger", problem.Changed);
        body.Children.Clear();
        body.Children.Add(heading);
        if (problem.Changed)
        {
            body.Children.Add(Text($"The device at {problem.Address} presented a different SSH key than the one EE saved. This happens after the switch is reset or replaced. If you did not reset or replace it, another device may be using its address. In that case, keep the saved key and check your network.", tone));
            body.Children.Add(Text("Lighting, schedules and port names saved for the previous switch stay saved but are not used with the new key.", tone));
            body.Children.Add(Text("Saved key"));
            body.Children.Add(Fingerprint(problem.SavedFingerprint ?? ""));
            body.Children.Add(Text($"New key{type}"));
            body.Children.Add(Fingerprint(problem.Fingerprint));
        }
        else
        {
            body.Children.Add(Text($"EE has no saved SSH key for {problem.Address}. Check that this fingerprint belongs to your switch. EE sends your password only after you accept the key."));
            body.Children.Add(Text($"Fingerprint{type}"));
            body.Children.Add(Fingerprint(problem.Fingerprint));
            body.Children.Add(Text("An SSH client that has connected to this switch shows the same fingerprint for the same key type."));
        }
        note.Text = Connections.HostKeyRemoteNote(problem);
        body.Children.Add(note);
        body.Children.Add(actions);
        accept.Content = problem.Changed ? "Accept new key" : "Accept key";
        accept.Classes.Set("primary", !problem.Changed);
        dismiss.Content = problem.Changed ? "Keep saved key" : "Cancel";
        this.Bind(BackgroundProperty, problem.Changed ? "error-surface" : "notice-surface").Bind(BorderBrushProperty, problem.Changed ? "error-border" : "line");
        CanAccept = null;
        accept.IsVisible = note.IsVisible = false;
        IsVisible = true;
        FocusHeading();
    }

    /// <summary>Shows Accept on the computer running EE; elsewhere the note says where the key can be accepted.</summary>
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

    static SelectableTextBlock Fingerprint(string value) =>
        new SelectableTextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap, FontFamily = Mono }.Bind(TextBlock.ForegroundProperty, "muted");
}
