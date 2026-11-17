using System.Net;

namespace EtherlightingEvolved.Native.Core;

/// <summary>
/// The workspace addresses a window opens, from the address dialog, --url, EE_WORKSPACE_URL or the saved address: the
/// Tailscale private HTTPS address, a LAN address, or HTTP on this computer, as an origin without account details, path,
/// query or fragment.
/// </summary>
public static class WorkspaceAddress
{
    public static Uri? Parse(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var address) &&
        (address.Scheme == "https" || address.Scheme == "http" && (address.IsLoopback || IsLan(address))) &&
        address.UserInfo.Length == 0 && address.AbsolutePath == "/" && address.Query.Length == 0 && address.Fragment.Length == 0
            ? address : null;

    /// <summary>Plain HTTP to a private IPv4 literal on a port other than 80. Opened only with a pairing.</summary>
    public static bool IsLan(Uri address) =>
        address.Scheme == "http" && address.HostNameType == UriHostNameType.IPv4 && !address.IsDefaultPort &&
        IPAddress.TryParse(address.Host, out var ip) && ip.ToString() == address.Host &&
        ip.GetAddressBytes() is [10, _, _, _] or [172, >= 16 and <= 31, _, _] or [192, 168, _, _];
}
