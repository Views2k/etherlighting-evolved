using System.Net;
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
    readonly HttpClient http;

    public EeApi(Uri baseUri)
    {
        BaseUri = baseUri;
        http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.All })
        {
            BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30),
        };
        // The gateway accepts changes only from its own origin.
        http.DefaultRequestHeaders.Add("Origin", baseUri.GetLeftPart(UriPartial.Authority));
    }

    public Uri BaseUri { get; }

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
            var detail = result is JsonObject json ? json["detail"] : null;
            var message = detail is JsonValue value && value.TryGetValue<string>(out var text2) ? text2
                : detail?["message"]?.GetValue<string>() is { Length: > 0 } nested ? nested
                : "The change could not be confirmed. Try again.";
            throw new ApiException(message, detail is JsonObject ? detail["code"]?.GetValue<string>() : null, status, writing && status >= 500,
                detail as JsonObject);
        }
    }

    public void Dispose() => http.Dispose();
}
