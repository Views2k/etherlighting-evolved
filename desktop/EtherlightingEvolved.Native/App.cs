using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Shell;
using EtherlightingEvolved.Native.Theme;

namespace EtherlightingEvolved.Native;

public sealed record Options(bool Background, bool Demo, Uri? Url, bool Quit)
{
    /// <summary>
    /// --shot DIR --page NAME [--theme ID] [--size 1440x900]: save a screenshot of one page from a window off screen,
    /// keeping the run's data under DIR (development checks).
    /// </summary>
    public string? Shot { get; init; }

    public string ShotPage { get; init; } = "Lighting";

    public string? ShotTheme { get; init; }

    public PixelSize ShotSize { get; init; } = new(1440, 900);

    /// <summary>A --url value the address dialog would refuse. The window then says so instead of opening a workspace.</summary>
    public string? RejectedUrl { get; init; }

    /// <summary>--after-update: setup started this copy after an automatic update, so the window opens where it was.</summary>
    public bool AfterUpdate { get; init; }

    /// <summary>
    /// Reads the command line. --url takes precedence over EE_WORKSPACE_URL; an environment value that is not a
    /// workspace address is ignored, since it may be left over from another setup.
    /// </summary>
    public static Options Parse(string[] args)
    {
        string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var requested = Arg("--url");
        var url = requested is not null ? WorkspaceAddress.Parse(requested) : WorkspaceAddress.Parse(Environment.GetEnvironmentVariable("EE_WORKSPACE_URL"));
        var rejected = requested is not null && url is null ? requested : null;
        var shot = Arg("--shot") is { } folder ? Path.GetFullPath(folder) : null;
        var size = Arg("--size")?.Split('x') is [var w, var h] && int.TryParse(w, out var width) && int.TryParse(h, out var height) ? new PixelSize(width, height) : new PixelSize(1440, 900);
        return new Options(args.Contains("--background"), args.Contains("--demo") || (shot is not null && requested is null && url is null), url, args.Contains("--quit"))
        {
            Shot = shot, ShotPage = Arg("--page") ?? "Lighting", ShotTheme = Arg("--theme"), ShotSize = size, RejectedUrl = rejected,
            AfterUpdate = args.Contains("--after-update"),
        };
    }
}

public sealed class App : Application
{
    /// <summary>The program version (major.minor.patch).</summary>
    public static string Version { get; } = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";

    public static Options Options { get; set; } = new(false, false, null, false);
    internal static SingleInstance? Instance { get; set; }
    internal static string DataDirectory { get; set; } = Locations.Workspace(false);
    AppHost? host;

    public override void Initialize()
    {
        Name = "Etherlighting Evolved";
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://EtherlightingEvolved/"))
        {
            Source = new Uri("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml"),
        });
        Styles.Add(Look.Styles());
        Look.Apply(this, Preferences.GetItem(Pages.Settings.ThemeSettings.StorageKey) ?? ThemeTokens.DefaultTheme);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Instance is not null)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            host = new AppHost(this, desktop, Options, Instance, DataDirectory);
            host.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>
/// The desktop program starts its bundled workspace, keeps the window available from the tray,
/// and asks the service to finish its current lighting session on exit.
/// </summary>
sealed partial class AppHost(Application app, IClassicDesktopStyleApplicationLifetime desktop, Options options, SingleInstance instance, string dataDirectory)
{
    ShellWindow? window;
    Workspace? workspace;
    ServiceHost? service;
    TrayIcon? tray;
    NativeMenu? menu;
    NativeMenuItem? browserItem;
    bool quitting;
    Task? quitTask;
    readonly Updates? updates = Updates.ForInstalledCopy(options);
    WindowPlacement? restorePlacement;
    string? updateProblem;
    DateTime lastInput = DateTime.UtcNow;
    bool shownOnce;

    public void Start()
    {
        // An error that escapes an input handler is shown instead of closing EE, which would lose an unapplied
        // preview and leave the window in the tray without its service.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            DesktopLog.Error("unhandled_error", e.Exception);
            try { window?.Shell.Notify(e.Exception.Message is { Length: > 0 } message ? message : "Could not confirm the change. Try again.", true); }
            catch (Exception) { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            DesktopLog.Error("unobserved_task_error", e.Exception);
            e.SetObserved();
        };
        desktop.ShutdownRequested += OnShutdownRequested;
        instance.Listen(command => Dispatcher.UIThread.InvokeAsync(() => HandleAsync(command)));
        if (options.Shot is null) CreateTray();
        if (updates is not null)
        {
            (updateProblem, restorePlacement) = updates.Resume(options.AfterUpdate);
            updates.Start(QuietForUpdate, Placement);
        }
        _ = ConnectAsync();
    }

    /// <summary>Nobody has used the window for ten minutes, no dialog is open and no change is being sent.</summary>
    bool QuietForUpdate() => !quitting && window is { } shown && DateTime.UtcNow - lastInput >= Updates.Quiet && !shown.Shell.Overlay.HasDialog
        && shown.Shell.Workspace.Busy == 0;

    /// <summary>Where the window is now, so the updated copy opens it in the same place.</summary>
    WindowPlacement? Placement()
    {
        if (window is null) return null;
        var hidden = !window.IsVisible || window.WindowState == WindowState.Minimized;
        // A window that was never shown or placed has no position of its own yet, and a minimized one reports none.
        if (!shownOnce || window.WindowState == WindowState.Minimized || WindowBounds.Read(window) is not { } bounds) return new WindowPlacement(hidden, false);
        return new WindowPlacement(hidden, window.WindowState == WindowState.Maximized, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    async Task ConnectAsync()
    {
        if (options.RejectedUrl is { } rejected)
        {
            if (options.Shot is not null) { desktop.Shutdown(1); return; }
            // The bundled service is not started in its place: the window says what was refused and lets the user
            // choose another address.
            Open(Locations.SavedWorkspace ?? DefaultAddress);
            window!.Shell.ShowConnectionProblem($"EE cannot open {rejected}. {AppShell.AddressHelp}");
            return;
        }
        Uri address;
        try
        {
            if (options.Url is { } url) address = url;
            else if (ServiceHost.Locate(AppContext.BaseDirectory, options.Demo, dataDirectory) is { } host)
            {
                address = await StartServiceAsync(host);
                AddBrowserItem();
            }
            else address = Locations.SavedWorkspace ?? DefaultAddress;
        }
        catch (Exception)
        {
            if (quitting) return;
            if (options.Shot is not null) { desktop.Shutdown(1); return; }
            if (window is null) Open(new Uri("http://127.0.0.1:9/"));
            window!.Shell.ShowServiceFailure(StartFailure, RetryServiceAsync);
            return;
        }
        if (quitting) return;
        Open(address);
        await window!.Shell.Start();
        if (options.Shot is { } shot) _ = ShotAsync(shot);
        if (updateProblem is { } problem) window.Shell.Notify(problem, true);
        else if (updates?.UpdatedFrom is not null) window.Shell.Notify($"EE was updated to {updates.Current.ToString(3)}.");
    }

    static readonly Uri DefaultAddress = new("http://127.0.0.1:4180/");

    static string StartFailure => $"The EE service did not start. Close any other EE window, then try again. The cause is recorded in {DesktopLog.File}.";

    Task<Uri> StartServiceAsync(ServiceHost host)
    {
        service = host;
        host.Stopped += _ => Dispatcher.UIThread.Post(() => ServiceStopped(host));
        return host.StartAsync(CancellationToken.None);
    }

    /// <summary>
    /// The bundled service ended on its own after it started. Reconnecting cannot succeed, so the window stops
    /// trying and offers to start the service again. A service replaced by a retry or ended by Quit is ignored.
    /// </summary>
    void ServiceStopped(ServiceHost host)
    {
        if (quitting || host != service || window is null) return;
        window.Shell.ShowServiceStopped($"The EE service stopped. Start it again to reconnect to the switch. The cause is recorded in {DesktopLog.File}.",
            RetryServiceAsync);
    }

    /// <summary>Starts the bundled service again after it failed or stopped, and opens the workspace in a new window.</summary>
    async Task RetryServiceAsync()
    {
        if (quitting) return;
        var previous = window;
        previous?.Shell.Status("Starting the EE service…");
        try
        {
            if (ServiceHost.Locate(AppContext.BaseDirectory, options.Demo, dataDirectory) is not { } host)
                throw new InvalidOperationException("The EE service files are missing. Reinstall EE.");
            var address = await StartServiceAsync(host);
            AddBrowserItem();
            if (quitting) return;
            if (previous is not null)
            {
                await previous.Shell.Workspace.DisposeAsync();
                previous.AllowClose = true;
                previous.Close();
            }
            Open(address);
            ShowWindow();
            await window!.Shell.Start();
        }
        catch (Exception exception) when (!quitting)
        {
            window?.Shell.ShowServiceFailure(exception is InvalidOperationException { Message: { Length: > 0 } message } && message.Contains("Reinstall")
                ? message : StartFailure, RetryServiceAsync);
        }
    }

    void Open(Uri address)
    {
        // The administration key goes only to the gateway this program started, never to an address chosen by hand.
        var owned = service is { Address: { } started } && started == address;
        workspace = new Workspace(address, owned ? service!.AdminKey : null, DeviceKeys.Get(address));
        window = new ShellWindow(new AppShell(workspace)
        {
            OpenWorkspaceAddress = service is null && options.Shot is null ? ChangeWorkspace : null,
            OpenInBrowser = owned && OperatingSystem.IsWindows() && options.Shot is null ? OpenInBrowserAsync : null,
            Updates = updates,
        });
        // Input in the window postpones an automatic update; a rack screen's taps arrive as pointer presses.
        void Used(object? sender, RoutedEventArgs e) => lastInput = DateTime.UtcNow;
        window.AddHandler(InputElement.PointerPressedEvent, Used, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.PointerWheelChangedEvent, Used, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.AddHandler(InputElement.KeyDownEvent, Used, RoutingStrategies.Tunnel, handledEventsToo: true);
        window.Opened += (_, _) => shownOnce = true;
        var hidden = options.Background;
        if (restorePlacement is { } saved)
        {
            // Only the first window after an update.
            restorePlacement = null;
            hidden |= saved.Hidden;
            if (Place(window, saved)) shownOnce = true;
        }
        if (options.Shot is not null)
        {
            // Screenshots come from a real window placed off screen, so nothing appears on the desktop.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(-20000, -20000);
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
        }
        if (!hidden) window.Show();
        else _ = window.Shell.SetHidden(true);
    }

    /// <summary>
    /// Puts a window that is not shown yet where it was before an update: the same pixel rectangle, maximized if it
    /// was. A rectangle whose screen is gone is ignored, and the window then opens centered as usual.
    /// </summary>
    internal static bool Place(Window window, WindowPlacement saved)
    {
        if (saved is not { X: { } x, Y: { } y, Width: >= 100 and var width, Height: >= 100 and var height }
            || !window.Screens.All.Any(screen => screen.Bounds.Intersects(new PixelRect(x, y, 100, 100)))) return false;
        var bounds = new PixelRect(x, y, width, height);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        WindowBounds.Apply(window, bounds);
        if (saved.Maximized)
        {
            // Windows maximizes it on the screen that holds the rectangle.
            window.WindowState = WindowState.Maximized;
            return true;
        }
        // Showing the window applies its size in window units once more, so the rectangle is set again afterwards.
        void Opened(object? sender, EventArgs e)
        {
            window.Opened -= Opened;
            WindowBounds.Apply(window, bounds);
        }
        window.Opened += Opened;
        return true;
    }

    async Task ChangeWorkspace(Uri address)
    {
        var previous = window!;
        using (var probe = new EeApi(address))
        {
            if (await probe.GetAsync("/api/workspace") is not JsonObject info || info.Bool("live") is null)
                throw new InvalidOperationException("That address did not respond as an EE workspace.");
        }
        await previous.Shell.Workspace.Idle();
        previous.Shell.Studio.SaveUiState(previous.Shell.PageName);
        Locations.SavedWorkspace = address;
        await previous.Shell.Workspace.DisposeAsync();
        previous.Shell.ResetWorkspace();
        previous.AllowClose = true;
        previous.Close();
        Open(address);
        ShowWindow();
        await window!.Shell.Start();
    }

    void CreateTray()
    {
        menu = new NativeMenu();
        var openItem = new NativeMenuItem("Open Etherlighting Evolved");
        openItem.Click += (_, _) => ShowWindow();
        menu.Add(openItem);
        if (Autostart.Supported)
        {
            var startItem = new NativeMenuItem(OperatingSystem.IsWindows() ? "Start with Windows" : "Start at login")
            {
                ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = Autostart.Enabled,
            };
            startItem.Click += (_, _) =>
            {
                try { Autostart.Set(!Autostart.Enabled); }
                catch (Exception) { }
                startItem.IsChecked = Autostart.Enabled;
            };
            menu.Add(startItem);
        }
        menu.Add(new NativeMenuItemSeparator());
        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) => _ = QuitAsync();
        menu.Add(quitItem);
        WindowIcon? icon = null;
        try { icon = new WindowIcon(AssetLoader.Open(new Uri("avares://EtherlightingEvolved/Assets/ee.ico"))); }
        catch (Exception) { }
        tray = new TrayIcon { Icon = icon, ToolTipText = "Etherlighting Evolved", Menu = menu, IsVisible = true };
        tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(app, [tray]);
    }

    /// <summary>
    /// Adds Open in browser below Open Etherlighting Evolved once this program's own service has started. The link
    /// it opens is put on the browser's command line, which other users can read on macOS and Linux, and their
    /// gateways only run the demo, so the item is Windows only.
    /// </summary>
    void AddBrowserItem()
    {
        if (!OperatingSystem.IsWindows() || menu is null || browserItem is not null) return;
        browserItem = new NativeMenuItem("Open in browser");
        browserItem.Click += (_, _) => _ = OpenInBrowserAsync();
        menu.Items.Insert(1, browserItem);
    }

    /// <summary>Opens the workspace in the default browser with a single-use administration link.</summary>
    async Task OpenInBrowserAsync()
    {
        if (quitting || window is null) return;
        if (service is not { Running: true } running)
        {
            ShowWindow();
            window.Shell.Notify("The EE service is not running. Quit EE and open it again.", true);
            return;
        }
        bool opened;
        try { opened = await window.Launcher.LaunchUriAsync(await running.BrowserLinkAsync(CancellationToken.None)); }
        catch (Exception error)
        {
            DesktopLog.Error("open_in_browser_failed", error);
            opened = false;
        }
        if (opened || quitting) return;
        ShowWindow();
        window?.Shell.Notify("The browser workspace could not be opened. Try again.", true);
    }

    void ShowWindow()
    {
        if (window is null || quitting) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    async Task<string> HandleAsync(string command)
    {
        if (command == "show")
        {
            ShowWindow();
            return "ok";
        }
        await QuitAsync();
        return service?.Running == true ? "service_still_running" : "closed";
    }

    /// <summary>
    /// Quits after trying to return the switch lighting. EE closes even when the return is not confirmed, for
    /// example while the switch is restarting; the next start recovers the lighting.
    /// </summary>
    Task QuitAsync() => quitTask ??= CloseDesktopAsync();

    void PrepareToQuit()
    {
        if (quitting) return;
        quitting = true;
        updates?.Stop();
        if (workspace is not null)
        {
            workspace.Closing = true;
            workspace.StopFrames();
        }
    }

    async Task CloseDesktopAsync()
    {
        var code = 0;
        // The window goes away at once; the tray icon stays until the service has stopped.
        try
        {
            PrepareToQuit();
            window?.Hide();
            if (tray is not null) tray.ToolTipText = "Etherlighting Evolved is closing";
        }
        catch (Exception) { code = 1; }
        var confirmed = true;
        try { confirmed = service is null || await service.StopAsync(); }
        catch (Exception) { confirmed = false; }
        await Finish(confirmed && code == 0 ? 0 : 1);
    }

    async Task Finish(int code)
    {
        try { window?.Shell.Studio.SaveUiState(window.Shell.PageName); }
        catch (Exception) { code = 1; }
        try { if (workspace is not null) await workspace.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception) { code = 1; }
        try { tray?.Dispose(); }
        catch (Exception) { code = 1; }
        if (window is not null) window.AllowClose = true;
        desktop.Shutdown(code);
    }

    // Windows sign-out can overlap an existing tray or installer request. The owned service
    // closes on a worker thread so the OS can wait for the same result while the UI is blocked.
    void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        using (SessionEnd.Block(window?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero))
        {
            try { PrepareToQuit(); } catch (Exception) { }
            try { service?.StopAsync().GetAwaiter().GetResult(); } catch (Exception) { }
        }
        if (window is not null) window.AllowClose = true;
    }

    /// <summary>Saves one page as shot.png from the real window off screen, then quits (development checks).</summary>
    async Task ShotAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var code = 1;
        try
        {
            var shell = window!.Shell;
            window.Width = options.ShotSize.Width;
            window.Height = options.ShotSize.Height;
            if (options.ShotTheme is { } theme) Look.Apply(app, theme);
            var started = Stopwatch.StartNew();
            while (!(shell.Workspace.Ready && shell.CurrentPage is not null) && shell.PageName != "Connect")
            {
                if (started.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("The workspace did not open.");
                await Task.Delay(200);
            }
            if (options.ShotPage != "Connect" && shell.PageName != options.ShotPage) await shell.Navigate(options.ShotPage);
            if (shell.PageName != options.ShotPage || shell.CurrentPage is null)
                throw new InvalidOperationException($"The requested {options.ShotPage} page did not open.");
            await Task.Delay(2500);
            if (Math.Abs(window.Bounds.Width - options.ShotSize.Width) > 1 || Math.Abs(window.Bounds.Height - options.ShotSize.Height) > 1)
                throw new InvalidOperationException($"Requested a {options.ShotSize.Width} × {options.ShotSize.Height} window, got {window.Bounds.Width} × {window.Bounds.Height}.");
            using var bitmap = new RenderTargetBitmap(options.ShotSize);
            bitmap.Render(window);
            bitmap.Save(Path.Combine(folder, "shot.png"));
            code = 0;
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "shot-error.txt"), exception.ToString());
        }
        if (service is not null && !await service.StopAsync()) code = 1;
        await Finish(code);
    }
}

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options.Quit) return SingleInstance.QuitRunningCopy();
        if (options.Shot is { } output) Locations.TestDataDirectory = Path.Combine(output, "client");
        var data = options.Shot is { } isolated ? Path.Combine(isolated, "data") : Locations.Workspace(options.Demo);
        // Development screenshots run beside the installed EE, so they never share its single-instance name.
        var suffix = options.Shot is { } shot ? SingleInstance.Suffix(true, shot) : SingleInstance.Suffix(options.Demo, data);
        using var instance = SingleInstance.TryAcquire(suffix);
        if (instance is null)
        {
            if (!options.Background && !options.AfterUpdate) SingleInstance.SendAsync(suffix, "show", TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return 0;
        }
        App.Options = options;
        App.Instance = instance;
        App.DataDirectory = data;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// The window draws with Skia on the processor. Measured on Windows, this used 61 MB of memory and 0.3% of one
    /// core while hidden, against 275 MB and 2% for GPU composition, whose frame clock keeps running when no window
    /// is shown. EE_GPU_RENDERING=1 selects GPU composition for comparison.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace()
            .With(new Avalonia.Rendering.Composition.CompositionOptions { UseRegionDirtyRectClipping = true });
        return Environment.GetEnvironmentVariable("EE_GPU_RENDERING") == "1"
            ? builder
            : builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
    }
}
