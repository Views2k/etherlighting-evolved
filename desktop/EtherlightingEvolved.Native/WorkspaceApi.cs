using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native;

/// <summary>
/// Lighting changes through the EE workspace API. The engine validates every request and keeps all switch safety
/// checks; this class only builds the same requests the browser workspace sends.
/// </summary>
public sealed class WorkspaceApi : IDisposable
{
    readonly HttpClient http;

    public WorkspaceApi(Uri baseUri)
    {
        http = new HttpClient(WorkspaceTransport.CreateHandler(baseUri)) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(45) };
        // The private Tailscale gateway requires the workspace origin on changes; the local gateway accepts it.
        http.DefaultRequestHeaders.Add("Origin", baseUri.GetLeftPart(UriPartial.Authority));
    }

    /// <summary>Runs the selected effect on the selected ports. Returns null on success or a message to show.</summary>
    public async Task<string?> ApplyEffectAsync(LightingState state, IReadOnlyCollection<int> ports, string effectId, JsonObject parameters)
    {
        var selected = ports.Order().ToList();
        var exact = state.Zones.FirstOrDefault(zone => zone.Ports.Order().SequenceEqual(selected));
        var effect = new JsonObject { ["id"] = effectId, ["params"] = parameters.DeepClone(), ["clear_overrides"] = true };
        if (exact is not null) return await SendAsync(HttpMethod.Put, $"/api/zones/{Uri.EscapeDataString(exact.Id)}/effect", effect);
        var (zones, id) = LightingModel.Regroup(state, selected, effectId, parameters);
        if (await SendAsync(HttpMethod.Put, "/api/zones", new JsonObject { ["zones"] = zones }) is { } error) return error;
        return await SendAsync(HttpMethod.Put, $"/api/zones/{Uri.EscapeDataString(id)}/effect", effect);
    }

    public Task<string?> ApplyColorAsync(IEnumerable<int> ports, string color, int brightness)
    {
        var map = new JsonObject();
        foreach (var port in ports) map[port.ToString()] = new JsonObject { ["color"] = color, ["brightness"] = brightness };
        return SendAsync(HttpMethod.Put, "/api/ports", new JsonObject { ["ports"] = map });
    }

    public async Task<string?> FollowEffectAsync(IEnumerable<int> ports)
    {
        foreach (var port in ports)
            if (await SendAsync(HttpMethod.Delete, $"/api/ports/{port}", null) is { } error) return error;
        return null;
    }

    public async Task<string?> ApplySfpAsync(IEnumerable<int> ports, JsonObject settings)
    {
        foreach (var port in ports)
            if (await SendAsync(HttpMethod.Put, $"/api/sfp/{port}", settings.DeepClone()) is { } error) return error;
        return null;
    }

    public Task<string?> SetBrightnessAsync(double value) =>
        SendAsync(HttpMethod.Put, "/api/brightness", new JsonObject { ["value"] = Math.Round(Math.Clamp(value, 0, 100)) });

    public Task<string?> SetReleasedAsync(bool release) => SendAsync(HttpMethod.Post, release ? "/api/release" : "/api/resume", null);

    public Task<string?> ApplyProfileAsync(string name) =>
        SendAsync(HttpMethod.Post, $"/api/presets/{Uri.EscapeDataString(name)}/apply", null);

    public async Task<IReadOnlyList<string>> ProfilesAsync()
    {
        try
        {
            using var document = JsonDocument.Parse(await http.GetStringAsync("/api/presets"));
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? document.RootElement.EnumerateObject().Select(item => item.Name).Order(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
        }
        catch (Exception) { return []; }
    }

    async Task<string?> SendAsync(HttpMethod method, string path, JsonNode? body)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return null;
            return Message(await response.Content.ReadAsStringAsync()) ?? $"EE refused the change ({(int)response.StatusCode}).";
        }
        catch (TaskCanceledException) { return "EE did not answer in time. Check the switch connection and try again."; }
        catch (HttpRequestException) { return "EE is not reachable. Check that it is running and try again."; }
    }

    static string? Message(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var detail = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("detail", out var value) ? value : default;
            return detail.ValueKind switch
            {
                JsonValueKind.String => detail.GetString(),
                JsonValueKind.Object => WorkspaceModel.Text(detail, "message"),
                JsonValueKind.Array => detail.EnumerateArray().Select(item => WorkspaceModel.Text(item, "msg")).FirstOrDefault(text => text is not null),
                _ => null,
            };
        }
        catch (JsonException) { return null; }
    }

    public void Dispose() => http.Dispose();
}
