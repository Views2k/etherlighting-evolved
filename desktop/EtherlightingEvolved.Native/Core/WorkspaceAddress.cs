namespace EtherlightingEvolved.Native.Core;

/// <summary>
/// The workspace addresses a window opens, from the address dialog, --url, EE_WORKSPACE_URL or the saved address:
/// the private HTTPS workspace address, or HTTP on this computer, as an origin without account details, path, query
/// or fragment.
/// </summary>
public static class WorkspaceAddress
{
    public static Uri? Parse(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var address) &&
        (address.Scheme == "https" || address.Scheme == "http" && address.IsLoopback) &&
        address.UserInfo.Length == 0 && address.AbsolutePath == "/" && address.Query.Length == 0 && address.Fragment.Length == 0
            ? address : null;
}
