using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace EtherlightingEvolved.Native.Shell;

/// <summary>
/// The desktop window: 1200 × 800, at least 900 × 320, without the system title bar so the workspace title bar and
/// its window buttons take its place, as in the earlier desktop app. Closing hides EE to the notification area.
/// </summary>
public sealed class ShellWindow : Window
{
    public ShellWindow(AppShell shell)
    {
        Shell = shell;
        Title = "Etherlighting Evolved";
        ShowInTaskbar = !OperatingSystem.IsWindows();
        Width = 1200;
        Height = 800;
        MinWidth = 900;
        MinHeight = 320;
        Background = new SolidColorBrush(Color.Parse("#151618"));
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = -1;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://EtherlightingEvolved/Assets/ee.ico"))); }
        catch (Exception) { }
        Content = shell;
        shell.HideRequested += Hide;
        shell.ToggleMaximizeRequested += () => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        shell.DragRequested += BeginMoveDrag;
        KeyDown += async (_, e) =>
        {
            if (e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Left or Key.Right) || shell.Overlay.HasDialog) return;
            e.Handled = true;
            await shell.MoveHistory(e.Key == Key.Left ? -1 : 1);
        };
        // A tap on a touch screen counts as hovering that never ends, so a tooltip would stay up with no way to
        // dismiss it. Tooltips show only while a mouse or pen is in use.
        AddHandler(PointerPressedEvent, FollowPointerType, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, FollowPointerType, RoutingStrategies.Tunnel, handledEventsToo: true);
        ToolTip.IsOpenProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            if (touch && ToolTip.GetIsOpen(control) && TopLevel.GetTopLevel(control) == this) ToolTip.SetIsOpen(control, false);
        });
        PointerPressed += async (_, e) =>
        {
            if (shell.Overlay.HasDialog) return;
            var button = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            if (button is not (PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed)) return;
            e.Handled = true;
            await shell.MoveHistory(button == PointerUpdateKind.XButton1Pressed ? -1 : 1);
        };
    }

    public AppShell Shell { get; }

    bool touch;

    /// <summary>True while the most recent pointer input came from a touch screen.</summary>
    public bool TouchInput => touch;

    void FollowPointerType(object? sender, PointerEventArgs e)
    {
        var next = e.Pointer.Type == PointerType.Touch;
        if (next == touch) return;
        touch = next;
        ToolTip.SetServiceEnabled(this, !touch);
        if (!touch) return;
        foreach (var control in this.GetVisualDescendants().OfType<Control>())
            if (ToolTip.GetIsOpen(control)) ToolTip.SetIsOpen(control, false);
    }

    /// <summary>Closing the window keeps EE running in the notification area unless the app is quitting.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == IsVisibleProperty)
            _ = Shell.SetHidden(!IsVisible || WindowState == WindowState.Minimized);
    }
}
