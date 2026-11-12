using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EtherlightingEvolved.Native.Core;

namespace EtherlightingEvolved.Native.Pages.Settings;

/// <summary>A request bound to the current switch (the renderers' "request").</summary>
public delegate Task<JsonNode?> Send(string path, string method = "GET", JsonNode? body = null);

/// <summary>An error's code and details, from a refused request or from an error object in a status payload.</summary>
public readonly record struct Problem(string? Code, JsonObject Details)
{
    public static Problem From(Exception exception) => exception is ApiException api ? new(api.Code, api.Details) : new(null, []);

    public static Problem From(JsonNode? node) => node switch
    {
        JsonObject value => new(value.Str("code"), value),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => new(value.GetValue<string>(), []),
        _ => new(null, []),
    };
}

/// <summary>
/// The connection messages and checks from preview/connections.mjs that Settings uses: switch handoff, the UniFi
/// Network controller connection, and private (Tailscale) access.
/// </summary>
public static class Connections
{
    static readonly Dictionary<string, string> Messages = new(StringComparer.Ordinal)
    {
        ["auth_failed"] = "The switch rejected the login. Check your SSH username and password.",
        ["authentication_failed"] = "The switch rejected the login. Check your SSH username and password.",
        ["missing_host"] = "Enter the switch address.",
        ["missing_username"] = "Enter your SSH username.",
        ["bad_port"] = "Enter an SSH port between 1 and 65535.",
        ["host_key_changed"] = "The switch identity has changed. Verify its SSH host key before reconnecting.",
        ["unreachable"] = "The switch could not be reached. Check its address and that SSH is enabled.",
        ["connect_failed"] = "The switch could not be reached. Check its address and that SSH is enabled.",
        ["ssh_error"] = "SSH could not connect. Check the switch address and SSH settings.",
        ["no_etherlighting"] = "The switch connected, but its lighting service is unavailable. Check that this model and firmware support Etherlighting.",
        ["output_recovery_required"] = "An earlier lighting session did not stop cleanly and EE could not return the switch lighting to its defaults yet. Check that the switch is reachable, then connect again to retry the recovery.",
        ["handoff_unconfirmed"] = "Handoff could not be confirmed. Automatic switch connection is paused. Choose Retry handoff to complete cleanup before reconnecting.",
        ["handoff_recovery_required"] = "The previous handoff needs a recovery review before reconnecting. Keep EE disconnected until that review is complete.",
        ["handoff_save_failed"] = "Your lighting setup or handoff preference could not be saved. Retry handoff before closing EE.",
        ["instance_running"] = "EE is already running. Use the open EE window or close it before opening another.",
        ["instance_unavailable"] = "EE could not verify whether another session is running. Check the running EE sessions before trying again.",
        ["compatibility_verification_required"] = "This switch model or firmware needs compatibility verification before lighting control. Use a verified EE switch and firmware profile to continue.",
        ["saved_login_failed"] = "The login could not be saved securely. Turn off Save login and try again.",
        ["saved_login_unavailable"] = "The saved login could not be opened. Enter your SSH credentials to connect.",
        ["no_saved_login"] = "Enter your SSH credentials to connect. Choose Save login to use them next time.",
        ["saved_login_forget_failed"] = "The saved login could not be removed. Try again.",
        ["busy"] = "Another connection is in progress. Wait for it to finish.",
        ["connection_in_progress"] = "Another connection is in progress. Wait for it to finish.",
        ["timeout"] = "The connection timed out. Check that the device is available and try again.",
        ["unavailable"] = "The local connection service is unavailable. Reopen this preview and try again.",
        ["controller_auth_failed"] = "UniFi Network rejected the login. Use a local controller account and check its password.",
        ["controller_unreachable"] = "UniFi Network could not be reached. Check its address and port.",
        ["controller_invalid_url"] = "Enter the controller’s HTTPS address, including its port if required.",
        ["controller_invalid_response"] = "The controller response could not be read. Check the UniFi Network address.",
        ["site_required"] = "Choose the UniFi site that contains your switch.",
        ["switch_not_found"] = "The connected switch was not found in this site. Check the selected site.",
    };

    public static string ConnectionError(Problem error, string fallback = "The connection could not be confirmed. Check the details and try again.") =>
        error.Code is { } code && Messages.TryGetValue(code, out var message) ? message : fallback;

    static bool HandoffNeedsReview(JsonNode? session) => session.Str("error.code") == "handoff_recovery_required";

    public static bool HandoffComplete(JsonNode? session) =>
        session.Bool("handed_off") == true && session.Str("handoff_state") == "complete" && session.Bool("logged_in") == false &&
        session.Bool("connected") == false && session.Bool("connecting") != true && session.Bool("automatic_connect_paused") == true && !HandoffNeedsReview(session);

    /// <summary>Port of requestHandoff. Null when the page went away meanwhile.</summary>
    public static async Task<(JsonObject? Session, string? Error)?> RequestHandoff(Send request, Func<bool> isActive)
    {
        Problem problem;
        try
        {
            var session = await request("/api/session/handoff", "POST");
            if (!isActive()) return null;
            if (HandoffComplete(session)) return ((JsonObject?)session, null);
            problem = new Problem(null, []);
        }
        catch (Exception exception)
        {
            problem = Problem.From(exception);
        }
        if (!isActive()) return null;
        JsonObject? current = null;
        try { current = await request("/api/session") as JsonObject; }
        catch (Exception) { }
        if (!isActive()) return null;
        return (current, HandoffComplete(current) ? null : HandoffNeedsReview(current) ? Messages["handoff_recovery_required"]
            : ConnectionError(problem, "Handoff could not be confirmed. Refresh connection status before reconnecting."));
    }

    public static readonly Dictionary<string, string> ControllerMessages = new(StringComparer.Ordinal)
    {
        ["invalid_url"] = "Enter the UniFi Network HTTPS address, including its port if required.",
        ["invalid_controller_kind"] = "Choose the type of UniFi controller you use.",
        ["invalid_auth_method"] = "Choose a sign-in method supported by this controller type.",
        ["api_key_required"] = "Enter your UniFi API key.",
        ["invalid_api_key"] = "Enter the API key exactly as it was created in UniFi, without spaces.",
        ["invalid_console_id"] = "Choose the console you want to connect.",
        ["invalid_cloud_origin"] = "Use the UniFi cloud connector address.",
        ["switch_identity_required"] = "Reconnect your switch using its IP address, then connect this controller.",
        ["api_response_incomplete"] = "The controller returned incomplete details. Check its Network version and API access, then try again.",
        ["integration_unavailable"] = "The Network API is unavailable at this address. Check the controller type, Network version, and API access.",
        ["no_consoles"] = "No consoles are available to this API key. Check its access in UniFi Site Manager.",
        ["invalid_site"] = "Choose a valid UniFi site.",
        ["invalid_certificate"] = "Verify the controller certificate fingerprint and try connecting again.",
        ["invalid_request"] = "Check the controller connection details and try again.",
        ["credentials_required"] = "Enter your UniFi Network username and password.",
        ["authentication_failed"] = "UniFi Network rejected the credentials. Check the selected sign-in method and credentials.",
        ["unreachable"] = "UniFi Network could not be reached. Check its address and port.",
        ["unsupported_controller"] = "Check the controller type and its direct HTTPS address, including the port.",
        ["controller_local_account_required"] = "Use a local UniFi account with Network access.",
        ["invalid_response"] = "The controller response could not be read. Check the UniFi Network address.",
        ["rate_limited"] = "UniFi Network asked EE to wait. Try connecting again in a moment.",
        ["controller_error"] = "UniFi Network could not complete the request. Try again.",
        ["no_sites"] = "This account has no available UniFi sites. Check its site access.",
        ["site_required"] = "Choose the UniFi site that contains your switch.",
        ["site_not_found"] = "The selected UniFi site is no longer available. Choose a site again.",
        ["switch_not_found"] = "The connected switch was not found in this UniFi site. Check the selected site.",
        ["switch_ambiguous"] = "More than one controller device matched this switch. Check the switch identity in UniFi Network.",
        ["switch_required"] = "Connect your switch before linking its UniFi Network details.",
        ["redirect_blocked"] = "The controller redirected this request. Enter the final UniFi Network HTTPS address.",
        ["response_too_large"] = "The controller returned more data than expected. Check the UniFi Network address.",
        ["two_factor_required"] = "Enter the verification code for your UniFi account.",
        ["verification_failed"] = "The verification code was not accepted. Enter a new code and try again.",
        ["controller_login_required"] = "Connect your controller before saving its login.",
        ["controller_login_save_failed"] = "The controller login could not be saved securely. Try Save reconnect preference again.",
        ["controller_login_remove_failed"] = "The saved controller login could not be removed. Try Save reconnect preference again.",
        ["saved_controller_login_unavailable"] = "The saved controller login could not be opened. Connect your controller again.",
    };

    public static string ControllerError(Problem error, string fallback = "The controller connection could not be confirmed. Check the address and login, then try again.")
    {
        if (error.Code == "controller_error")
        {
            var detail = error.Details;
            var steps = new Dictionary<string, string> { ["login"] = "sign-in", ["sites"] = "site discovery", ["devices"] = "switch discovery", ["clients"] = "client discovery" };
            if (detail.Str("operation") is { } operation && steps.TryGetValue(operation, out var step))
            {
                var status = detail.Int("upstream_status") is { } code && code >= 400 && code <= 599 ? $" (HTTP {code})" : "";
                return $"UniFi Network could not complete {step}{status}. Try connecting again.";
            }
        }
        if (error.Code is { } key && ControllerMessages.TryGetValue(key, out var message)) return message;
        return error.Code == "timeout" ? "UniFi Network did not respond in time. Check the controller and try again." : fallback;
    }

    public readonly record struct ReconnectControls(bool Supported, bool Checked, bool Changed, bool CanApply);

    public static ReconnectControls ControllerReconnectControls(JsonNode? current, bool? draft)
    {
        var supported = current.Bool("preferences_supported") == true;
        var saved = current.Bool("auto_reconnect") == true;
        var chosen = draft ?? saved;
        return new ReconnectControls(supported, chosen, chosen != saved,
            supported && (chosen ? current.Bool("can_save_login") == true && (!saved || current.Bool("saved_login_matches_connection") == false) : saved));
    }

    public static bool ControllerReconnectConfirmed(JsonNode? current, bool enabled) =>
        current.Bool("preferences_supported") == true && !current.Truthy("saved_login_error") &&
        current.Bool("auto_reconnect") == enabled && current.Bool("saved_login_available") == enabled &&
        (!enabled || current.Bool("saved_login_matches_connection") == true);

    /// <summary>The https origin of a controller address with no path, query, fragment or account; otherwise null.</summary>
    public static string? ControllerOrigin(string? value)
    {
        if (value is null || Regex.IsMatch(value, @"\s")) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url)) return null;
        if (url.Scheme != "https" || url.Host.Length == 0 || url.UserInfo.Length > 0 || url.AbsolutePath is not ("" or "/") || url.Query.Length > 0 || url.Fragment.Length > 0) return null;
        // The URL parser keeps a bare query or fragment mark out of Query and Fragment; the browser treats them as empty too.
        return url.IsDefaultPort ? $"https://{url.IdnHost}" : $"https://{url.IdnHost}:{url.Port}";
    }

    public static Dictionary<string, List<string>> ControllerSupportedMethods(JsonNode? current)
    {
        var allowed = new Dictionary<string, string[]> { ["self_hosted"] = ["password"], ["unifi_os"] = ["password", "api_key"], ["cloud_connector"] = ["api_key"] };
        if (current is not JsonObject value || !value.ContainsKey("supported_auth_methods"))
            return new() { ["self_hosted"] = ["password"], ["unifi_os"] = ["password"], ["cloud_connector"] = [] };
        var advertised = value["supported_auth_methods"];
        return allowed.ToDictionary(entry => entry.Key, entry => entry.Value
            .Where(method => advertised.At(entry.Key) is JsonArray list && list.Any(item => item.Str() == method)).ToList());
    }

    public sealed record Target(string? Url, string ControllerKind, string AuthMethod, string? ConsoleId);

    static bool SameControllerTarget(JsonNode? target, Target values) =>
        target.Truthy("url") && ControllerOrigin(values.Url?.Trim()) == ControllerOrigin(target.Str("url"))
        && (target.Str("controller_kind") is { Length: > 0 } kind ? kind : "self_hosted") == values.ControllerKind
        && (target.Str("auth_method") is { Length: > 0 } method ? method : "password") == (values.AuthMethod is { Length: > 0 } auth ? auth : "password")
        && (target.Str("console_id") is { Length: > 0 } console ? console : null) == (values.ConsoleId is { Length: > 0 } id ? id : null);

    public static bool SameTarget(JsonNode? target, Target values) => SameControllerTarget(target, values);

    public readonly record struct FieldProblem(string Field, string Message);

    public static FieldProblem? ControllerConnectionProblem(Target values, string username, string password, string apiKey, JsonNode? current)
    {
        var methods = ControllerSupportedMethods(current);
        var method = values.AuthMethod is { Length: > 0 } chosen ? chosen : "password";
        if (values.ControllerKind is not ("self_hosted" or "unifi_os" or "cloud_connector") || methods[values.ControllerKind].Count == 0)
            return new("controller_kind", ControllerMessages["invalid_controller_kind"]);
        if (!methods[values.ControllerKind].Contains(method)) return new("auth_method", ControllerMessages["invalid_auth_method"]);
        var origin = ControllerOrigin(values.Url?.Trim());
        if (origin is null) return new("url", "Enter the controller’s direct HTTPS address and port, without a page path, query, or account details.");
        if (values.ControllerKind == "cloud_connector")
        {
            if (values.Url?.Trim() != "https://api.ui.com") return new("url", ControllerMessages["invalid_cloud_origin"]);
            if (!Regex.IsMatch(values.ConsoleId ?? "", @"^[A-Za-z0-9_-]{1,128}(?::[0-9]{1,20})?$")) return new("console_id", ControllerMessages["invalid_console_id"]);
        }
        else if (!string.IsNullOrEmpty(values.ConsoleId)) return new("console_id", ControllerMessages["invalid_console_id"]);
        var saved = new List<JsonNode?>();
        if (current.Truthy("configured")) saved.Add(current);
        if (current.Truthy("pending_site_choice")) saved.Add(current.At("pending_site_choice"));
        var reuse = saved.Any(target => SameControllerTarget(target, values));
        if (method == "api_key")
        {
            if (apiKey.Length == 0 && reuse) return null;
            if (apiKey.Length == 0) return new("api_key", ControllerMessages["api_key_required"]);
            if (!ValidApiKey(apiKey)) return new("api_key", ControllerMessages["invalid_api_key"]);
            return null;
        }
        var name = username.Trim();
        if (name.Length == 0 && password.Length == 0 && reuse) return null;
        if (name.Length == 0) return new("username", "Enter your local UniFi username.");
        if (password.Length == 0) return new("password", "Enter your UniFi password.");
        return null;
    }

    public static bool ValidApiKey(string value) => Regex.IsMatch(value, @"^[\x21-\x7e]{1,4096}$");

    static readonly Dictionary<string, string> PrivateAccessMessages = new(StringComparer.Ordinal)
    {
        ["tailscale_not_installed"] = "Install Tailscale on this computer, then refresh status.",
        ["tailscale_unavailable"] = "Open Tailscale on this computer, then refresh status.",
        ["tailscale_signin_required"] = "Sign in to Tailscale on this computer to continue setup.",
        ["tailscale_permission_required"] = "Tailscale needs permission to manage private access. Open EE as an administrator and try again.",
        ["tailscale_approval_required"] = "Complete the approval in Tailscale, then refresh status and apply the setting.",
        ["tailscale_not_running"] = "Connect Tailscale on this computer, then refresh status.",
        ["tailscale_device_identity_required"] = "Use a personal Tailscale account on the computer running EE, then refresh status.",
        ["private_access_port_change_requires_off"] = "Turn private access off and confirm cleanup before changing its port.",
        ["private_access_identity_changed"] = "The approved Tailscale account or computer changed. Turn private access off before setting it up again.",
        ["private_access_port_in_use"] = "Another service uses EE’s private access port. Keep that service unchanged and choose a separate port in Connection options.",
        ["private_access_route_unavailable"] = "Check Tailscale Serve on this PC, then refresh status.",
        ["private_access_funnel_enabled"] = "Turn off Tailscale Funnel for this address, then refresh status.",
        ["private_access_listener_failed"] = "EE could not open private access. Check the host app and try again.",
        ["private_access_save_failed"] = "The private access setting could not be saved. Try Apply setting again.",
        ["private_access_setup_unconfirmed"] = "Private access was not confirmed. Refresh status before trying again.",
        ["private_access_cleanup_unconfirmed"] = "Private access shutdown was not confirmed. Keep EE open and refresh status before trying again.",
        ["private_access_shutdown_unconfirmed"] = "Private access shutdown could not be confirmed. Review the local connection before retrying.",
        ["private_access_invalid_configuration"] = "Check the Tailscale configuration on this PC, then refresh status.",
        ["unavailable"] = "Private access could not be checked. Refresh status to try again.",
        ["forbidden"] = "Change this setting in EE on the host computer.",
    };

    public static string PrivateAccessError(Problem error) =>
        error.Code is { } code && PrivateAccessMessages.TryGetValue(code, out var message) ? message
            : "Private access could not be confirmed. Refresh status before trying again.";

    static readonly Regex Control = new(@"[\u0000- \u007f]");

    /// <summary>The origin of a ready private address: https on a *.*.ts.net name, port 1024–65535, path "/".</summary>
    public static string? PrivateAccessUrl(string? value)
    {
        if (value is null || Control.IsMatch(value)) return null;
        var match = Regex.Match(value, @"^https://([^/?#:@]+):(\d+)(/?)$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var host = match.Groups[1].Value.ToLowerInvariant();
        if (!int.TryParse(match.Groups[2].Value, out var port) || port < 1024 || port > 65535) return null;
        const string label = "[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?";
        if (!Regex.IsMatch(host, $@"^{label}\.{label}\.ts\.net$")) return null;
        return $"https://{host}:{port}";
    }

    /// <summary>A Tailscale install or approval page address that the browser workspace would link to; otherwise null.</summary>
    public static string? PrivateAccessSetupUrl(string? value, string? action)
    {
        if (value is null || Control.IsMatch(value) || !Regex.IsMatch(value, @"^https://(?:login\.tailscale\.com|tailscale\.com)/[^#]*$")) return null;
        var path = Regex.Replace(value.Split('?')[0], "^https://[^/]+", "");
        if (path.Contains('%') || path.Contains('\\') || path.Split('/').Any(part => part is "." or "..")) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.UserInfo.Length > 0 || !url.IsDefaultPort) return null;
        var origin = $"{url.Scheme}://{url.Host}";
        var search = url.Query;
        if (action == "install" && origin == "https://tailscale.com" && url.AbsolutePath == "/download" && search.Length == 0) return url.AbsoluteUri;
        if (action == "approve" && origin == "https://login.tailscale.com" &&
            (Regex.IsMatch(url.AbsolutePath, "^/a/[A-Za-z0-9_-]+$") && search.Length == 0 || url.AbsolutePath == "/f/serve" || url.AbsolutePath == "/admin/machines" && search.Length == 0))
            return url.AbsoluteUri;
        return null;
    }
}

/// <summary>Port of lightingPorts (port-topology.mjs): the Ethernet and SFP port numbers of a state.</summary>
public static class PortTopology
{
    static bool Valid(JsonNode? value) => value.Int() is > 0 and <= 256;

    static List<int> Ids(IEnumerable<JsonNode?> values) => values.Where(Valid).Select(value => value.Int()!.Value).Distinct().Order().ToList();

    public static (List<int> Copper, List<int> Sfp) LightingPorts(JsonNode? state)
    {
        var count = state.At("num_ports") ?? state.At("switch.num_ports");
        List<int> copper;
        if (state.At("ports") is JsonArray ports) copper = Ids(ports.Select(port => port.At("port")));
        else if (count.Int() is { } total && total >= 0 && total <= 256) copper = Enumerable.Range(1, total).ToList();
        else copper = [];
        List<int> sfp;
        if (state.At("sfp") is JsonArray cages) sfp = Ids(cages.Select(port => port.At("port")));
        else sfp = Ids((state.At("sfp_ports") ?? state.At("switch.sfp_ports")) as JsonArray ?? []);
        return (copper, sfp.Where(id => !copper.Contains(id)).ToList());
    }
}
