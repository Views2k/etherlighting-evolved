using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using EtherlightingEvolved.Native.Core;

namespace EtherlightingEvolved.Native;

/// <summary>
/// Starts and stops the EE service: the local workspace gateway, which starts the lighting engine. Shutdown goes
/// through the gateway so it can finish the current switch scene before the engine exits.
/// </summary>
public sealed class ServiceHost
{
    readonly string node, script, root, dataDirectory;
    readonly bool packaged, demo;
    readonly object stopLock = new();
    // The service's last output lines, kept for desktop.log when it fails to start or stops on its own.
    readonly ConcurrentQueue<string> output = new();
    Task<bool>? stopping, forcing;
    Process? process;

    internal ServiceHost(string node, string script, string root, bool packaged, bool demo, string dataDirectory)
    {
        (this.node, this.script, this.root, this.packaged, this.demo, this.dataDirectory) = (node, script, root, packaged, demo, dataDirectory);
    }

    public Uri? Address { get; private set; }

    public bool Running => process is { HasExited: false };

    /// <summary>The started service exited without being asked to; the argument is its exit code. Raised on a worker thread.</summary>
    public event Action<int>? Stopped;

    /// <summary>The bundled service next to the application, or the source checkout it was built from.</summary>
    public static ServiceHost? Locate(string appDirectory, bool demo, string dataDirectory)
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        var bundledNode = Path.Combine(appDirectory, "runtime", "node" + exe);
        var bundledScript = Path.Combine(appDirectory, "preview", "server.mjs");
        if (File.Exists(bundledNode) && File.Exists(bundledScript) && File.Exists(Path.Combine(appDirectory, "engine", "EEEngine" + exe)))
            return new ServiceHost(bundledNode, bundledScript, appDirectory, true, demo, dataDirectory);
        for (var folder = new DirectoryInfo(appDirectory); folder is not null; folder = folder.Parent)
        {
            var script = Path.Combine(folder.FullName, "preview", "server.mjs");
            if (!File.Exists(script) || !File.Exists(Path.Combine(folder.FullName, "packaging", "native_service.py"))) continue;
            var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => Path.Combine(entry, "node" + exe)).FirstOrDefault(File.Exists);
            return path is null ? null : new ServiceHost(path, script, folder.FullName, false, demo, dataDirectory);
        }
        return null;
    }

    public async Task<Uri> StartAsync(CancellationToken token)
    {
        Process running;
        Task<Uri> ready;
        try { (running, ready) = Launch(); }
        catch (Exception error)
        {
            // The window says desktop.log holds the cause of a failed start, including one before the service runs.
            DesktopLog.Error("service_start_failed", error);
            throw;
        }
        try
        {
            running.BeginOutputReadLine();
            running.BeginErrorReadLine();
            var address = await ready.WaitAsync(TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
            var workspace = Json.Parse(await http.GetStringAsync(new Uri(address, "/api/workspace"), token).ConfigureAwait(false));
            if (workspace.Bool("live") != !demo) throw new InvalidOperationException("The EE workspace could not be verified.");
            lock (stopLock)
            {
                if (running.HasExited) throw new InvalidOperationException("The EE service stopped while starting.");
                Address = address;
            }
            return address;
        }
        catch
        {
            // Give the gateway its own cleanup first, in case its engine already connected to the switch.
            try { EndUnstartedService(); } catch (Exception) { }
            if (running.HasExited) await Drain(running).ConfigureAwait(false);
            DesktopLog.Service("service_start_failed", running.HasExited ? running.ExitCode : null, output);
            throw;
        }

        void EndUnstartedService()
        {
            try { running.StandardInput.Close(); } catch (Exception) { }
            if (!running.WaitForExit(25000)) Kill();
        }
    }

    /// <summary>Starts the gateway on a free local port. The task gives its address once it listens.</summary>
    (Process Running, Task<Uri> Ready) Launch()
    {
        Directory.CreateDirectory(dataDirectory);
        var port = FreePort(demo ? 4190 : 4180);
        var start = new ProcessStartInfo(node, [script])
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var key in start.Environment.Keys.ToList())
            if (key.StartsWith("EE_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)
                || key is "NODE_OPTIONS" or "NODE_PATH")
                start.Environment.Remove(key);
        if (packaged) start.Environment["EE_RUNTIME_MODE"] = "packaged";
        start.Environment["EE_PREVIEW_LIVE"] = demo ? "0" : "1";
        start.Environment["EE_PREVIEW_PORT"] = port.ToString();
        start.Environment["EE_WORKSPACE_DATA_DIR"] = dataDirectory;
        // The gateway closes the workspace, confirming lighting cleanup, when this program's handle to it closes.
        start.Environment["EE_DESKTOP_HOST"] = "1";
        var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = $"Browser preview: http://127.0.0.1:{port}";
        var running = new Process { StartInfo = start, EnableRaisingEvents = true };
        running.OutputDataReceived += (_, line) =>
        {
            if (line.Data?.Trim() == expected) ready.TrySetResult(new Uri($"http://127.0.0.1:{port}/"));
        };
        running.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is not { } text) return;
            output.Enqueue(DesktopLog.Clean(text));
            while (output.Count > 64) output.TryDequeue(out var _);
        };
        running.Exited += async (_, _) =>
        {
            ready.TrySetException(new InvalidOperationException("The EE service stopped while starting."));
            // Until Address is set StartAsync reports the failure, and a requested stop is expected.
            lock (stopLock)
                if (Address is null || stopping is not null || forcing is not null) return;
            await Drain(running).ConfigureAwait(false);
            DesktopLog.Service("service_stopped", running.ExitCode, output);
            Stopped?.Invoke(running.ExitCode);
        };
        running.Start();
        // Only a started process is kept: Running and Kill would throw for one that never ran.
        process = running;
        return (running, ready.Task);
    }

    /// <summary>Waits briefly for the last output lines of a service that has exited.</summary>
    static async Task Drain(Process ended)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await ended.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Asks the gateway to close the workspace, which returns the switch lighting, and waits for the service to
    /// exit. True only when that is confirmed. Concurrent callers share one close request.
    /// <para>Without <paramref name="force"/> an unconfirmed close leaves the service running, so the user can try
    /// again. With it (a second Quit, the installer, Windows sign-out) the gateway first gets its own cleanup
    /// attempts after its input closes, and is ended only if it still has not exited; the unconfirmed exit is then
    /// recorded so the next start recovers the lighting.</para>
    /// </summary>
    public async Task<bool> StopAsync(bool force = true)
    {
        Task<bool> close;
        lock (stopLock) close = stopping ??= Task.Run(CloseAsync);
        if (await close.ConfigureAwait(false)) return true;
        if (!force)
        {
            // Let a later request ask the gateway again.
            lock (stopLock) if (stopping == close) stopping = null;
            return false;
        }
        Task<bool> ending;
        lock (stopLock) ending = forcing ??= Task.Run(EndAsync);
        return await ending.ConfigureAwait(false);
    }

    async Task<bool> CloseAsync()
    {
        var running = process;
        if (running is null) return true;
        // A service that never became ready opened no workspace, so there is nothing to return.
        if (running.HasExited) return running.ExitCode == 0 || Address is null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            if (Address is { } address)
            {
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                using var response = await http.PostAsync(new Uri(address, "/api/workspace/close"), null, deadline.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return false;
                var body = Json.Parse(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false));
                if (body.Bool("closed") != true) return false;
            }
            running.StandardInput.Close();
            await running.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return running.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    async Task<bool> EndAsync()
    {
        var running = process;
        if (running is null) return true;
        // Closing the gateway's input starts its own cleanup: up to four attempts five seconds apart.
        try { running.StandardInput.Close(); } catch (Exception) { }
        try
        {
            await running.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false);
            if (running.ExitCode == 0) return true;
        }
        catch (Exception) { }
        Kill();
        if (Address is not null) Locations.RecordUnconfirmedExit(dataDirectory);
        return false;
    }

    /// <summary>Ends only the gateway started by this instance and its local descendants. True once it has exited.</summary>
    public bool Kill()
    {
        var running = process;
        if (running is null || running.HasExited) return true;
        try { running.Kill(entireProcessTree: true); }
        catch (Exception) when (running.HasExited) { return true; }
        catch (Exception) { return false; }
        return running.WaitForExit(5000);
    }

    static int FreePort(int preferred)
    {
        for (var port = preferred; port < preferred + 20; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException) { }
        }
        throw new InvalidOperationException("No local port is free for the EE workspace.");
    }
}
