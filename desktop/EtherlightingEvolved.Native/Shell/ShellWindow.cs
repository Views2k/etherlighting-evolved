using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace EtherlightingEvolved.Native.Shell;

/// <summary>
/// The desktop window: 1200 × 800, at least 900 × 600, without the system title bar so the workspace title bar and
/// its window buttons take its place, as in the earlier desktop app. Closing hides EE to the notification area.
/// </summary>
public sealed class ShellWindow : Window
{
    public ShellWindow(AppShell shell)
    {
        Shell = shell;
        Title = "Etherlighting Evolved";
        Width = 1200;
        Height = 800;
        MinWidth = 900;
        MinHeight = 600;
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
    }

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
