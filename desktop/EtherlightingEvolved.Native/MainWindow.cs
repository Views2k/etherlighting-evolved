using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace EtherlightingEvolved.Native;

public sealed class MainWindow : Window
{
    readonly TextBlock heading = new() { Text = "Etherlighting Evolved", FontSize = 26, FontWeight = FontWeight.SemiBold };
    readonly TextBlock status = new() { Text = "Starting…", FontSize = 13, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly Border notice = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10), Margin = new Thickness(0, 0, 0, 14), IsVisible = false };
    readonly TextBlock noticeText = new() { TextWrapping = TextWrapping.Wrap };
    readonly StackPanel addressRow = new() { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false, Margin = new Thickness(0, 0, 0, 14) };
    readonly TextBox address = new() { Width = 340, Watermark = "http://127.0.0.1:4180/" };
    readonly Button open = new() { Content = "Open full controls", VerticalAlignment = VerticalAlignment.Center };
    WorkspaceClient? client;
    WorkspaceSnapshot? latest;
    List<int> selection = [];
    int? anchor;
    int posted;
    Action<Uri>? connect;

    public PortMap Map { get; } = new();

    public Inspector Controls { get; } = new();

    public WorkspaceApi? Api { get; private set; }

    public Uri? Workspace { get; private set; }

    public WorkspaceSnapshot? Current { get; private set; }

    /// <summary>Closing the window keeps EE running in the notification area unless the app is quitting.</summary>
    public bool AllowClose { get; set; }

    public MainWindow()
    {
        Title = "Etherlighting Evolved";
        Width = 1320;
        Height = 780;
        MinWidth = 760;
        MinHeight = 480;
        Background = Palette.Window;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://EtherlightingEvolved/Assets/ee.ico"))); }
        catch (Exception) { }
        status.Foreground = Palette.Waiting;
        notice.Child = noticeText;
        open.Click += async (_, _) =>
        {
            if (Workspace is null) return;
            try { await Launcher.LaunchUriAsync(Workspace); }
            catch (Exception) { ShowNotice($"Open {Workspace} in a browser for the full controls.", false); }
        };
        var connectButton = new Button { Content = "Connect" };
        connectButton.Click += (_, _) => SubmitAddress();
        address.KeyDown += (_, e) => { if (e.Key == Key.Enter) SubmitAddress(); };
        addressRow.Children.Add(new TextBlock { Text = "EE workspace address", VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.Muted });
        addressRow.Children.Add(address);
        addressRow.Children.Add(connectButton);

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(open, Dock.Right);
        header.Children.Add(open);
        header.Children.Add(new StackPanel { Children = { heading, status } });

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 14) };
        foreach (var (label, pick) in new (string, Func<IEnumerable<PortView>, IEnumerable<int>>)[]
                 {
                     ("All ports", ports => ports.Select(port => port.Port)),
                     ("Ethernet", ports => ports.Where(port => !port.Sfp).Select(port => port.Port)),
                     ("SFP", ports => ports.Where(port => port.Sfp).Select(port => port.Port)),
                     ("None", _ => []),
                 })
        {
            var button = new Button { Content = label };
            button.Click += (_, _) => Select(pick(Current?.Ports ?? []).ToList());
            toolbar.Children.Add(button);
        }

        var top = new StackPanel { Children = { header, notice, addressRow, toolbar } };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(Controls, Dock.Right);
        Controls.Margin = new Thickness(24, 0, 0, 0);
        var scroller = new ScrollViewer { Content = Map, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Content = new DockPanel { Margin = new Thickness(28, 22, 20, 22), Children = { top, Controls, scroller } };
        Map.PortPressed += OnPortPressed;
    }

    public void Attach(Uri workspace, WorkspaceClient next)
    {
        if (client is not null)
        {
            client.Changed -= OnChanged;
            _ = client.DisposeAsync().AsTask();
        }
        Api?.Dispose();
        Workspace = workspace;
        client = next;
        Api = new WorkspaceApi(workspace);
        Controls.Bind(Api);
        client.Changed += OnChanged;
        UpdatePaused();
        client.Start();
    }

    public void SetStatus(string text, bool live)
    {
        status.Text = text;
        status.Foreground = live ? Palette.Live : Palette.Waiting;
    }

    public void ShowNotice(string? text, bool error)
    {
        notice.IsVisible = !string.IsNullOrEmpty(text);
        noticeText.Text = text;
        notice.Background = error ? new SolidColorBrush(Color.Parse("#3a1f24")) : Palette.Panel;
        noticeText.Foreground = error ? Palette.Error : Palette.Text;
    }

    /// <summary>For a window that does not start EE itself: lets the user point it at a workspace.</summary>
    public void EnableAddressEditor(Uri current, Action<Uri> onConnect)
    {
        connect = onConnect;
        address.Text = current.ToString();
        addressRow.IsVisible = true;
    }

    void SubmitAddress()
    {
        if (Uri.TryCreate(address.Text?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") connect?.Invoke(uri);
        else ShowNotice("Enter the EE workspace address, for example http://127.0.0.1:4180/.", true);
    }

    public void Select(IReadOnlyList<int> ports)
    {
        selection = ports.Distinct().ToList();
        anchor = selection.Count > 0 ? selection[^1] : null;
        Map.SetSelection(selection);
        if (Current is not null) Controls.Update(Current, selection);
    }

    void OnPortPressed(int port, KeyModifiers modifiers)
    {
        var additive = modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);
        if (modifiers.HasFlag(KeyModifiers.Shift) && anchor is { } start)
        {
            var order = Map.PortOrder.ToList();
            int a = order.IndexOf(start), b = order.IndexOf(port);
            if (a >= 0 && b >= 0)
            {
                var range = order.GetRange(Math.Min(a, b), Math.Abs(a - b) + 1);
                selection = additive ? selection.Union(range).ToList() : range;
                Map.SetSelection(selection);
                if (Current is not null) Controls.Update(Current, selection);
                return;
            }
        }
        if (additive) selection = selection.Contains(port) ? selection.Where(item => item != port).ToList() : [.. selection, port];
        else selection = selection.Count == 1 && selection[0] == port ? [] : [port];
        anchor = port;
        Map.SetSelection(selection);
        if (Current is not null) Controls.Update(Current, selection);
    }

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
        if (change.Property == WindowStateProperty || change.Property == IsVisibleProperty) UpdatePaused();
    }

    // A hidden or minimized window shows nothing, so it stops following the live stream until it is shown again.
    void UpdatePaused() => client?.SetPaused(!IsVisible || WindowState == WindowState.Minimized);

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
        Current = snapshot;
        if (heading.Text != snapshot.Title) heading.Text = snapshot.Title;
        if (status.Text != snapshot.Status) status.Text = snapshot.Status;
        var tone = snapshot.Live ? Palette.Live : Palette.Waiting;
        if (!ReferenceEquals(status.Foreground, tone)) status.Foreground = tone;
        if (selection.Count > 0)
        {
            var present = snapshot.Ports.Select(port => port.Port).ToHashSet();
            if (selection.Any(port => !present.Contains(port)))
            {
                selection = selection.Where(present.Contains).ToList();
                Map.SetSelection(selection);
            }
        }
        Map.Update(snapshot.Ports);
        Controls.Update(snapshot, selection);
    }
}
