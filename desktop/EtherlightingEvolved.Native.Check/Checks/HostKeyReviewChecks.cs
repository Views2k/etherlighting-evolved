using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Pages;
using EtherlightingEvolved.Native.Pages.Connect;

namespace EtherlightingEvolved.Native.Check.Checks;

/// <summary>The SSH key review of the Connect page: the same problem rules as the browser and the review panel.</summary>
internal static class HostKeyReviewChecks
{
    static readonly string FpA = "SHA256:" + new string('A', 43);
    static readonly string FpB = "SHA256:" + new string('B', 42) + "A";

    static JsonObject Untrusted(Action<JsonObject>? change = null)
    {
        var error = new JsonObject
        {
            ["code"] = "host_key_untrusted", ["message"] = "Check this switch’s SSH key fingerprint before signing in.", ["host"] = "192.168.1.2",
            ["port"] = 22, ["ssh_host_key"] = FpA, ["ssh_host_key_type"] = "ssh-ed25519",
        };
        change?.Invoke(error);
        return error;
    }

    static JsonObject Changed(Action<JsonObject>? change = null) => Untrusted(error =>
    {
        error["code"] = "host_key_changed";
        error["ssh_host_key"] = FpB;
        error["ssh_host_key_type"] = "ssh-rsa";
        error["saved_ssh_host_key"] = FpA;
        change?.Invoke(error);
    });

    public static void Run(Action<bool, string> check)
    {
        Problems(check);
        Flow(check);
        Review(check);
    }

    static void Problems(Action<bool, string> check)
    {
        var untrusted = HostKeyProblem.From(Untrusted());
        var wrapped = HostKeyProblem.From(new JsonObject { ["code"] = "host_key_changed", ["details"] = Changed(error => { error["port"] = 2222; error["host"] = " Switch.Example "; }) });
        check(untrusted is { Changed: false, Port: 22, KeyType: "ssh-ed25519", SavedFingerprint: null, Address: "192.168.1.2", Route: "/api/session/host-key/accept" }
              && untrusted.Fingerprint == FpA
              && wrapped is { Changed: true, Address: "Switch.Example port 2222", Route: "/api/session/host-key/replace" }
              && wrapped.Fingerprint == FpB && wrapped.SavedFingerprint == FpA
              && wrapped.Key == string.Join('\n', "host_key_changed", "switch.example", "2222", FpB, FpA)
              && HostKeyProblem.From(Untrusted(error => error["ssh_host_key_type"] = "bad type"))?.KeyType is null,
            "host key problems are read from a session error or a refused request's details");

        var invalid = new JsonObject?[]
        {
            Untrusted(error => error["code"] = "auth_failed"), Untrusted(error => error["ssh_host_key"] = FpA[..^1]),
            Untrusted(error => error["port"] = "22"), Untrusted(error => error["port"] = true), Untrusted(error => error["port"] = 0),
            Untrusted(error => error["port"] = 22.5), Untrusted(error => error["host"] = ""), Untrusted(error => error["host"] = "   "),
            Untrusted(error => error["host"] = "switch\u0000"), Untrusted(error => error["host"] = new string('a', 254)),
            Changed(error => error.Remove("saved_ssh_host_key")), Changed(error => error["saved_ssh_host_key"] = FpB),
            Untrusted(error => error["code"] = "toString"), Untrusted(error => error["code"] = "__proto__"), Untrusted(error => error["code"] = "constructor"),
            Untrusted(error => error["ssh_host_key"] = FpA + "\n"), null,
        };
        check(invalid.All(error => HostKeyProblem.From(error) is null) && HostKeyProblem.From(JsonValue.Create("host_key_untrusted")) is null,
            "incomplete or malformed key errors give no review, as in the browser");

        check(Same(untrusted!.Body(), $$"""{"host":"192.168.1.2","port":22,"ssh_host_key":"{{FpA}}"}""")
              && Same(HostKeyProblem.From(Changed())!.Body(), $$"""{"host":"192.168.1.2","port":22,"ssh_host_key":"{{FpB}}","saved_ssh_host_key":"{{FpA}}"}""")
              && wrapped!.Body().Str("host") == " Switch.Example ",
            "the accept and replace bodies echo the key exactly as the service sent it");

        var changed = HostKeyProblem.From(Changed());
        string Error(string code) => Connections.HostKeyError(new ApiException("", code, 409, false, null), changed);
        check(Error("host_key_stale") == "The switch’s SSH key changed again. Connect again to review the current key."
              && Error("host_key_in_use").StartsWith("EE still has an open switch connection.", StringComparison.Ordinal)
              && Error("switch_playback_reserved").StartsWith("EE is handing lighting back to UniFi", StringComparison.Ordinal)
              && Error("host_key_save_failed") == "The SSH key could not be saved. Check the EE data folder and try again."
              && Error("invalid_credentials") == "The SSH key details were not accepted. Connect again to review the key."
              && Error("local_access_required") == "A new key can be accepted only in EE on the computer that runs it."
              && Connections.HostKeyError(new ApiException("", "local_access_required", 403, false, null), untrusted) == "This key can be accepted only in EE on the computer that runs it."
              && Error("unknown") == "The SSH key could not be saved. Connect again to review the key."
              && Connections.HostKeyError(new TimeoutException(), changed) == "The SSH key could not be saved. Connect again to review the key.",
            "refused key requests show the browser's messages");

        check(Connections.CanAcceptHostKey(JsonNode.Parse("""{"can_close_workspace":true,"local":true}"""))
              && Connections.CanAcceptHostKey(JsonNode.Parse("""{"can_configure":false,"can_close_workspace":false,"admin_required":true,"local":true}"""))
              && !Connections.CanAcceptHostKey(JsonNode.Parse("""{"can_configure":false,"can_close_workspace":false,"can_change_port":false}"""))
              && !Connections.CanAcceptHostKey(JsonNode.Parse("""{"local":false}"""))
              && !Connections.CanAcceptHostKey(JsonNode.Parse("{}")) && !Connections.CanAcceptHostKey(null),
            "only the local gateway's answer allows accepting a key, also without close access; the private gateway's answer and a failed request count as remote");

        check(Connections.ConnectionError(Untrusted()) == "Check the switch’s SSH key before EE sends your password."
              && Connections.ConnectionError(Changed()) == "The switch’s SSH key changed. Review the saved and new keys before connecting.",
            "key errors without a review show the browser's connection messages");
    }

    static void Flow(Action<bool, string> check)
    {
        var flow = new HostKeyFlow();
        var saved = JsonNode.Parse("""{"logged_in":false,"has_saved_login":true,"remembered":{"host":" 192.168.1.2 ","port":22}}""")!.AsObject();
        saved["error"] = Changed();
        var opened = flow.FromSession(saved, "", "22", false, false) && flow.Retry == HostKeyRetry.Saved;
        var repeated = !flow.FromSession(saved, "", "22", false, false) && flow.Retry == HostKeyRetry.Saved;
        flow.FromAction(HostKeyProblem.From(Changed())!, HostKeyRetry.Form);
        var action = flow.Retry == HostKeyRetry.Form;
        flow.TargetEdited();
        var edited = flow.Open is null;
        flow.FromSession(saved, "", "22", false, false);
        flow.Dismiss();
        var dismissed = !flow.FromSession(saved, "", "22", false, false) && flow.Open is null;
        flow.FromAction(HostKeyProblem.From(Changed())!, HostKeyRetry.Form);
        var reopened = flow.Open is not null;
        flow.Close();
        check(opened && repeated && action && edited && dismissed && reopened,
            "a session review opens once for the saved login, an action replaces its retry, and a dismissed key stays closed");

        var unsaved = new JsonObject { ["logged_in"] = false, ["error"] = Untrusted() };
        check(!flow.FromSession(unsaved, "", "22", false, false) && !flow.FromSession(unsaved, "other.example", "22", false, false)
              && !flow.FromSession(unsaved, "192.168.1.2", "2222", false, false) && !flow.FromSession(unsaved, "192.168.1.2", "22", true, false)
              && !flow.FromSession(unsaved, "192.168.1.2", "22", false, true) && flow.Open is null,
            "a session error for another address, or while a request or handoff is pending, opens no review");
        var form = flow.FromSession(unsaved, " 192.168.1.2 ", "22", false, false) && flow.Retry == HostKeyRetry.None;
        unsaved["logged_in"] = true;
        check(form && !flow.FromSession(unsaved, "192.168.1.2", "22", false, false) && flow.Open is null,
            "the address in the form opens a review that only asks to connect, and a signed-in session closes it");
    }

    static void Review(Action<bool, string> check)
    {
        var accepted = new List<HostKeyProblem>();
        var dismissed = new List<HostKeyProblem>();
        var review = new HostKeyReview(accepted.Add, dismissed.Add);
        var window = new Window { Width = 500, Height = 700, Content = new ScrollViewer { Content = review } };
        // The review's own text: button labels are left out, and fingerprints are read separately.
        List<string> Texts() => review.GetLogicalDescendants().OfType<TextBlock>()
            .Where(text => text is not SelectableTextBlock && text.IsEffectivelyVisible && text.FindLogicalAncestorOfType<Button>() is null)
            .Select(text => text.Text ?? "").ToList();
        List<string> Fingerprints() => review.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(text => text.Text ?? "").ToList();
        bool Shown(string text) => review.Buttons.Any(button => Equals(button.Content, text) && button.IsEffectivelyVisible);
        void Click(string text)
        {
            review.Buttons.Single(button => Equals(button.Content, text)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
        try
        {
            window.Show();
            var untrusted = HostKeyProblem.From(Untrusted())!;
            review.Show(untrusted);
            review.SetCanAccept(true);
            Dispatcher.UIThread.RunJobs();
            check(review.IsVisible && Texts().SequenceEqual([
                    "Check the switch’s SSH key",
                    "EE has no saved SSH key for 192.168.1.2. Check that this fingerprint belongs to your switch. EE sends your password only after you accept the key.",
                    "Fingerprint (ssh-ed25519)",
                    "An SSH client that has connected to this switch shows the same fingerprint for the same key type.",
                ]) && Fingerprints().SequenceEqual([FpA]) && Shown("Accept key") && Shown("Cancel"),
                "a first key shows its address, type and fingerprint with Accept key and Cancel");
            Click("Accept key");
            Click("Cancel");
            check(accepted.SequenceEqual([untrusted]) && ReferenceEquals(accepted[0], untrusted) && dismissed.SequenceEqual([untrusted]),
                "each review button reports its problem once");

            var changed = HostKeyProblem.From(Changed())!;
            review.Show(changed);
            Dispatcher.UIThread.RunJobs();
            var waiting = !Shown("Accept new key") && Shown("Keep saved key") && !Texts().Contains("A new key can be accepted only in EE on the computer that runs it.");
            review.SetCanAccept(false);
            Dispatcher.UIThread.RunJobs();
            var remote = !Shown("Accept new key") && Texts().Last() == "A new key can be accepted only in EE on the computer that runs it.";
            check(waiting && remote, "Accept new key stays hidden until this computer is confirmed, and a remote client sees where to accept it");
            review.SetCanAccept(true);
            Dispatcher.UIThread.RunJobs();
            check(Texts().SequenceEqual([
                    "The switch’s SSH key changed",
                    "The device at 192.168.1.2 presented a different SSH key than the one EE saved. This happens after the switch is reset or replaced. If you did not reset or replace it, another device may be using its address. In that case, keep the saved key and check your network.",
                    "Lighting, schedules and port names saved for the previous switch stay saved but are not used with the new key.",
                    "Saved key",
                    "New key (ssh-rsa)",
                ]) && Fingerprints().SequenceEqual([FpA, FpB]) && Shown("Accept new key") && Shown("Keep saved key")
                && !review.Buttons[0].Classes.Contains("primary"),
                "a changed key shows the saved and new fingerprints with Accept new key and Keep saved key");
            review.Hide();
            check(!review.IsVisible && review.Problem is null, "hiding the review forgets its problem");
        }
        finally
        {
            window.Close();
        }
    }

    static bool Same(JsonNode? value, string expected) => JsonNode.DeepEquals(value, JsonNode.Parse(expected));
}
