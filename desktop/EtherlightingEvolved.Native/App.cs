using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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

    public static Options? Parse(string[] args)
    {
        Uri? url = null;
        var address = Environment.GetEnvironmentVariable("EE_WORKSPACE_URL");
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--url" && i + 1 < args.Length) address = args[++i];
        }
        if (!string.IsNullOrWhiteSpace(address))
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out url) || url.Scheme is not ("http" or "https")) return null;
        }
        string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var shot = Arg("--shot") is { } folder ? Path.GetFullPath(folder) : null;
        var size = Arg("--size")?.Split('x') is [var w, var h] && int.TryParse(w, out var width) && int.TryParse(h, out var height) ? new PixelSize(width, height) : new PixelSize(1440, 900);
        return new Options(args.Contains("--background"), args.Contains("--demo") || (shot is not null && url is null), url, args.Contains("--quit"))
        {
            Shot = shot, ShotPage = Arg("--page") ?? "Lighting", ShotTheme = Arg("--theme"), ShotSize = size,
        };
    }
}

public sealed class App : Application
{
    public const string ThemeKey = "ee.theme.v1";
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
        string? theme = null;
        try { theme = Preferences.GetItem(ThemeKey); } catch (Exception) { }
        Look.Apply(this, theme ?? ThemeTokens.DefaultTheme);
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
    bool quitting, forceArmed;
    Task? quitTask;

    public void Start()
    {
        // An error that escapes an input handler is shown instead of closing EE, which would lose an unapplied
        // preview and leave the window in the tray without its service.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Trace.TraceError(e.Exception.ToString());
            try { window?.Shell.Notify(e.Exception.Message is { Length: > 0 } message ? message : "Could not confirm the change. Try again.", true); }
            catch (Exception) { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
        desktop.ShutdownRequested += OnShutdownRequested;
        instance.Listen(command => Dispatcher.UIThread.InvokeAsync(() => HandleAsync(command)));
        if (options.Shot is null) CreateTray();
        _ = ConnectAsync();
    }

    async Task ConnectAsync()
    {
        Uri address;
        try
        {
            if (options.Url is { } url) address = url;
            else if (ServiceHost.Locate(AppContext.BaseDirectory, options.Demo, dataDirectory) is { } host)
            {
                service = host;
                address = await host.StartAsync(CancellationToken.None);
            }
            else address = Locations.SavedWorkspace ?? new Uri("http://127.0.0.1:4180/");
        }
        catch (Exception)
        {
            if (quitting) return;
            if (options.Shot is not null) { desktop.Shutdown(1); return; }
            if (window is null) Open(new Uri("http://127.0.0.1:9/"));
            window!.Shell.ShowServiceFailure("The EE service did not start. Close any other EE window, then try again.", RetryServiceAsync);
            return;
        }
        if (quitting) return;
        Open(address);
        await window!.Shell.Start();
        if (options.Shot is { } shot) _ = ShotAsync(shot);
    }

    /// <summary>Starts the bundled service again after a failed start and opens the workspace in a new window.</summary>
    async Task RetryServiceAsync()
    {
        if (quitting) return;
        var previous = window;
        previous?.Shell.Status("Starting the EE service…");
        try
        {
            if (ServiceHost.Locate(AppContext.BaseDirectory, options.Demo, dataDirectory) is not { } host)
                throw new InvalidOperationException("The EE service files are missing. Reinstall EE.");
            service = host;
            var address = await host.StartAsync(CancellationToken.None);
            if (quitting) return;
            if (previous is not null)
            {
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
                ? message : "The EE service did not start. Close any other EE window, then try again.", RetryServiceAsync);
        }
    }

    void Open(Uri address)
    {
        workspace = new Workspace(address);
        window = new ShellWindow(new AppShell(workspace)
        {
            OpenWorkspaceAddress = service is null && options.Shot is null ? ChangeWorkspace : null,
        });
        if (options.Shot is not null)
        {
            // Screenshots come from a real window placed off screen, so nothing appears on the desktop.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(-20000, -20000);
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
        }
        if (!options.Background) window.Show();
        else _ = window.Shell.SetHidden(true);
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
        var menu = new NativeMenu();
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
        quitItem.Click += (_, _) => _ = QuitAsync(force: false);
        menu.Add(quitItem);
        WindowIcon? icon = null;
        try { icon = new WindowIcon(AssetLoader.Open(new Uri("avares://EtherlightingEvolved/Assets/ee.ico"))); }
        catch (Exception) { }
        tray = new TrayIcon { Icon = icon, ToolTipText = "Etherlighting Evolved", Menu = menu, IsVisible = true };
        tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(app, [tray]);
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
        // The installer must be able to replace the files, so its request ends EE even if cleanup is unconfirmed.
        await QuitAsync(force: true);
        return service?.Running == true ? "service_still_running" : "closed";
    }

    /// <summary>
    /// Quits after returning the switch lighting. A Quit from the tray whose return is not confirmed keeps EE
    /// running and says so; choosing Quit again, the installer and Windows sign-out end EE anyway.
    /// </summary>
    Task QuitAsync(bool force) => quitTask ??= CloseDesktopAsync(force || forceArmed);

    void PrepareToQuit()
    {
        if (quitting) return;
        quitting = true;
        if (workspace is not null)
        {
            workspace.Closing = true;
            workspace.StopFrames();
        }
    }

    async Task CloseDesktopAsync(bool force)
    {
        var code = 0;
        try { PrepareToQuit(); window?.Shell.Status("Returning the switch lighting…"); }
        catch (Exception) { code = 1; }
        var confirmed = true;
        try { confirmed = service is null || await service.StopAsync(force); }
        catch (Exception) { confirmed = false; }
        if (!confirmed && !force)
        {
            KeepRunning();
            return;
        }
        await Finish(confirmed && code == 0 ? 0 : 1);
    }

    void KeepRunning()
    {
        forceArmed = true;
        quitting = false;
        quitTask = null;
        if (workspace is not null)
        {
            workspace.Closing = false;
            workspace.ConnectFrames();
        }
        ShowWindow();
        window?.Shell.Notify("EE could not confirm that the switch lighting was returned. Choose Quit again to close anyway; " +
                             "EE recovers the lighting the next time it connects.", true);
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
        if (Options.Parse(args) is not { } options) return 2;
        if (options.Quit) return SingleInstance.QuitRunningCopy();
        if (options.Shot is { } output) Locations.TestDataDirectory = Path.Combine(output, "client");
        var data = options.Shot is { } isolated ? Path.Combine(isolated, "data") : Locations.Workspace(options.Demo);
        // Development screenshots run beside the installed EE, so they never share its single-instance name.
        var suffix = options.Shot is { } shot ? SingleInstance.Suffix(true, shot) : SingleInstance.Suffix(options.Demo, data);
        using var instance = SingleInstance.TryAcquire(suffix);
        if (instance is null)
        {
            if (!options.Background) SingleInstance.SendAsync(suffix, "show", TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
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
