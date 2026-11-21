using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Headless;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Pages.Settings;

namespace EtherlightingEvolved.Native.Check.Checks;

/// <summary>
/// Automatic updates against a fake GitHub with a test clock and setup launcher: what is checked, downloaded,
/// verified, started and recorded, and what the next start reads back. Nothing here contacts GitHub or runs setup.
/// </summary>
internal static class UpdateChecks
{
    const string Sums = Updates.Repository + "/releases/latest/download/SHA256SUMS";

    // The newer release in these checks. The expected texts take it from here, because tools/check-repository.py
    // accepts only the current version after "EE " in a source file.
    const string Newer = "0.7.0";
    static readonly byte[] Installer = Encoding.UTF8.GetBytes("stand-in for the 0.7.0 installer");

    static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    static string InstallerUrl(string version) => $"{Updates.Repository}/releases/download/v{version}/EtherlightingEvolved-Setup-{version}.exe";

    static byte[] SumsFor(string version, byte[] installer) => Encoding.UTF8.GetBytes(
        $"{Hash(Encoding.UTF8.GetBytes("linux"))}  EtherlightingEvolved-{version}-linux-x64.tar.gz\n{Hash(installer)}  EtherlightingEvolved-Setup-{version}.exe\n");

    sealed class FakeGitHub : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes = [];
        public readonly List<string> Requests = [];

        public void Serve(string url, byte[] body) => Routes[url] = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            var reply = Routes.TryGetValue(url, out var route) ? route(request) : new HttpResponseMessage(HttpStatusCode.NotFound);
            reply.RequestMessage ??= request;
            return Task.FromResult(reply);
        }
    }

    sealed class Rig
    {
        public readonly FakeGitHub GitHub = new();
        public readonly List<(ProcessStartInfo Info, TaskCompletionSource<int> Exit)> Started = [];
        public DateTime Now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        public Version? InstallerVersion = new(0, 7, 0);
        public bool Quiet;
        public bool LaunchFails;
        public WindowPlacement? Placement = new(false, true, -6232, 1012, 1280, 400);
        public readonly string Data;
        readonly string program;

        public Rig(string output, bool writable = true, string? data = null)
        {
            var root = Directory.CreateDirectory(Path.Combine(output, "updates-" + Guid.NewGuid().ToString("N"))).FullName;
            Data = data ?? Path.Combine(root, "data");
            program = writable ? Directory.CreateDirectory(Path.Combine(root, "program")).FullName : Path.Combine(root, "missing");
        }

        public Updates Make(string current = "0.6.4")
        {
            var updates = new Updates(Version.Parse(current), Data, program, GitHub, () => Now, _ => InstallerVersion, info =>
            {
                if (LaunchFails) return null;
                var exit = new TaskCompletionSource<int>();
                Started.Add((info, exit));
                return exit.Task;
            });
            updates.Attach(() => Quiet, () => Placement);
            return updates;
        }

        public JsonObject State => Json.Parse(File.Exists(Path.Combine(Data, "update.json")) ? File.ReadAllText(Path.Combine(Data, "update.json")) : "{}") as JsonObject ?? [];

        public string[] Installers => Directory.Exists(Data) ? Directory.GetFiles(Data, "EtherlightingEvolved-Setup-*") : [];
    }

    public static async Task Run(string output, Action<bool, string> check)
    {
        Parsing(check);
        Preferences.SetItem(Updates.AutomaticKey, "true");
        await Schedule(output, check);
        await Verification(output, check);
        await Choices(output, check);
        Preferences.SetItem(Updates.AutomaticKey, "true");
    }

    static void Parsing(Action<bool, string> check)
    {
        const string published = """
            9d2dacf002131f5ff63c6a77f67bb17d6e39a6422f2c515934722c4e3769883b  EtherlightingEvolved-0.6.3-linux-arm64.tar.gz
            2ce036ced47d04e09a9c83a4e99832f7aaf49f17195650fc3ca7cddf89175fa7  EtherlightingEvolved-0.6.3-linux-x64.tar.gz
            ece69b8f6f573bc9526f0bfb1bf76ac6fd279948bfcf1831863cff47213fbe5e  EtherlightingEvolved-0.6.3-osx-arm64.zip
            83488729c66a55e8c1d01a9a883213f256ba09fc034b4badbae82912936f575f  EtherlightingEvolved-0.6.3-osx-x64.zip
            11e4d02930e1b71df7cab4268293cf62474dc1176fc61eb0f569600ed3a09135  EtherlightingEvolved-Setup-0.6.3.exe

            """;
        var release = Updates.Parse(published);
        check(release is { Label: "0.6.3", File: "EtherlightingEvolved-Setup-0.6.3.exe", Sha256: "11e4d02930e1b71df7cab4268293cf62474dc1176fc61eb0f569600ed3a09135" }
              && Updates.Parse(published.Replace("\n", "\r\n")) == release,
            "updates: the Windows installer, its version and its SHA-256 are read from the 0.6.3 SHA256SUMS, with either line ending");
        var line = "11e4d02930e1b71df7cab4268293cf62474dc1176fc61eb0f569600ed3a09135  EtherlightingEvolved-Setup-0.6.3.exe";
        check(Updates.Parse(line + "\n" + line.Replace("0.6.3.exe", "0.6.4.exe")) is null && Updates.Parse(line.ToUpperInvariant().Replace("ETHERLIGHTINGEVOLVED-SETUP-0.6.3.EXE",
                  "EtherlightingEvolved-Setup-0.6.3.exe")) is null
              && Updates.Parse(line + ".partial") is null && Updates.Parse(line.Replace("0.6.3", "0.6")) is null && Updates.Parse("") is null,
            "updates: a list with two installers, an uppercase hash, another file name or no installer gives no release");
    }

    static async Task Schedule(string output, Action<bool, string> check)
    {
        var rig = new Rig(output);
        rig.GitHub.Serve(Sums, SumsFor("0.7.0", Installer));
        rig.GitHub.Serve(InstallerUrl("0.7.0"), Installer);
        var updates = rig.Make();
        await updates.Tick();
        check(rig.GitHub.Requests.Count == 0 && updates.Stage == UpdateStage.Waiting, "updates: nothing is checked in the first two minutes after start");

        rig.Now += Updates.FirstCheck;
        await updates.Tick();
        var target = Path.Combine(rig.Data, "EtherlightingEvolved-Setup-0.7.0.exe");
        check(rig.GitHub.Requests.SequenceEqual([Sums, InstallerUrl("0.7.0")]) && updates.Stage == UpdateStage.Ready && File.Exists(target)
              && rig.Installers.Length == 1 && rig.Started.Count == 0 && updates.Describe() == $"EE {Newer} installs after 10 minutes without input.",
            "updates: a newer release is downloaded and verified, then waits until the window is quiet");

        rig.Quiet = true;
        await updates.Tick();
        var started = rig.Started.SingleOrDefault();
        var log = Path.Combine(rig.Data, "setup-0.7.0.log");
        var restart = Json.Parse(File.ReadAllText(Path.Combine(rig.Data, "restart.json")));
        check(rig.Started.Count == 1 && started.Info.FileName == target && started.Info.UseShellExecute
              && started.Info.Arguments == $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /SP- /EEUPDATE /LOG=\"{log}\""
              && updates.Stage == UpdateStage.Installing && rig.State.Str("attempt.version") == "0.7.0" && rig.State.Str("attempt.from") == "0.6.4"
              && restart.Str("version") == "0.7.0" && restart.Int("x") == -6232 && restart.Int("y") == 1012 && restart.Bool("maximized") == true
              && restart.Bool("hidden") == false,
            "updates: once quiet, setup runs silently with /EEUPDATE and a log, after the attempt and the window placement are saved");
        await updates.Tick();
        check(rig.Started.Count == 1, "updates: nothing more happens while setup runs");

        started.Exit.SetResult(5);
        check(updates.Stage == UpdateStage.Failed && updates.Error == $"EE {Newer} did not install (setup exit code 5). The setup log is {log}."
              && rig.State["attempt"] is null && rig.State.Str("failed.version") == "0.7.0" && !File.Exists(Path.Combine(rig.Data, "restart.json")),
            "updates: setup that ends while EE still runs installed nothing, and that is recorded");

        rig.Now += Updates.Interval;
        await updates.Tick();
        check(updates.Stage == UpdateStage.Failed && updates.Error!.StartsWith($"EE {Newer} could not be installed. EE tries again after ") && rig.Started.Count == 1
              && rig.GitHub.Requests.Count == 3,
            "updates: a release that did not install is not tried again within a day");
        rig.Now += Updates.RetryFailed;
        await updates.Tick();
        check(rig.Started.Count == 2 && rig.GitHub.Requests.Count == 4 && updates.Stage == UpdateStage.Installing,
            "updates: a day later the downloaded installer is checked again and run without a new download");

        // Setup quit EE, installed 0.7.0 and started it with --after-update.
        var next = rig.Make("0.7.0");
        var (problem, placement) = next.Resume(afterUpdate: true);
        check(problem is null && placement == rig.Placement && next.UpdatedFrom == "0.6.4" && rig.Installers.Length == 0 && rig.State.Count == 0
              && !File.Exists(Path.Combine(rig.Data, "restart.json"))
              && next.Describe() == "EE was updated from 0.6.4 to 0.7.0. The first check runs two minutes after EE starts.",
            "updates: the updated copy reads the saved window placement, clears the records and removes the installer");
        rig.GitHub.Serve(Sums, SumsFor("0.7.0", Installer));
        rig.Now += Updates.FirstCheck;
        await next.Tick();
        check(next.Stage == UpdateStage.Current && rig.GitHub.Requests.Count(url => url.EndsWith(".exe")) == 1
              && next.Describe().StartsWith($"EE was updated from 0.6.4 to 0.7.0. EE {Newer} is the latest version. Checked at "),
            "updates: the same version is current and nothing is downloaded");

        // Placement only for a start by setup, within 30 minutes.
        var stale = new Rig(output);
        Directory.CreateDirectory(stale.Data);
        File.WriteAllText(Path.Combine(stale.Data, "restart.json"),
            new JsonObject { ["at"] = (stale.Now - TimeSpan.FromMinutes(31)).ToString("o"), ["version"] = "0.7.0", ["hidden"] = false, ["x"] = 1, ["y"] = 2, ["width"] = 900, ["height"] = 600 }.ToJsonString());
        var late = stale.Make("0.7.0").Resume(afterUpdate: true).Placement;
        File.WriteAllText(Path.Combine(stale.Data, "restart.json"),
            new JsonObject { ["at"] = stale.Now.ToString("o"), ["version"] = "0.7.0", ["hidden"] = true }.ToJsonString());
        var manual = stale.Make("0.7.0").Resume(afterUpdate: false).Placement;
        File.WriteAllText(Path.Combine(stale.Data, "restart.json"),
            new JsonObject { ["at"] = stale.Now.ToString("o"), ["version"] = "0.7.0", ["hidden"] = true }.ToJsonString());
        var hidden = stale.Make("0.7.0").Resume(afterUpdate: true).Placement;
        check(late is null && manual is null && hidden == new WindowPlacement(true, false) && !File.Exists(Path.Combine(stale.Data, "restart.json")),
            "updates: a placement older than 30 minutes or read by a start not made by setup is ignored and removed");

        // EE quit for setup, setup failed, and the old version was started by hand.
        var failed = new Rig(output);
        Directory.CreateDirectory(failed.Data);
        File.WriteAllText(Path.Combine(failed.Data, "update.json"),
            new JsonObject { ["attempt"] = new JsonObject { ["version"] = "0.7.0", ["from"] = "0.6.4", ["at"] = failed.Now.ToString("o") } }.ToJsonString());
        var old = failed.Make();
        var (oldProblem, _) = old.Resume(afterUpdate: false);
        check(oldProblem == $"EE {Newer} did not install. The setup log is in {failed.Data}." && old.Stage == UpdateStage.Failed && failed.State["attempt"] is null
              && failed.State.Str("failed.version") == "0.7.0",
            "updates: an older version started after an update records that the update did not install");
    }

    static async Task Verification(string output, Action<bool, string> check)
    {
        var tampered = new Rig(output);
        tampered.GitHub.Serve(Sums, SumsFor("0.7.0", Installer));
        tampered.GitHub.Serve(InstallerUrl("0.7.0"), Encoding.UTF8.GetBytes("something else"));
        var updates = tampered.Make();
        await updates.CheckNow();
        check(updates.Stage == UpdateStage.Failed && updates.Error == $"Could not download EE {Newer}. The downloaded installer does not match the release's SHA256SUMS."
              && tampered.Installers.Length == 0 && tampered.State.Str("failed.version") == "0.7.0",
            "updates: an installer that does not match SHA256SUMS is deleted and not tried again for a day");
        tampered.Now += Updates.Interval;
        await updates.Tick();
        check(tampered.GitHub.Requests.Count(url => url.EndsWith(".exe")) == 1, "updates: the mismatched installer is not downloaded again within a day");

        var wrong = new Rig(output) { InstallerVersion = new Version(0, 6, 9) };
        wrong.GitHub.Serve(Sums, SumsFor("0.7.0", Installer));
        wrong.GitHub.Serve(InstallerUrl("0.7.0"), Installer);
        updates = wrong.Make();
        await updates.CheckNow();
        check(updates.Stage == UpdateStage.Failed && updates.Error == $"Could not download EE {Newer}. The downloaded installer is not EE {Newer}." && wrong.Installers.Length == 0,
            "updates: an installer whose version information names another version is deleted");

        var large = new Rig(output);
        large.GitHub.Serve(Sums, SumsFor("0.7.0", Installer));
        large.GitHub.Routes[InstallerUrl("0.7.0")] = _ =>
        {
            var content = new ByteArrayContent(Installer);
            content.Headers.ContentLength = 600L * 1024 * 1024;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        };
        updates = large.Make();
        await updates.CheckNow();
        check(updates.Stage == UpdateStage.Failed && updates.Error!.EndsWith("The installer is larger than EE accepts.") && large.Installers.Length == 0,
            "updates: an installer larger than 512 MB is refused");

        var plain = new Rig(output);
        plain.GitHub.Routes[Sums] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(SumsFor("0.7.0", Installer)), RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://example.test/SHA256SUMS"),
        };
        updates = plain.Make();
        await updates.CheckNow();
        check(updates.Stage == UpdateStage.Failed && updates.Error == "Could not check for updates. GitHub redirected the download to an address without HTTPS."
              && plain.GitHub.Requests.Count == 1,
            "updates: a list that arrives over plain HTTP is not used");

        var offline = new Rig(output);
        offline.GitHub.Routes[Sums] = _ => throw new HttpRequestException("offline");
        updates = offline.Make();
        offline.Now += Updates.FirstCheck;
        await updates.Tick();
        var first = updates.Error;
        offline.GitHub.Serve(Sums, SumsFor("0.6.4", Installer));
        offline.Now += TimeSpan.FromMinutes(30);
        await updates.Tick();
        var waited = offline.GitHub.Requests.Count;
        offline.Now += TimeSpan.FromMinutes(30);
        await updates.Tick();
        check(first == "Could not check for updates. GitHub could not be reached." && waited == 1 && offline.GitHub.Requests.Count == 2
              && updates.Stage == UpdateStage.Current,
            "updates: a failed check is repeated at the next hourly check");

        var missing = new Rig(output);
        updates = missing.Make();
        await updates.CheckNow();
        check(updates.Error == "Could not check for updates. GitHub answered 404 (not found).", "updates: a release without SHA256SUMS is reported");

        var older = new Rig(output);
        older.GitHub.Serve(Sums, SumsFor("0.6.3", Installer));
        updates = older.Make();
        await updates.CheckNow();
        check(updates.Stage == UpdateStage.Current && updates.Release is null && older.GitHub.Requests.Count == 1,
            "updates: an older latest release is never installed");
    }

    static async Task Choices(string output, Action<bool, string> check)
    {
        Preferences.SetItem(Updates.AutomaticKey, "false");
        var off = new Rig(output) { Quiet = true };
        off.GitHub.Serve(Sums, SumsFor("0.7.0", Installer));
        off.GitHub.Serve(InstallerUrl("0.7.0"), Installer);
        var updates = off.Make();
        off.Now += TimeSpan.FromHours(5);
        await updates.Tick();
        var silent = off.GitHub.Requests.Count == 0 && updates.Stage == UpdateStage.Off && updates.Describe() == "Automatic updates are off.";
        await updates.CheckNow();
        var offered = updates.Stage == UpdateStage.Available && updates.Describe() == $"EE {Newer} is available." && off.GitHub.Requests.Count == 1;
        await updates.InstallNow();
        check(silent && offered && off.Started.Count == 1 && off.GitHub.Requests.Count == 2,
            "updates: with automatic updates off nothing is checked; Check for updates offers the release and Install now installs it");

        Preferences.SetItem(Updates.AutomaticKey, "true");
        var admin = new Rig(output, writable: false) { Quiet = true };
        admin.GitHub.Serve(Sums, SumsFor("0.7.0", Installer));
        admin.GitHub.Serve(InstallerUrl("0.7.0"), Installer);
        updates = admin.Make();
        admin.Now += Updates.FirstCheck;
        await updates.Tick();
        await updates.Tick();
        var waiting = admin.Started.Count == 0 && updates.Stage == UpdateStage.Ready
            && updates.Describe() == $"EE {Newer} is ready. Installing it needs administrator approval.";
        await updates.InstallNow();
        check(updates.NeedsApproval && waiting && admin.Started.Count == 1,
            "updates: a copy installed for all users downloads the update and runs setup only on Install now");

        var refused = new Rig(output) { Quiet = true, LaunchFails = true };
        refused.GitHub.Serve(Sums, SumsFor("0.7.0", Installer));
        refused.GitHub.Serve(InstallerUrl("0.7.0"), Installer);
        updates = refused.Make();
        await updates.InstallNow();
        await updates.CheckNow();
        await updates.InstallNow();
        check(updates.Stage == UpdateStage.Failed && updates.Error == $"Could not start the EE {Newer} installer. Windows did not start the installer."
              && refused.State.Count == 0 && !File.Exists(Path.Combine(refused.Data, "restart.json")),
            "updates: setup that does not start leaves no attempt or placement behind");
    }

    /// <summary>The Updates part of Settings → About. Runs after the headless platform is set up.</summary>
    public static void Panel(string output, Action<bool, string> check)
    {
        Preferences.SetItem(Updates.AutomaticKey, "true");
        var rig = new Rig(output);
        rig.GitHub.Serve(Sums, SumsFor("0.7.0", Installer));
        rig.GitHub.Serve(InstallerUrl("0.7.0"), Installer);
        var updates = rig.Make();
        var panel = new UpdatesPanel(updates);
        var window = new Avalonia.Controls.Window { Content = new Avalonia.Controls.Border { Padding = new Avalonia.Thickness(24), Child = panel }, Width = 720, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var before = panel.StatusText == "The first check runs two minutes after EE starts." && panel.AutomaticBox.IsChecked == true && !panel.InstallButton.IsVisible;
        Wait(updates.CheckNow());
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(output, "updates-panel.png"));
        var ready = panel.StatusText == $"EE {Newer} installs after 10 minutes without input." && panel.InstallButton.IsVisible
            && panel.InstallButton.Content as string == $"Install EE {Newer} now" && panel.CheckButton.IsEnabled;
        panel.AutomaticBox.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        var saved = Preferences.GetItem(Updates.AutomaticKey) == "false" && !updates.Automatic && panel.StatusText == $"EE {Newer} is ready to install.";
        panel.AutomaticBox.IsChecked = true;
        window.Close();
        check(before && ready && saved && updates.Automatic, "updates: Settings shows the update state, offers Install now and saves the automatic update setting");

        // The window placement saved before an update. The headless platform has one screen at 0,0.
        var screen = new Avalonia.Controls.Window().Screens.All.Single().Bounds;
        var placed = new Avalonia.Controls.Window { Width = 1200, Height = 800, MinWidth = 900, MinHeight = 320 };
        var onScreen = AppHost.Place(placed, new WindowPlacement(false, false, screen.X + 40, screen.Y + 30, 1000, 500));
        var scaling = placed.RenderScaling;
        var gone = new Avalonia.Controls.Window { Width = 1200, Height = 800, WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterScreen };
        var offScreen = AppHost.Place(gone, new WindowPlacement(false, false, screen.Right + 5000, screen.Y, 1280, 400));
        var unplaced = AppHost.Place(gone, new WindowPlacement(true, false)) || AppHost.Place(gone, new WindowPlacement(false, false, screen.X, screen.Y, 40, 30));
        check(onScreen && placed.WindowStartupLocation == Avalonia.Controls.WindowStartupLocation.Manual && placed.Position == new Avalonia.PixelPoint(screen.X + 40, screen.Y + 30)
              && Math.Abs(placed.Width - 1000 / scaling) < 0.01 && Math.Abs(placed.Height - 500 / scaling) < 0.01 && placed.MinWidth == 900 && placed.MinHeight == 320
              && !offScreen && !unplaced && gone.WindowStartupLocation == Avalonia.Controls.WindowStartupLocation.CenterScreen && gone.Width == 1200 && gone.Height == 800,
            "updates: a saved window rectangle is applied in pixels, and one on a screen that is gone or without a size is ignored");
    }

    /// <summary>Runs the window's jobs until the task ends, as the checks after the headless setup run on its thread.</summary>
    static void Wait(Task task)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }
}
