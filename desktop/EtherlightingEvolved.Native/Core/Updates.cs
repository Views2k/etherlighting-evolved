using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Threading;

namespace EtherlightingEvolved.Native.Core;

/// <summary>The Windows installer of a release, as its SHA256SUMS lists it.</summary>
public sealed record UpdateRelease(Version Version, string File, string Sha256)
{
    public string Label => Version.ToString(3);
}

/// <summary>
/// Where the window was when EE closed for an update: its rectangle in screen pixels, which is missing when the
/// window had not been shown since EE started.
/// </summary>
public sealed record WindowPlacement(bool Hidden, bool Maximized, int? X = null, int? Y = null, int? Width = null, int? Height = null);

public enum UpdateStage { Off, Waiting, Checking, Current, Available, Downloading, Ready, Installing, Failed }

/// <summary>
/// Keeps a copy installed by setup on Windows current with the project's GitHub releases. Two minutes after start
/// and then every hour it reads the latest release's SHA256SUMS. A newer Windows installer is downloaded into the
/// updates folder, and it is kept only if its SHA-256 matches that list and its version information names the same
/// version. It is installed once nobody has used the window for ten minutes, no dialog is open and no change is being
/// sent. Setup asks this copy to quit, as it does for any upgrade, and starts the new version with --after-update,
/// which opens the window where it was. An installer that ends while this copy still runs did not install, and that
/// version is tried again after a day. With automatic updates off, nothing is checked until Check for updates.
/// </summary>
public sealed class Updates
{
    public const string Repository = "https://github.com/Views2k/etherlighting-evolved";
    public const string AutomaticKey = "ee.updates.automatic.v1";
    public static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2), Interval = TimeSpan.FromHours(1), Quiet = TimeSpan.FromMinutes(10),
        RetryFailed = TimeSpan.FromDays(1), RestartAge = TimeSpan.FromMinutes(30);

    /// <summary>The 0.6.3 installer is 76 MB; a reply larger than this is not an EE installer.</summary>
    const long InstallerLimit = 512L * 1024 * 1024;

    const int SumsLimit = 64 * 1024;

    static readonly Regex InstallerLine = new(@"^([0-9a-f]{64}) [ *](EtherlightingEvolved-Setup-(\d{1,5})\.(\d{1,5})\.(\d{1,5})\.exe)\r?$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    readonly HttpClient http;
    readonly Func<DateTime> clock;
    readonly Func<string, Version?> installerVersion;
    readonly Func<ProcessStartInfo, Task<int>?> launch;
    Func<bool> quiet = () => false;
    Func<WindowPlacement?> placement = () => null;
    DispatcherTimer? timer;
    DateTime nextCheck;
    bool working, stopped;

    /// <param name="programFolder">The installed program. Setup needs administrator approval when it cannot be written.</param>
    /// <param name="installerVersion">Reads the version information of a downloaded installer.</param>
    /// <param name="launch">Starts setup and returns its exit code once it ends, or null if it did not start.</param>
    public Updates(Version current, string folder, string programFolder, HttpMessageHandler? handler = null, Func<DateTime>? clock = null,
        Func<string, Version?>? installerVersion = null, Func<ProcessStartInfo, Task<int>?>? launch = null)
    {
        Current = current;
        Folder = folder;
        this.clock = clock ?? (() => DateTime.UtcNow);
        this.installerVersion = installerVersion ?? ReadInstallerVersion;
        this.launch = launch ?? Launch;
        NeedsApproval = !Writable(programFolder);
        http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true) { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EtherlightingEvolved/" + current.ToString(3));
        nextCheck = this.clock() + FirstCheck;
        Stage = Automatic ? UpdateStage.Waiting : UpdateStage.Off;
    }

    /// <summary>
    /// The updater for a copy installed by setup on Windows, or null: other systems, demo and screenshot runs, and
    /// copies run from a build folder.
    /// </summary>
    public static Updates? ForInstalledCopy(Options options)
    {
        if (!OperatingSystem.IsWindows() || options.Demo || options.Shot is not null) return null;
        var program = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(program, "unins000.exe")) || !Version.TryParse(App.Version, out var current)) return null;
        return new Updates(current, Path.Combine(Locations.AppData, "updates"), program);
    }

    public Version Current { get; }

    /// <summary>Downloaded installers, setup logs and the update records.</summary>
    public string Folder { get; }

    /// <summary>The program folder cannot be written, so setup asks for administrator approval and runs only on request.</summary>
    public bool NeedsApproval { get; }

    public UpdateStage Stage { get; private set; }

    /// <summary>The newer release found by the last check.</summary>
    public UpdateRelease? Release { get; private set; }

    public string? Error { get; private set; }

    public DateTime? CheckedAt { get; private set; }

    /// <summary>The version this copy replaced, when it was started after an update.</summary>
    public string? UpdatedFrom { get; private set; }

    /// <summary>Raised on the thread that changed the stage.</summary>
    public event Action? Changed;

    string StateFile => Path.Combine(Folder, "update.json");

    string RestartFile => Path.Combine(Folder, "restart.json");

    public bool Automatic
    {
        get => Preferences.GetItem(AutomaticKey) != "false";
        set
        {
            Preferences.SetItem(AutomaticKey, value ? "true" : "false");
            if (value) nextCheck = clock();
            if (Stage is UpdateStage.Off or UpdateStage.Waiting) Set(value ? UpdateStage.Waiting : UpdateStage.Off);
            else Changed?.Invoke();
        }
    }

    /// <summary>Reads SHA256SUMS. The release must list exactly one Windows installer.</summary>
    public static UpdateRelease? Parse(string sums)
    {
        var matches = InstallerLine.Matches(sums);
        if (matches.Count != 1) return null;
        var match = matches[0];
        var parts = new int[3];
        for (var index = 0; index < 3; index++)
            if (!int.TryParse(match.Groups[index + 3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out parts[index])) return null;
        return new UpdateRelease(new Version(parts[0], parts[1], parts[2]), match.Groups[2].Value, match.Groups[1].Value);
    }

    /// <summary>A line for the Settings page.</summary>
    public string Describe()
    {
        var updated = UpdatedFrom is { } from ? $"EE was updated from {from} to {Current.ToString(3)}. " : "";
        return Stage switch
        {
            UpdateStage.Off => updated + "Automatic updates are off.",
            UpdateStage.Waiting => updated + "The first check runs two minutes after EE starts.",
            UpdateStage.Checking => "Checking for updates…",
            UpdateStage.Current => $"{updated}EE {Current.ToString(3)} is the latest version. Checked at {Time(CheckedAt)}.",
            UpdateStage.Available => $"EE {Release?.Label} is available.",
            UpdateStage.Downloading => $"Downloading EE {Release?.Label}…",
            UpdateStage.Ready when NeedsApproval => $"EE {Release?.Label} is ready. Installing it needs administrator approval.",
            UpdateStage.Ready when Automatic => $"EE {Release?.Label} installs after {Quiet.TotalMinutes:0} minutes without input.",
            UpdateStage.Ready => $"EE {Release?.Label} is ready to install.",
            UpdateStage.Installing => $"Installing EE {Release?.Label}. EE closes and starts again.",
            _ => Error ?? "The update did not finish.",
        };
    }

    static string Time(DateTime? time) => time?.ToLocalTime().ToString("t", CultureInfo.CurrentCulture) ?? "";

    /// <summary>
    /// Reads what an update left behind. Started on the version that was being installed, it removes the installer
    /// and, for a start by setup within 30 minutes, returns the window placement saved before. Started on an older
    /// version, the update did not install: that is recorded and returned as a problem.
    /// </summary>
    public (string? Problem, WindowPlacement? Placement) Resume(bool afterUpdate)
    {
        var state = ReadJson(StateFile);
        string? problem = null;
        if (state["attempt"] is JsonObject attempt && Version.TryParse(attempt.Str("version"), out var version))
        {
            state.Remove("attempt");
            if (version > Current)
            {
                state["failed"] = new JsonObject { ["version"] = version.ToString(3), ["at"] = Stamp(clock()) };
                problem = $"EE {version.ToString(3)} did not install. The setup log is in {Folder}.";
                Fail(problem);
            }
            else
            {
                if (version == Current) UpdatedFrom = attempt.Str("from");
                state.Remove("failed");
                RemoveInstallers(keep: null);
            }
            WriteJson(StateFile, state);
            if (problem is null) Changed?.Invoke();
        }
        WindowPlacement? saved = null;
        var restart = ReadJson(RestartFile);
        if (afterUpdate && restart.Str("version") == Current.ToString(3)
            && DateTime.TryParse(restart.Str("at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) && clock() >= at && clock() - at < RestartAge)
        {
            saved = new WindowPlacement(restart.Bool("hidden") == true, restart.Bool("maximized") == true, restart.Int("x"), restart.Int("y"),
                restart.Int("width"), restart.Int("height"));
        }
        try { File.Delete(RestartFile); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return (problem, saved);
    }

    /// <summary>Starts the half-minute schedule. quiet says that nobody is using the window.</summary>
    public void Start(Func<bool> quiet, Func<WindowPlacement?> placement)
    {
        Attach(quiet, placement);
        timer ??= new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, async (_, _) =>
        {
            try { await Tick(); }
            catch (Exception error) { DesktopLog.Error("update_failed", error); }
        });
        timer.Start();
    }

    /// <summary>Sets what Tick asks before installing, without starting the schedule (desktop checks).</summary>
    internal void Attach(Func<bool> quiet, Func<WindowPlacement?> placement)
    {
        this.quiet = quiet;
        this.placement = placement;
    }

    /// <summary>Stops the schedule when EE quits. A running download is left to end with the process.</summary>
    public void Stop()
    {
        stopped = true;
        timer?.Stop();
    }

    /// <summary>One step of the schedule: check when due, download a newer installer, and install it when quiet.</summary>
    public async Task Tick()
    {
        if (working || stopped || !Automatic || Stage == UpdateStage.Installing) return;
        if (clock() >= nextCheck) await Check();
        if (Stage == UpdateStage.Available && !Blocked()) await Download();
        if (Stage == UpdateStage.Ready && !NeedsApproval && quiet()) Install();
    }

    /// <summary>Check for updates in Settings: checks now and, with automatic updates on, downloads what it finds.</summary>
    public async Task CheckNow()
    {
        if (working || Stage == UpdateStage.Installing) return;
        await Check(manual: true);
        if (Automatic && Stage == UpdateStage.Available) await Download();
    }

    /// <summary>Install now in Settings: downloads the newer release if needed and installs it at once.</summary>
    public async Task InstallNow()
    {
        if (working || Release is null || Stage == UpdateStage.Installing) return;
        if (!IsDownloaded(Release)) await Download();
        if (Stage == UpdateStage.Ready) Install();
    }

    async Task Check(bool manual = false)
    {
        working = true;
        nextCheck = clock() + Interval;
        Set(UpdateStage.Checking);
        try
        {
            var release = Parse(await GetText(new Uri($"{Repository}/releases/latest/download/SHA256SUMS")))
                ?? throw new UpdateException("The latest release lists no Windows installer.");
            CheckedAt = clock();
            if (release.Version <= Current)
            {
                Release = null;
                Set(UpdateStage.Current);
                return;
            }
            Release = release;
            if (!manual && Blocked())
                Fail($"EE {release.Label} could not be installed. EE tries again after {(FailedAt() + RetryFailed)?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}.");
            else Set(IsDownloaded(release) ? UpdateStage.Ready : UpdateStage.Available);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or UpdateException)
        {
            Fail("Could not check for updates. " + Reason(error));
        }
        finally { working = false; }
    }

    /// <summary>The newer release did not install less than a day ago.</summary>
    bool Blocked() => Release is { } release && ReadJson(StateFile)["failed"] is JsonObject failed && failed.Str("version") == release.Label
        && FailedAt() is { } at && clock() < at + RetryFailed;

    DateTime? FailedAt() => DateTime.TryParse(ReadJson(StateFile).At("failed").Str("at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
        ? at : null;

    async Task Download()
    {
        if (Release is not { } release) return;
        working = true;
        Set(UpdateStage.Downloading);
        var target = Path.Combine(Folder, release.File);
        var partial = target + ".partial";
        try
        {
            Directory.CreateDirectory(Folder);
            using var response = await http.GetAsync(new Uri($"{Repository}/releases/download/v{release.Label}/{release.File}"), HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            Secure(response);
            if (response.Content.Headers.ContentLength > InstallerLimit) throw new UpdateException("The installer is larger than EE accepts.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    total += read;
                    if (total > InstallerLimit) throw new UpdateException("The installer is larger than EE accepts.");
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read));
                }
            }
            if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != release.Sha256)
                throw new UpdateException("The downloaded installer does not match the release's SHA256SUMS.");
            if (installerVersion(partial) != release.Version) throw new UpdateException($"The downloaded installer is not EE {release.Label}.");
            File.Move(partial, target, overwrite: true);
            RemoveInstallers(keep: release.File);
            Set(UpdateStage.Ready);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or UpdateException)
        {
            try { File.Delete(partial); }
            catch (Exception remove) when (remove is IOException or UnauthorizedAccessException) { }
            DesktopLog.Error("update_download_failed", error);
            // A release whose installer fails its checks would fail again, so it waits a day like one that did not install.
            if (error is UpdateException) ForgetAttempt(release, failed: true);
            Fail($"Could not download EE {release.Label}. " + Reason(error));
        }
        finally { working = false; }
    }

    /// <summary>
    /// Starts setup for the downloaded release after recording the attempt and the window placement. Setup asks this
    /// copy to quit before it changes any file, so if setup ends while this copy still runs, nothing was installed.
    /// </summary>
    void Install()
    {
        if (Release is not { } release) return;
        var installer = Path.Combine(Folder, release.File);
        var log = Path.Combine(Folder, $"setup-{release.Label}.log");
        var state = ReadJson(StateFile);
        state["attempt"] = new JsonObject { ["version"] = release.Label, ["from"] = Current.ToString(3), ["at"] = Stamp(clock()) };
        Task<int>? exit;
        try
        {
            WriteJson(StateFile, state);
            var window = placement();
            var restart = new JsonObject { ["at"] = Stamp(clock()), ["version"] = release.Label, ["hidden"] = window?.Hidden ?? true, ["maximized"] = window?.Maximized ?? false };
            if (window is { X: { } x, Y: { } y, Width: { } width, Height: { } height })
            {
                restart["x"] = x;
                restart["y"] = y;
                restart["width"] = width;
                restart["height"] = height;
            }
            WriteJson(RestartFile, restart);
            Set(UpdateStage.Installing);
            exit = launch(new ProcessStartInfo(installer, $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL /SP- /EEUPDATE /LOG=\"{log}\"")
            {
                UseShellExecute = true,
            }) ?? throw new UpdateException("Windows did not start the installer.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException
                                         or UpdateException)
        {
            DesktopLog.Error("update_start_failed", error);
            ForgetAttempt(release, failed: false);
            Fail($"Could not start the EE {release.Label} installer. " + Reason(error));
            return;
        }
        _ = Watch(exit, release, log);
    }

    async Task Watch(Task<int> exit, UpdateRelease release, string log)
    {
        int code;
        try { code = await exit; }
        catch (Exception) { code = -1; }
        if (stopped) return;
        ForgetAttempt(release, failed: true);
        Fail($"EE {release.Label} did not install (setup exit code {code}). The setup log is {log}.");
    }

    void ForgetAttempt(UpdateRelease release, bool failed)
    {
        try
        {
            var state = ReadJson(StateFile);
            state.Remove("attempt");
            if (failed) state["failed"] = new JsonObject { ["version"] = release.Label, ["at"] = Stamp(clock()) };
            WriteJson(StateFile, state);
            File.Delete(RestartFile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    bool IsDownloaded(UpdateRelease release)
    {
        var file = Path.Combine(Folder, release.File);
        try
        {
            if (!File.Exists(file)) return false;
            using var stream = File.OpenRead(file);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() == release.Sha256 && installerVersion(file) == release.Version;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Removes downloaded installers other than keep, and setup logs other than the newest.</summary>
    void RemoveInstallers(string? keep)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var file in Directory.EnumerateFiles(Folder, "EtherlightingEvolved-Setup-*"))
                if (Path.GetFileName(file) != keep) File.Delete(file);
            foreach (var log in new DirectoryInfo(Folder).GetFiles("setup-*.log").OrderByDescending(file => file.LastWriteTimeUtc).Skip(1))
                log.Delete();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    async Task<string> GetText(Uri uri)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        Secure(response);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[SumsLimit + 1];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length))) > 0) length += read;
        if (length > SumsLimit) throw new UpdateException("SHA256SUMS is larger than EE accepts.");
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    /// <summary>The client does not follow a redirect from HTTPS to HTTP; this checks the final address as well.</summary>
    static void Secure(HttpResponseMessage response)
    {
        if (response.RequestMessage?.RequestUri is { Scheme: not "https" }) throw new UpdateException("GitHub redirected the download to an address without HTTPS.");
    }

    static string Reason(Exception error) => error switch
    {
        UpdateException => error.Message,
        HttpRequestException { StatusCode: { } status } => $"GitHub answered {(int)status} {(status == HttpStatusCode.NotFound ? "(not found)" : status.ToString())}.",
        HttpRequestException => "GitHub could not be reached.",
        TaskCanceledException => "GitHub did not answer in time.",
        System.ComponentModel.Win32Exception or InvalidOperationException => "Windows did not start it.",
        _ => "The updates folder could not be written.",
    };

    void Set(UpdateStage stage)
    {
        Stage = stage;
        if (stage != UpdateStage.Failed) Error = null;
        Changed?.Invoke();
    }

    void Fail(string message)
    {
        Error = message;
        Set(UpdateStage.Failed);
    }

    static string Stamp(DateTime time) => time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    static JsonObject ReadJson(string file)
    {
        try { return Json.Parse(File.ReadAllText(file)) as JsonObject ?? []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    static void WriteJson(string file, JsonObject value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, value.ToJsonString());
            File.Move(temporary, file, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    static bool Writable(string folder)
    {
        try
        {
            using var probe = new FileStream(Path.Combine(folder, ".ee-update-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// The release version in the version information of an EE installer (its file version), or null for another
    /// file. Inno Setup pads the text values with spaces, so the product name is compared without them.
    /// </summary>
    internal static Version? ReadInstallerVersion(string file)
    {
        var info = FileVersionInfo.GetVersionInfo(file);
        return info.ProductName?.Trim() == "Etherlighting Evolved" && info.FileMajorPart + info.FileMinorPart + info.FileBuildPart > 0
            ? new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) : null;
    }

    static Task<int>? Launch(ProcessStartInfo info)
    {
        var process = Process.Start(info);
        if (process is null) return null;
        return WaitAsync(process);

        static async Task<int> WaitAsync(Process process)
        {
            using (process)
            {
                await process.WaitForExitAsync();
                return process.ExitCode;
            }
        }
    }
}

sealed class UpdateException(string message) : Exception(message);
