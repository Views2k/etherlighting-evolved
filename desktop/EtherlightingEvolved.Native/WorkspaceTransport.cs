using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace EtherlightingEvolved.Native;

internal static class WorkspaceTransport
{
    internal static bool UsesPeerLookup(Uri workspace, bool linux) =>
        linux && workspace.Scheme == Uri.UriSchemeHttps && workspace.UserInfo.Length == 0 &&
        workspace.HostNameType == UriHostNameType.Dns && workspace.IdnHost.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) &&
        workspace.IdnHost.Split('.').Length == 4;

    internal static SocketsHttpHandler CreateHandler(Uri workspace, DecompressionMethods decompression = DecompressionMethods.None)
    {
        var handler = new SocketsHttpHandler { UseProxy = false, AutomaticDecompression = decompression };
        if (!UsesPeerLookup(workspace, OperatingSystem.IsLinux())) return handler;
        handler.ConnectCallback = async (context, token) =>
        {
            var endpoint = context.DnsEndPoint;
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                if (IsWorkspaceEndpoint(workspace, endpoint))
                {
                    var addresses = await ResolveAsync(endpoint.Host, Dns.GetHostAddressesAsync, LookupPeerAsync, token).ConfigureAwait(false);
                    await socket.ConnectAsync(addresses, endpoint.Port, token).ConfigureAwait(false);
                }
                else await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
                // TLS and the request authority still use the workspace name.
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        };
        return handler;
    }

    internal static bool IsWorkspaceEndpoint(Uri workspace, DnsEndPoint endpoint) =>
        endpoint.Port == workspace.Port && string.Equals(endpoint.Host, workspace.IdnHost, StringComparison.OrdinalIgnoreCase);

    internal static async Task<IPAddress[]> ResolveAsync(string host, Func<string, CancellationToken, Task<IPAddress[]>> dns,
        Func<string, CancellationToken, Task<IPAddress[]>> peer, CancellationToken token)
    {
        try
        {
            var addresses = await dns(host, token).ConfigureAwait(false);
            if (addresses.Length > 0) return addresses;
        }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain or SocketError.NoRecovery) { }
        token.ThrowIfCancellationRequested();
        return await peer(host, token).ConfigureAwait(false);
    }

    static async Task<IPAddress[]> LookupPeerAsync(string host, CancellationToken token)
    {
        var start = new ProcessStartInfo("/usr/bin/tailscale")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "ip", host + "." },
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception) { throw new IOException("Tailscale could not resolve the workspace address."); }
        try
        {
            var drain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            var buffer = new char[257];
            var length = await process.StandardOutput.ReadBlockAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
            if (length == buffer.Length) throw new IOException("Tailscale returned an invalid peer address.");
            await Task.WhenAll(drain, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Tailscale could not resolve the workspace address.");
            return ParsePeerAddresses(new string(buffer, 0, length));
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { } // The lookup exited between HasExited and Kill.
        }
    }

    internal static IPAddress[] ParsePeerAddresses(string value)
    {
        var lines = value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length is < 1 or > 2) throw new IOException("Tailscale returned an invalid peer address.");
        var result = new List<IPAddress>(lines.Length);
        foreach (var line in lines)
        {
            if (!IPAddress.TryParse(line, out var ip) || line.Contains('%') || !line.Equals(ip.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Tailscale returned an invalid peer address.");
            var bytes = ip.GetAddressBytes();
            if (!(bytes is [100, >= 64 and <= 127, _, _] or [0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0, ..]))
                throw new IOException("Tailscale returned an invalid peer address.");
            result.Add(ip);
        }
        return [.. result];
    }

    internal static Task ConnectStreamAsync(ClientWebSocket socket, Uri workspace, HttpClient http, CancellationToken token)
    {
        socket.Options.SetRequestHeader("Origin", workspace.GetLeftPart(UriPartial.Authority));
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var address = new UriBuilder(workspace) { Scheme = workspace.Scheme == "https" ? "wss" : "ws", Path = "/api/ws" }.Uri;
        return UsesPeerLookup(workspace, OperatingSystem.IsLinux())
            ? socket.ConnectAsync(address, http, token)
            : socket.ConnectAsync(address, token);
    }
}
