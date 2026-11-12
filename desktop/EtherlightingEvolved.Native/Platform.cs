using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace EtherlightingEvolved.Native;

/// <summary>
/// One copy per user. The mutex name matches the earlier desktop app, so the installer's running-copy check and
/// <c>--quit</c> work for both. Other copies send their request over a pipe only the same user can open.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string BaseName = "EtherlightingEvolved.Desktop";
    readonly Mutex mutex;
    readonly string pipe;
    readonly CancellationTokenSource stop = new();

    SingleInstance(Mutex mutex, string pipe) => (this.mutex, this.pipe) = (mutex, pipe);

    public static string Suffix(bool demo, string dataDirectory) =>
        demo ? ".Demo." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataDirectory)))[..16].ToLowerInvariant() : "";

    static string PipeName(string suffix) =>
        BaseName + suffix + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..12];

    public static SingleInstance? TryAcquire(string suffix)
    {
        var mutex = new Mutex(true, BaseName + suffix, out var created);
        if (created) return new SingleInstance(mutex, PipeName(suffix));
        mutex.Dispose();
        return null;
    }

    public static bool IsRunning(string name)
    {
        if (!Mutex.TryOpenExisting(name, out var existing)) return false;
        existing.Dispose();
        return true;
    }

    /// <summary>Answers "show" and "quit" requests from later copies and the installer.</summary>
    public void Listen(Func<string, Task<string>> handle)
    {
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(stop.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                    await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                    var command = (await reader.ReadLineAsync(stop.Token))?.Trim() ?? "";
                    var reply = command is "show" or "quit" ? await handle(command) : "unknown";
                    await writer.WriteLineAsync(reply);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { await Task.Delay(200); }
            }
        });
    }

    public static async Task<string?> SendAsync(string suffix, string command, TimeSpan timeout)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName(suffix), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var cancel = new CancellationTokenSource(timeout);
            await client.ConnectAsync(cancel.Token);
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(command);
            return (await reader.ReadLineAsync(cancel.Token))?.Trim();
        }
        catch (Exception) { return null; }
    }

    /// <summary>For the installer: ask the running copy to quit and wait until it and its engine have exited.</summary>
    public static int QuitRunningCopy()
    {
        if (!IsRunning(BaseName)) return 0;
        SendAsync("", "quit", TimeSpan.FromSeconds(120)).GetAwaiter().GetResult();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning(BaseName) && !IsRunning("EtherlightingEvolved")) return 0;
            Thread.Sleep(200);
        }
        return 1;
    }

    public void Dispose()
    {
        stop.Cancel();
        try { mutex.ReleaseMutex(); } catch (Exception) { }
        mutex.Dispose();
    }
}

/// <summary>Start with the computer: the same Run value the installer writes on Windows, a login item elsewhere.</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "Etherlighting Evolved";

    static string Command => $"\"{Environment.ProcessPath}\" --background";

    public static bool Supported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public static bool Enabled
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows()) return WindowsValue() is not null;
                return File.Exists(LoginItem);
            }
            catch (Exception) { return false; }
        }
    }

    public static void Set(bool enabled)
    {
        if (OperatingSystem.IsWindows()) { SetWindows(enabled); return; }
        if (!enabled) { File.Delete(LoginItem); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(LoginItem)!);
        File.WriteAllText(LoginItem, OperatingSystem.IsMacOS()
            ? $"""
              <?xml version="1.0" encoding="UTF-8"?>
              <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
              <plist version="1.0"><dict>
                <key>Label</key><string>com.views2k.etherlighting-evolved</string>
                <key>ProgramArguments</key><array><string>{System.Security.SecurityElement.Escape(Environment.ProcessPath)}</string><string>--background</string></array>
                <key>RunAtLoad</key><true/>
              </dict></plist>
              """
            : $"[Desktop Entry]\nType=Application\nName=Etherlighting Evolved\nExec={Command}\nX-GNOME-Autostart-enabled=true\n");
    }

    static string LoginItem => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "com.views2k.etherlighting-evolved.plist")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "autostart", "etherlighting-evolved.desktop");

    [SupportedOSPlatform("windows")]
    static object? WindowsValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name);
    }

    [SupportedOSPlatform("windows")]
    static void SetWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(Name, Command);
        else key.DeleteValue(Name, throwOnMissingValue: false);
    }
}

/// <summary>Where this program keeps its own settings and finds the workspace data.</summary>
public static class Locations
{
    internal static string? TestDataDirectory { get; set; }

    public static string AppData => TestDataDirectory ?? (OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Etherlighting Evolved")
        : OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Etherlighting Evolved")
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "etherlighting-evolved"));

    public static string Workspace(bool demo) => Path.Combine(AppData, demo ? "demo" : "workspace");

    static string SettingsFile => Path.Combine(AppData, "native.json");

    /// <summary>The workspace address this window uses when it does not start EE itself.</summary>
    public static Uri? SavedWorkspace
    {
        get
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(SettingsFile));
                return Uri.TryCreate(node?["workspace_url"]?.GetValue<string>(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
            }
            catch (Exception) { return null; }
        }
        set
        {
            Directory.CreateDirectory(AppData);
            var temporary = SettingsFile + ".tmp";
            File.WriteAllText(temporary, new JsonObject { ["workspace_url"] = value?.ToString() }.ToJsonString());
            File.Move(temporary, SettingsFile, overwrite: true);
        }
    }

    /// <summary>The same record the earlier desktop app kept when shutdown could not be confirmed.</summary>
    internal static void RecordUnconfirmedExit(string directory)
    {
        var file = Path.Combine(directory, "last-session.json");
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                time = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), stage = "desktop_exit", outcome = "cleanup_unconfirmed",
                build = typeof(App).Assembly.GetName().Version?.ToString(3),
            }) + "\n");
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception) { }
        finally { try { File.Delete(temporary); } catch (Exception) { } }
    }
}

/// <summary>Asks Windows to wait while the lighting is returned during sign-out or shutdown.</summary>
public static class SessionEnd
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ShutdownBlockReasonCreate(IntPtr window, string reason);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ShutdownBlockReasonDestroy(IntPtr window);

    public static IDisposable Block(IntPtr window)
    {
        if (!OperatingSystem.IsWindows() || window == IntPtr.Zero) return new Release(() => { });
        try { ShutdownBlockReasonCreate(window, "Closing the Etherlighting Evolved workspace"); }
        catch (Exception) { return new Release(() => { }); }
        return new Release(() => { try { ShutdownBlockReasonDestroy(window); } catch (Exception) { } });
    }

    sealed class Release(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
