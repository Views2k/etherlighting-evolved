using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace EtherlightingEvolved.Native;

public sealed class MainWindow : Window
{
    readonly WorkspaceClient? client;
    readonly Uri workspace;
    readonly TextBlock heading = new() { Text = "Etherlighting Evolved", FontSize = 26, FontWeight = FontWeight.SemiBold };
    readonly TextBlock status = new() { Text = "Connecting to EE…", FontSize = 13, Margin = new Thickness(0, 4, 0, 0) };
    WorkspaceSnapshot? latest;
    int posted;

    public PortMap Map { get; } = new();

    public MainWindow(Uri workspace, WorkspaceClient? client)
    {
        this.workspace = workspace;
        this.client = client;
        Title = "Etherlighting Evolved";
        Width = 1200;
        Height = 660;
        MinWidth = 560;
        MinHeight = 380;
        Background = Palette.Window;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://EtherlightingEvolvedNative/Assets/ee.ico"))); }
        catch (Exception) { }
        status.Foreground = Palette.Waiting;

        var open = new Button { Content = "Open full controls", VerticalAlignment = VerticalAlignment.Center };
        open.Click += async (_, _) =>
        {
            try { await Launcher.LaunchUriAsync(workspace); }
            catch (Exception) { status.Text = $"Open {workspace} in a browser for the full controls."; }
        };
        var titles = new StackPanel { Children = { heading, status } };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 22) };
        DockPanel.SetDock(open, Dock.Right);
        header.Children.Add(open);
        header.Children.Add(titles);
        DockPanel.SetDock(header, Dock.Top);
        var scroller = new ScrollViewer { Content = Map, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Content = new DockPanel { Margin = new Thickness(28, 22, 28, 22), Children = { header, scroller } };
        if (client is not null) client.Changed += OnChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        client?.Start();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty) client?.SetPaused(WindowState == WindowState.Minimized);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (client is null) return;
        client.Changed -= OnChanged;
        _ = client.DisposeAsync().AsTask();
    }

    // Stream messages arrive on a background thread. Only the newest one is kept and applied once per UI turn.
    void OnChanged(WorkspaceSnapshot snapshot)
    {
        Volatile.Write(ref latest, snapshot);
        if (Interlocked.Exchange(ref posted, 1) == 0) Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
    }

    void Flush()
    {
        Interlocked.Exchange(ref posted, 0);
        if (Volatile.Read(ref latest) is { } snapshot) Apply(snapshot);
    }

    public void Apply(WorkspaceSnapshot snapshot)
    {
        if (heading.Text != snapshot.Title) heading.Text = snapshot.Title;
        if (status.Text != snapshot.Status) status.Text = snapshot.Status;
        var tone = snapshot.Live ? Palette.Live : Palette.Waiting;
        if (!ReferenceEquals(status.Foreground, tone)) status.Foreground = tone;
        Map.Update(snapshot.Ports);
    }
}
