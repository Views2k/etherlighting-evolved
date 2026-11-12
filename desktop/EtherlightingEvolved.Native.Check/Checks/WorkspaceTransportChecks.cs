using System.Net;
using System.Net.Sockets;

namespace EtherlightingEvolved.Native.Check.Checks;

internal static class WorkspaceTransportChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var workspace = new Uri("https://ee.example.ts.net:8444");
        check(WorkspaceTransport.UsesPeerLookup(workspace, true) && !WorkspaceTransport.UsesPeerLookup(workspace, false),
            "only Linux uses the Tailscale fallback");
        foreach (var uri in new[] { "http://ee.example.ts.net", "http://localhost:4180", "https://example.org", "https://ee.example.ts.net.invalid",
                                    "https://ts.net", "https://example.ts.net", "https://extra.ee.example.ts.net", "https://user@ee.example.ts.net" })
            check(!WorkspaceTransport.UsesPeerLookup(new Uri(uri), true), "ordinary URLs and other authorities use the system connection");
        check(WorkspaceTransport.IsWorkspaceEndpoint(workspace, new DnsEndPoint("EE.EXAMPLE.TS.NET", 8444)) &&
            !WorkspaceTransport.IsWorkspaceEndpoint(workspace, new DnsEndPoint("other.example.ts.net", 8444)) &&
            !WorkspaceTransport.IsWorkspaceEndpoint(workspace, new DnsEndPoint(workspace.Host, 443)),
            "peer resolution is confined to the exact configured hostname and port");

        var normal = new[] { IPAddress.IPv6Loopback };
        var peer = WorkspaceTransport.ParsePeerAddresses("100.64.0.8\nfd7a:115c:a1e0::8\n");
        check(peer.Length == 2 && peer[0].AddressFamily == AddressFamily.InterNetwork && peer[1].AddressFamily == AddressFamily.InterNetworkV6,
            "the installed CLI's IPv4 and IPv6 results are both available");
        foreach (var output in new[] { "", "127.0.0.1", "100.63.255.255", "100.128.0.1", "8.8.8.8", "fd7a:115c:a1df::8", "100.64.8",
                                       "::ffff:100.64.0.8", "fd7a:115c:a1e0::8%2", "100.64.0.8\nunexpected output" })
        {
            try { WorkspaceTransport.ParsePeerAddresses(output); check(false, "invalid CLI output rejected"); }
            catch (IOException) { check(true, "invalid CLI output rejected"); }
        }

        int lookups = 0;
        Task<IPAddress[]> Peer(string host, CancellationToken token)
        {
            check(host == workspace.Host && !token.IsCancellationRequested, "peer lookup receives the configured name");
            lookups++;
            return Task.FromResult(peer);
        }
        Task<IPAddress[]> Resolve(Func<string, CancellationToken, Task<IPAddress[]>> dns, CancellationToken token = default) =>
            WorkspaceTransport.ResolveAsync(workspace.Host, dns, Peer, token);

        var answer = await Resolve((_, _) => Task.FromResult(normal));
        check(answer == normal && lookups == 0, "working system DNS never launches a peer lookup");
        foreach (var error in new[] { SocketError.HostNotFound, SocketError.TryAgain, SocketError.NoData, SocketError.NoRecovery })
        {
            var fallback = await Resolve((_, _) => throw new SocketException((int)error));
            check(fallback == peer, "a name resolution failure uses the private peer");
        }
        check(await Resolve((_, _) => Task.FromResult(Array.Empty<IPAddress>())) == peer, "empty system DNS result uses the private peer");
        int before = lookups;
        try { await Resolve((_, _) => throw new SocketException((int)SocketError.ConnectionRefused)); check(false, "connection refusal retained"); }
        catch (SocketException error) { check(error.SocketErrorCode == SocketError.ConnectionRefused && before == lookups, "connection refusal never triggers a peer lookup"); }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await Resolve((_, _) => throw new SocketException((int)SocketError.HostNotFound), cancelled.Token); check(false, "cancellation retained"); }
        catch (OperationCanceledException) { check(before == lookups, "a cancelled connection never launches a peer lookup"); }
    }
}
