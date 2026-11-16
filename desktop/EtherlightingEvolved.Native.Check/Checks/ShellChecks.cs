using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Pages;
using EtherlightingEvolved.Native.Shell;

namespace EtherlightingEvolved.Native.Check.Checks;

internal static class ShellChecks
{
    public static void Contracts(Action<bool, string> check)
    {
        foreach (var page in AppShell.PageNames.Append("Connect"))
            check(PageRegistry.Has(page), $"{page} has a native page");
        check(PageRegistry.StudioFactory is not null, "Lighting registers its editor for other pages");
        var local = new JsonObject { ["number"] = 4, ["zero"] = 0L };
        check(local.Int("number") == 4 && local.Truthy("number") && !local.Truthy("zero"), "numeric settings work before and after serialization");
        var earlier = new JsonObject { ["type"] = "frame", ["ports"] = new JsonArray(1), ["identify"] = "running" };
        var later = new JsonObject { ["ports"] = null, ["identify"] = null, ["connected"] = true };
        Workspace.MergeFrame(earlier, later);
        check(earlier["ports"] is JsonArray { Count: 1 } && earlier.ContainsKey("identify") && earlier["identify"] is null
            && earlier.Bool("connected") == true && earlier.Str("type") == "frame",
            "combined frames retain the last port reading and honor explicit clears");
        earlier["uplink_bandwidth"] = new JsonObject { ["sequence"] = 7 };
        Workspace.MergeFrame(earlier, new JsonObject { ["ports"] = new JsonArray(2) });
        check(earlier.Bool("connected") == true && earlier.Int("uplink_bandwidth.sequence") == 7,
            "a partial port frame keeps the connection and uplink measurement");
        Workspace.MergeFrame(earlier, new JsonObject { ["connected"] = false, ["uplink_bandwidth"] = null });
        check(earlier.Bool("connected") == false && earlier["uplink_bandwidth"] is null, "an explicit disconnect clears bandwidth");
        UplinkBandwidthChecks.Run(check);
        check(AppShell.TryWorkspaceAddress("https://example.test:8443", out _) &&
              AppShell.TryWorkspaceAddress("http://127.0.0.1:4180", out _) &&
              !AppShell.TryWorkspaceAddress("https://example.test/?token=example", out _) &&
              !AppShell.TryWorkspaceAddress("http://example.test", out _), "the workspace picker accepts private HTTPS and local HTTP origins");
    }

    public static void ExerciseDesktop(string output, Action<bool, string> check, string? page = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        AvaloniaSynchronizationContext.InstallIfNeeded();
        var directory = Path.Combine(output, "workspace-" + Guid.NewGuid().ToString("N"));
        var host = ServiceHost.Locate(AppContext.BaseDirectory, true, directory)
            ?? throw new InvalidOperationException("The simulator gateway could not be found.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var task = Run();
        if (!task.IsCompleted) Dispatcher.UIThread.MainLoop(deadline.Token);
        try
        {
            if (!task.IsCompleted) throw new TimeoutException("The native simulator checks timed out.");
            task.GetAwaiter().GetResult();
        }
        finally { if (host.Running) host.Kill(); }

        async Task Run()
        {
            try
            {
                var address = await host.StartAsync(deadline.Token);
                await using var workspace = new Workspace(address);
                var shell = new AppShell(workspace);
                var window = new ShellWindow(shell);
                try
                {
                    window.Show();
                    // A client older than its service has fewer bundled effects than the service sends.
                    EffectRenderer.SetEffectCatalog(new JsonArray(EffectCatalog.Catalog[0]!.DeepClone()));
                    await shell.Start();
                    if (!workspace.Ready || workspace.Options.Bool("live") != false || workspace.Session.Bool("simulated") != true)
                        throw new InvalidOperationException("The native page checks require their own active simulator.");
                    var served = (await workspace.Request("/api/effects")).Arr().Select(effect => effect.Str("id"));
                    check(EffectRenderer.EffectList.Select(effect => effect.Str("id")).SequenceEqual(served),
                        "the service's effect catalog is installed before any page opens");
                    foreach (var name in AppShell.PageNames)
                    {
                        await shell.Navigate(name);
                        check(shell.PageName == name && shell.CurrentPage?.GetType().Name == name + "Page", $"{name} opens in the native workspace");
                    }
                    await shell.MoveHistory(-1);
                    check(shell.PageName == "Settings", "Back returns to the previous page");
                    await shell.MoveHistory(1);
                    check(shell.PageName == "Help", "Forward restores the page");
                    check(!Texts(shell.CurrentPage!).Any(text => text.Contains("Refreshing the page")), "Help does not mention a page refresh");
                    await SettingsAboutChecks(shell, check);
                    await shell.SetHidden(true);
                    await shell.SetHidden(false);
                    var clock = Stopwatch.StartNew();
                    while (!workspace.StreamOpen && clock.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25);
                    check(workspace.StreamOpen && !workspace.Disconnected, "the live stream recovers after an immediate hide and restore");
                    if (page is null or "Lighting") await LightingPageChecks.RunAsync(shell, check, output);
                    if (page is null or "Lighting") await SliderChecks.RunAsync(shell, check);
                    if (page is null or "Effects") await GalleryPageChecks.RunAsync(shell, check);
                    if (page is null or "Profiles" or "Automation") await AutomationProfileChecks.RunAsync(shell, check);
                    if (page is null or "Fans") await FansPageChecks.RunAsync(shell, check);
                    await shell.Navigate("Lighting", true);
                    using var image = window.CaptureRenderedFrame();
                    check(image is not null, "the normal workspace renders after the editing workflows");
                    image?.Save(Path.Combine(output, "native-workspace.png"));
                    shell.ShowServiceStopped("The EE service stopped.", () => Task.CompletedTask);
                    await Task.Delay(1800);
                    check(!workspace.Ready && !workspace.StreamOpen && shell.CurrentPage is null
                          && window.GetLogicalDescendants().OfType<Button>().Any(button => Equals(button.Content, "Try again")),
                        "a stopped service ends reconnecting and offers to start it again");
                }
                finally
                {
                    shell.ResetWorkspace();
                    window.AllowClose = true;
                    window.Close();
                }
                check(await host.StopAsync(), "the desktop test stops its simulator cleanly");
            }
            finally { deadline.Cancel(); }
        }
    }

    static List<string> Texts(Control root) =>
        root.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").ToList();

    static async Task SettingsAboutChecks(AppShell shell, Action<bool, string> check)
    {
        var workspace = shell.Workspace;
        await shell.Navigate("Settings");
        var settings = shell.CurrentPage!;
        await settings.CanLeave();
        var texts = Texts(settings);
        var buttons = settings.GetLogicalDescendants().OfType<Button>().Select(button => button.Content as string).ToList();
        check(texts.Contains("Ethernet ports") && !buttons.Contains("Close web session"),
            "Settings returns the switch through Handoff or Quit, without the browser's close button");
        var service = workspace.About.Str("version");
        check(service is { Length: > 0 } && texts.Contains(service) && texts.Contains(App.Version) && buttons.Contains("Releases"),
            "About shows the app and service versions and a Releases link");
        check(workspace.Disclaimer.Contains("use at your own risk") && texts.Contains(workspace.Disclaimer), "About shows the service's full disclaimer");
        var connect = PageRegistry.Create("Connect", shell)!;
        try { check(Texts(connect).Contains(workspace.Disclaimer), "the connection view shows the service's full disclaimer"); }
        finally { connect.Leave(); }
    }
}
