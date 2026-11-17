using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EtherlightingEvolved.Native.Core;

/// <summary>A refused or unconfirmed request, with the same fields the browser workspace reads.</summary>
public sealed class ApiException(string message, string? code, int status, bool uncertain, JsonObject? details) : Exception(message)
{
    public string? Code { get; } = code;

    public int Status { get; } = status;

    /// <summary>True when a change may or may not have been applied, so the current setup must be read again.</summary>
    public bool Uncertain { get; } = uncertain;

    public JsonObject Details { get; } = details ?? [];
}

/// <summary>
/// Requests to the EE workspace gateway. A port of the browser workspace's request helper (preview/api.mjs): the same
/// routes, bodies, timeout and messages. The engine validates every change and keeps every switch safety check.
/// </summary>
public sealed class EeApi : IDisposable
{
    // The gateway answers these itself. The key goes only to them, so no engine route or redirect can receive it.
    static readonly HashSet<string> AdminPaths = new(StringComparer.Ordinal)
    {
        "/api/private-access", "/api/private-access/refresh", "/api/private-access/login", "/api/lan-access", "/api/lan-access/code",
        "/api/workspace/close", "/api/workspace/admin-link",
    };
    // Only 16 lowercase hexadecimal characters follow, so the path has no dot segment or escape that HttpClient rewrites
    // before sending it.
    static readonly Regex LanDevicePath = new("^/api/lan-access/devices/[0-9a-f]{16}\\z");
    static readonly Regex KeyPattern = new("^[0-9a-f]{64}\\z");
    // The requests that carry switch or UniFi passwords and keys. A LAN connection is not encrypted, so they are never
    // sent to a LAN address, whatever its answers say.
    static readonly HashSet<(string Method, string Path)> SecretRequests =
    [
        ("POST", "/api/session/login"), ("POST", "/api/controller/connect"), ("POST", "/api/controller/consoles"), ("PUT", "/api/controller/preferences"),
    ];
    const string PairingRequired = "Pair this device first. Enter the pairing code shown on the computer running EE.";
    readonly HttpClient http;
    readonly string? adminKey;

    /// <param name="adminKey">The administration key of a gateway this program started; null for any other workspace.</param>
    /// <param name="deviceKey">The key this computer received when it paired with a LAN workspace.</param>
    public EeApi(Uri baseUri, string? adminKey = null, string? deviceKey = null)
    {
        BaseUri = baseUri;
        Lan = WorkspaceAddress.IsLan(baseUri);
        if (Lan && adminKey is not null) throw new ArgumentException("A LAN workspace never receives the administration key.", nameof(adminKey));
        this.adminKey = adminKey;
        DeviceKey = deviceKey;
        http = new HttpClient(WorkspaceTransport.CreateHandler(baseUri, DecompressionMethods.All))
        {
            BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30),
        };
        // The gateway accepts changes only from its own origin.
        http.DefaultRequestHeaders.Add("Origin", baseUri.GetLeftPart(UriPartial.Authority));
    }

    public Uri BaseUri { get; }

    /// <summary>The workspace is reached through LAN access: plain HTTP to a private address, opened with a pairing.</summary>
    public bool Lan { get; }

    /// <summary>The paired device key a LAN workspace receives in its ee_lan_ cookie; unused for any other address.</summary>
    internal string? DeviceKey { get; set; }

    internal static bool IsAdminPath(string path) => AdminPaths.Contains(path) || LanDevicePath.IsMatch(path);

    string DeviceCookie(string key) => $"ee_lan_{BaseUri.Port}={key}";

    internal Task ConnectStreamAsync(ClientWebSocket socket, CancellationToken token)
    {
        if (Lan)
        {
            if (DeviceKey is not { } key) throw new ApiException(PairingRequired, "lan_pairing_required", 403, false, null);
            socket.Options.SetRequestHeader("Cookie", DeviceCookie(key));
        }
        return WorkspaceTransport.ConnectStreamAsync(socket, BaseUri, http, token);
    }

    public Task<JsonNode?> GetAsync(string path, CancellationToken token = default) => RequestAsync(path, "GET", null, null, token);

    public Task<JsonNode?> PutAsync(string path, JsonNode? body, string? workspace = null) => RequestAsync(path, "PUT", body, workspace);

    public Task<JsonNode?> PostAsync(string path, JsonNode? body = null, string? workspace = null) => RequestAsync(path, "POST", body, workspace);

    public Task<JsonNode?> DeleteAsync(string path, JsonNode? body = null, string? workspace = null) => RequestAsync(path, "DELETE", body, workspace);

    public async Task<JsonNode?> RequestAsync(string path, string method, JsonNode? body = null, string? workspace = null, CancellationToken token = default)
    {
        if (!path.StartsWith("/api/", StringComparison.Ordinal)) throw new ArgumentException("Invalid API route.", nameof(path));
        if (Lan)
        {
            var route = path.Split('?', 2)[0];
            if (SecretRequests.Contains((method, route)))
                throw new ApiException("This action is available only on the computer running EE.", "local_access_required", 403, false, null);
            // Until it pairs, a LAN workspace answers only these, so nothing else is sent.
            if (DeviceKey is null && !(method == "GET" && route == "/api/workspace" || method == "POST" && route == "/api/lan-access/pair"))
                throw new ApiException(PairingRequired, "lan_pairing_required", 403, false, null);
        }
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (workspace is not null) request.Headers.Add("X-EE-Workspace", workspace);
        if (adminKey is not null && IsAdminPath(path)) request.Headers.Add("X-EE-Admin-Key", adminKey);
        if (Lan && DeviceKey is { } key) request.Headers.Add("Cookie", DeviceCookie(key));
        return await SendAsync(request, method is not ("GET" or "HEAD"), token).ConfigureAwait(false);
    }

    /// <summary>
    /// Pairs this computer with a LAN workspace and returns the key from the ee_lan_ cookie the gateway sets. The key is
    /// never in the answer's body. The caller saves it and sets <see cref="DeviceKey"/>.
    /// </summary>
    public async Task<string> PairAsync(string code, string name)
    {
        if (!Lan) throw new InvalidOperationException("Only a LAN workspace pairs devices.");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/lan-access/pair")
        {
            Content = new StringContent(new JsonObject { ["code"] = code, ["name"] = name, ["client"] = "app" }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var prefix = $"ee_lan_{BaseUri.Port}=";
        string? key = null;
        await SendAsync(request, true, CancellationToken.None, response =>
        {
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                key = cookies.Select(cookie => cookie.Split(';', 2)[0]).Where(pair => pair.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(pair => pair[prefix.Length..]).FirstOrDefault(KeyPattern.IsMatch);
        });
        return key ?? throw new ApiException("The response could not be read. Refresh the current setup before retrying this change.", "invalid_response", 200, true, null);
    }

    async Task<JsonNode?> SendAsync(HttpRequestMessage request, bool writing, CancellationToken token, Action<HttpResponseMessage>? accepted = null)
    {
        using (request)
        {
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
            {
                throw new ApiException(writing
                        ? "The connection was interrupted before this change could be confirmed. Refresh the current setup before retrying."
                        : "The workspace could not be reached. Check its connection and try again.",
                    "connection_unavailable", 0, writing, null);
            }
            using (response)
            {
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode) accepted?.Invoke(response);
                if (response.IsSuccessStatusCode && status is 204 or 205) return null;
                JsonNode? result;
                try
                {
                    var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    result = JsonNode.Parse(text);
                }
                catch (Exception exception) when (exception is JsonException or HttpRequestException)
                {
                    throw new ApiException(response.IsSuccessStatusCode && writing
                            ? "The response could not be read. Refresh the current setup before retrying this change."
                            : "The workspace returned an unreadable response. Refresh and try again.",
                        "invalid_response", status, writing, null);
                }
                if (response.IsSuccessStatusCode) return result;
                throw FromError(status, writing, result);
            }
        }
    }

    /// <summary>
    /// The exception for a refused request. The detail is a message, an object with a message and code, or another
    /// shape (such as a validation list) that gets the general message, as in the browser workspace.
    /// </summary>
    internal static ApiException FromError(int status, bool writing, JsonNode? body)
    {
        var detail = body is JsonObject json ? json["detail"] : null;
        var fields = detail as JsonObject;
        var message = detail.Str() ?? fields.Str("message");
        return new ApiException(message is { Length: > 0 } ? message : "The change could not be confirmed. Try again.", fields.Str("code"), status,
            writing && status >= 500, fields);
    }

    public void Dispose() => http.Dispose();
}
