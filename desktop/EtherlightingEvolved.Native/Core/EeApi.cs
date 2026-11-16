using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
        "/api/private-access", "/api/private-access/refresh", "/api/private-access/login", "/api/workspace/close", "/api/workspace/admin-link",
    };
    readonly HttpClient http;
    readonly string? adminKey;

    /// <param name="adminKey">The administration key of a gateway this program started; null for any other workspace.</param>
    public EeApi(Uri baseUri, string? adminKey = null)
    {
        BaseUri = baseUri;
        this.adminKey = adminKey;
        http = new HttpClient(WorkspaceTransport.CreateHandler(baseUri, DecompressionMethods.All))
        {
            BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30),
        };
        // The gateway accepts changes only from its own origin.
        http.DefaultRequestHeaders.Add("Origin", baseUri.GetLeftPart(UriPartial.Authority));
    }

    public Uri BaseUri { get; }

    internal Task ConnectStreamAsync(ClientWebSocket socket, CancellationToken token) =>
        WorkspaceTransport.ConnectStreamAsync(socket, BaseUri, http, token);

    public Task<JsonNode?> GetAsync(string path, CancellationToken token = default) => RequestAsync(path, "GET", null, null, token);

    public Task<JsonNode?> PutAsync(string path, JsonNode? body, string? workspace = null) => RequestAsync(path, "PUT", body, workspace);

    public Task<JsonNode?> PostAsync(string path, JsonNode? body = null, string? workspace = null) => RequestAsync(path, "POST", body, workspace);

    public Task<JsonNode?> DeleteAsync(string path, JsonNode? body = null, string? workspace = null) => RequestAsync(path, "DELETE", body, workspace);

    public async Task<JsonNode?> RequestAsync(string path, string method, JsonNode? body = null, string? workspace = null, CancellationToken token = default)
    {
        if (!path.StartsWith("/api/", StringComparison.Ordinal)) throw new ArgumentException("Invalid API route.", nameof(path));
        var writing = method is not ("GET" or "HEAD");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (workspace is not null) request.Headers.Add("X-EE-Workspace", workspace);
        if (adminKey is not null && AdminPaths.Contains(path)) request.Headers.Add("X-EE-Admin-Key", adminKey);
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
