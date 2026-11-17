using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Pages;
using EtherlightingEvolved.Native.Ui;

namespace EtherlightingEvolved.Native.Check.Checks;

/// <summary>The connection messages, checks and controls that the connection view and Settings share.</summary>
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

        const string newer = "The saved lighting for this switch was saved by a newer version of EE. Update EE to use it.";
        const string damaged = "The saved lighting for this switch could not be verified.";
        check(Connections.ConnectionError(new ApiException(newer, "invalid_saved_scene", 422, false, null)) == newer
              && Connections.ConnectionError(JsonNode.Parse($$"""{"code":"invalid_saved_scene","message":"{{damaged}}"}""")) == damaged
              && Connections.ConnectionError(JsonNode.Parse("""{"code":"invalid_saved_scene"}""")) == Connections.DefaultError,
            "an unusable saved scene shows the service message that names its recovery, as the browser does");

        const string reserved = "Return switch playback to UniFi from the PC that saved it.";
        const string newerKeys = "SSH host keys were saved by a newer version of EE. Update EE to use them.";
        check(Connections.ConnectionError(new ApiException(reserved, "switch_playback_reserved", 409, false, null)) == reserved
              && Connections.ConnectionError(JsonNode.Parse($$"""{"code":"host_key_save_failed","message":"{{newerKeys}}"}""")) == newerKeys
              && Connections.ConnectionError(JsonNode.Parse("""{"code":"switch_playback_reserved"}""")) == Connections.DefaultError
              && Connections.ConnectionError(new ApiException("private-host", "output_unconfirmed", 409, false, null))
                  == "EE could not confirm the switch lighting. Check that the switch is reachable, then connect again."
              && Connections.ConnectionError(JsonNode.Parse("""{"code":"invalid_settings"}"""))
                  == "EE could not apply its lighting settings to this switch. Connect again; if this continues, check the advanced settings in ee.toml."
              && Connections.ConnectionError(JsonNode.Parse("""{"code":"scene_save_failed"}"""))
                  == "The lighting setup could not be saved. Check the EE data folder and try again.",
            "reserved playback and unusable saved SSH keys show the service message, and lighting that could not be confirmed, applied or saved has its own message, as in the browser");

        var discovery = JsonNode.Parse("""{"code":"controller_error","operation":"sites","upstream_status":502}""");
        check(Connections.ControllerError(discovery) == "UniFi Network could not complete site discovery (HTTP 502). Try connecting again."
              && Connections.ControllerError(new ApiException("", "timeout", 504, false, null)) == "UniFi Network did not respond in time. Check the controller and try again."
              && Connections.ControllerError(new ApiException("", "rate_limited", 429, false, null)) == Connections.ControllerMessages["rate_limited"],
            "controller errors name the failed step and its upstream status");

        check(Connections.PrivateAccessError(JsonValue.Create("forbidden")) == "Change this setting in EE on the host computer."
              && Connections.PrivateAccessUrl("https://host.example.ts.net:8444/") == "https://host.example.ts.net:8444"
              && Connections.PrivateAccessUrl("https://name@host.example.ts.net:8444/") is null
              && Connections.PrivateAccessUrl("https://host.example.ts.net/") is null,
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

        const string lighting = "Lighting returned to UniFi. Your saved logins and profiles are kept. Connect when you want EE to control lighting again.";
        ConnectionNotice? WithFans(string fans)
        {
            var session = handedOff!.DeepClone();
            session["fan_handoff"] = JsonNode.Parse(fans);
            return Connections.HandoffStatus(session);
        }
        var returned = WithFans("\"returned\"");
        var unconfirmed = WithFans("\"unconfirmed\"");
        check(returned is { Error: false, Tone: "online" } && returned.Message.StartsWith(lighting + " ", StringComparison.Ordinal)
              && returned.Message.Contains("runs the fans again", StringComparison.Ordinal)
              && unconfirmed is { Error: true, Tone: "" } && unconfirmed.Message.Contains("choose Handoff to UniFi to retry", StringComparison.Ordinal)
              && WithFans("null") == new ConnectionNotice(lighting, false, "online") && WithFans("\"not_running\"") == new ConnectionNotice(lighting, false, "online")
              && Connections.HandoffStatus(handedOff) == new ConnectionNotice(lighting, false, "online")
              && Connections.HandoffFans(JsonNode.Parse("""{"fan_handoff":"returned"}"""))?.Message == "The switch’s own controller runs the fans again."
              && Connections.HandoffFans(handedOff) is null,
            "connections: handoff status names the fan result");
        check(Connections.HandoffConfirmation(true, false).Contains("return the fans to the switch’s own controller", StringComparison.Ordinal)
              && !Connections.HandoffConfirmation(false, false).Contains("fan", StringComparison.Ordinal)
              && Connections.HandoffConfirmation(true, true).EndsWith(" Unapplied connection changes will be discarded.", StringComparison.Ordinal),
            "connections: the handoff confirmation names the fans only on a switch with fan control");

        // Help cards and the controller's setup help use the same disclosure as the connection options.
        var details = new Disclosure("Where do I find these details?", new TextBlock { Text = "Details" });
        void Press(Key key) => details.Summary.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Press(Key.Enter);
        var opened = details.Open && details.Body.IsVisible;
        Press(Key.Space);
        check(details.Summary.Focusable && opened && !details.Open && !details.Body.IsVisible, "a disclosure opens and closes from the keyboard");

        var actions = new FlowRow(10, new Border { Width = 40, Height = 34 }, new Border { Width = 40, Height = 34, IsVisible = false }, new Border { Width = 40, Height = 34 });
        actions.Measure(Size.Infinity);
        check(actions.DesiredSize == new Size(90, 34), "an action row in Connect or Settings leaves no gap for a hidden button");

        // Every page asks through the same confirmation dialog. The connection view passes itself as the owner and its
        // lifetime as the signal.
        var overlay = new Overlay();
        var owner = new object();
        using var leave = new CancellationTokenSource();
        var forget = overlay.Confirm("Forget saved login?", "Remove the saved switch login from this PC.", "Forget login", owner, leave.Token);
        var repeated = overlay.Confirm("Forget saved login?", "Remove the saved switch login from this PC.", "Forget login", owner, leave.Token);
        var other = overlay.Confirm("Retry handoff to UniFi?", "Stop EE lighting and return control to UniFi.", "Retry handoff", owner, leave.Token);
        var single = overlay.HasDialog && overlay.Children.Count == 2;
        leave.Cancel();
        var clock = Stopwatch.StartNew();
        while (!forget.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        check(single && ReferenceEquals(forget, repeated) && other.IsCompletedSuccessfully && !other.Result
              && forget.IsCompletedSuccessfully && !forget.Result && !overlay.HasDialog,
            "an owner has one confirmation at a time, and its canceled signal closes it as Cancel does");
    }
}
