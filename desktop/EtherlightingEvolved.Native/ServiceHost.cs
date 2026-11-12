using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace EtherlightingEvolved.Native;

/// <summary>
/// Starts and stops the EE service: the local workspace gateway, which starts the lighting engine. Shutdown goes
/// through the gateway's confirmed close, which returns the switch lighting before the engine exits.
/// </summary>
public sealed class ServiceHost
{
    readonly string node, script, root, dataDirectory;
    readonly bool packaged, demo;
    Process? process;
    TaskCompletionSource<Uri>? ready;

    ServiceHost(string node, string script, string root, bool packaged, bool demo, string dataDirectory)
    {
        (this.node, this.script, this.root, this.packaged, this.demo, this.dataDirectory) = (node, script, root, packaged, demo, dataDirectory);
    }

    public Uri? Address { get; private set; }

    public bool Running => process is { HasExited: false };

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
        ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = $"Browser preview: http://127.0.0.1:{port}";
        process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, line) =>
        {
            if (line.Data?.Trim() == expected) ready.TrySetResult(new Uri($"http://127.0.0.1:{port}/"));
        };
        process.ErrorDataReceived += (_, _) => { };
        process.Exited += (_, _) => ready.TrySetException(new InvalidOperationException("The EE service stopped while starting."));
        if (!process.Start()) throw new InvalidOperationException("The EE service could not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            var address = await ready.Task.WaitAsync(TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
            using var workspace = JsonDocument.Parse(await http.GetStringAsync(new Uri(address, "/api/workspace"), token).ConfigureAwait(false));
            if (WorkspaceModel.Bool(workspace.RootElement, "live") != !demo) throw new InvalidOperationException("The EE workspace could not be verified.");
            Address = address;
            return address;
        }
        catch
        {
            Kill();
            throw;
        }
    }

    /// <summary>Asks the gateway to return the lighting and stop the engine. True only when shutdown is confirmed.</summary>
    public async Task<bool> StopAsync()
    {
        var running = process;
        if (running is null) return true;
        if (running.HasExited) return running.ExitCode == 0;
        if (Address is { } address)
        {
            try
            {
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(45) };
                using var response = await http.PostAsync(new Uri(address, "/api/workspace/close"), null).ConfigureAwait(false);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                if (WorkspaceModel.Bool(body.RootElement, "closed") != true) return false;
            }
            catch (Exception) { return false; }
        }
        try { running.StandardInput.Close(); } catch (Exception) { }
        try { await running.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
        return running.ExitCode == 0;
    }

    /// <summary>Last resort after an unconfirmed shutdown. EE recovers the lighting record at its next start.</summary>
    public void Kill()
    {
        var running = process;
        if (running is null || running.HasExited) return;
        try { running.StandardInput.Close(); } catch (Exception) { }
        if (running.WaitForExit(8000)) return;
        try { running.Kill(entireProcessTree: true); } catch (Exception) { }
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
