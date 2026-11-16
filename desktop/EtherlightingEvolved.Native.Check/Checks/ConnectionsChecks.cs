using System.Text.Json.Nodes;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Pages;

namespace EtherlightingEvolved.Native.Check.Checks;

/// <summary>The connection messages and checks that the connection view and Settings share.</summary>
internal static class ConnectionsChecks
{
    public static void Run(Action<bool, string> check)
    {
        var refused = new ApiException("", "auth_failed", 401, false, null);
        check(Connections.ConnectionError(refused) == Connections.Messages["auth_failed"]
              && Connections.ConnectionError(new TimeoutException()) == Connections.Messages["timeout"]
              && Connections.ConnectionError(JsonNode.Parse("""{"code":"unknown"}""")) == Connections.DefaultError
              && Connections.ConnectionError(JsonValue.Create("auth_failed")) == Connections.DefaultError,
            "connection errors read the code of a refused request or an error object, as the browser does");

        var discovery = JsonNode.Parse("""{"code":"controller_error","operation":"sites","upstream_status":502}""");
        check(Connections.ControllerError(discovery) == "UniFi Network could not complete site discovery (HTTP 502). Try connecting again."
              && Connections.ControllerError(new ApiException("", "timeout", 504, false, null)) == "UniFi Network did not respond in time. Check the controller and try again."
              && Connections.ControllerError(new ApiException("", "rate_limited", 429, false, null)) == Connections.ControllerMessages["rate_limited"],
            "controller errors name the failed step and its upstream status");

        check(Connections.PrivateAccessError(JsonValue.Create("forbidden")) == "Change this setting in EE on the host computer."
              && Connections.PrivateAccessUrl("https://host.tail1.ts.net:8444/") == "https://host.tail1.ts.net:8444"
              && Connections.PrivateAccessUrl("https://name@host.tail1.ts.net:8444/") is null
              && Connections.PrivateAccessUrl("https://host.tail1.ts.net/") is null,
            "private access reads an error code or object and accepts only a ts.net address with its own port");

        var target = new Connections.Target("https://console.example.test", "unifi_os", "api_key", null);
        var status = JsonNode.Parse("""
            {"configured":true,"url":"https://console.example.test","controller_kind":"unifi_os","auth_method":"api_key",
             "supported_auth_methods":{"self_hosted":["password"],"unifi_os":["password","api_key"],"cloud_connector":["api_key"]}}
            """)!.AsObject();
        var reused = Connections.ControllerConnectionProblem(target, "", "", "", status);
        status["configured"] = false;
        var missing = Connections.ControllerConnectionProblem(target, "", "", "", status);
        var spaced = Connections.ControllerConnectionProblem(target, "", "", "key with spaces", status);
        check(reused is null && missing?.Field == "api_key" && spaced?.Message == Connections.ControllerMessages["invalid_api_key"],
            "a saved controller target reuses its key, and a new one needs a valid key");

        var handedOff = JsonNode.Parse("""
            {"handed_off":true,"handoff_state":"complete","logged_in":false,"connected":false,"automatic_connect_paused":true}
            """);
        check(Connections.HandoffComplete(handedOff) && Connections.HandoffStatus(handedOff)?.Tone == "online"
              && Connections.HandoffBlocked(JsonNode.Parse("""{"handoff_state":"unconfirmed"}""")),
            "handoff status distinguishes a completed handoff from one that blocks reconnecting");
    }
}
