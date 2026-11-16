using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;

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
        PointerPressed += async (_, e) =>
        {
            if (shell.Overlay.HasDialog) return;
            var button = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            if (button is not (PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed)) return;
            e.Handled = true;
            await shell.MoveHistory(button == PointerUpdateKind.XButton1Pressed ? -1 : 1);
        };
        // A rack window is used by touch, often through a stream that turns each tap into a mouse click and leaves
        // the pointer where it tapped, so a tooltip would open there and stay. Rack-sized windows show no tooltips.
        // This follows the window size only; pointer input does no extra work.
        PropertyChanged += (_, e) =>
        {
            if (e.Property != ClientSizeProperty) return;
            var tips = ClientSize.Height >= RackHeight;
            if (ToolTip.GetServiceEnabled(this) != tips) ToolTip.SetServiceEnabled(this, tips);
        };
    }

    /// <summary>Windows shorter than this are treated as rack windows.</summary>
    public const double RackHeight = 520;

    public AppShell Shell { get; }

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
