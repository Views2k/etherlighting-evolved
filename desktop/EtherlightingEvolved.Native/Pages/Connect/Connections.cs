using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EtherlightingEvolved.Native.Core;

namespace EtherlightingEvolved.Native.Pages.Connect;

/// <summary>An error with only a code, for the places where the browser workspace throws <c>{code}</c>.</summary>
public sealed class CodedException(string code, string? message = null) : Exception(message ?? code)
{
    public string Code { get; } = code;
}

/// <summary>A handoff or connection message with its notice tone ("online", "waiting" or none).</summary>
public sealed record ConnectionNotice(string Message, bool Error, string Tone);

/// <summary>The result of a handoff request: the latest session, and the error to show when it was not confirmed.</summary>
public sealed record HandoffResult(JsonObject? Session, string? Error);

/// <summary>The controller target a form or saved connection describes.</summary>
public sealed record ControllerTarget(string? Url, string? ControllerKind, string? AuthMethod, string? ConsoleId)
{
    public static ControllerTarget? From(JsonNode? node) => node is JsonObject
        ? new ControllerTarget(node.Str("url"), node.Str("controller_kind"), node.Str("auth_method"), node.Str("console_id"))
        : null;
}

/// <summary>The controller form values checked before connecting.</summary>
public sealed record ControllerValues(string? Url, string? ControllerKind, string? AuthMethod, string? ConsoleId,
    string? Username = null, string? Password = null, string? ApiKey = null);

/// <summary>The reconnect preference controls (controllerReconnectControls).</summary>
public readonly record struct ReconnectControls(bool Supported, bool Checked, bool Changed, bool CanApply);

/// <summary>
/// Messages, checks and requests of the browser workspace's connection views (preview/connections.mjs): switch login
/// and handoff messages, the UniFi controller messages and checks, and the private access messages and addresses.
/// </summary>
public static class Connections
{
    public const string DefaultError = "The connection could not be confirmed. Check the details and try again.";
    public const string DefaultControllerError = "The controller connection could not be confirmed. Check the address and login, then try again.";

    public static readonly IReadOnlyDictionary<string, string> Messages = new Dictionary<string, string>(StringComparer.Ordinal)
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

    static string? CodeOf(Exception? error) => error switch
    {
        ApiException api => api.Code,
        CodedException coded => coded.Code,
        _ => null,
    };

    /// <summary>The message for a failed connection (connectionError).</summary>
    public static string ConnectionError(string? code, bool timedOut = false, string fallback = DefaultError) =>
        code is not null && Messages.TryGetValue(code, out var message) ? message : timedOut ? Messages["timeout"] : fallback;

    public static string ConnectionError(Exception? error, string fallback = DefaultError) =>
        ConnectionError(CodeOf(error), error is TimeoutException, fallback);

    public static string ConnectionError(JsonNode? error, string fallback = DefaultError) => ConnectionError(error.Str("code"), false, fallback);

    // ---- Handoff to UniFi

    static bool HandoffNeedsReview(JsonNode? session) => session.Str("error.code") == "handoff_recovery_required";

    public static bool HandoffBlocked(JsonNode? session) => session.Str("handoff_state") is "pending" or "unconfirmed" || HandoffNeedsReview(session);

    public static bool HandoffRetryable(JsonNode? session) => session.Str("handoff_state") == "unconfirmed" && !HandoffNeedsReview(session);

    public static bool HandoffComplete(JsonNode? session) =>
        session.Bool("handed_off") == true && session.Str("handoff_state") == "complete" && session.Bool("logged_in") == false &&
        session.Bool("connected") == false && session.Bool("connecting") != true && session.Bool("automatic_connect_paused") == true &&
        !HandoffNeedsReview(session);

    /// <summary>The handoff message for a session, or null when there is none (handoffStatus).</summary>
    public static ConnectionNotice? HandoffStatus(JsonNode? session)
    {
        if (HandoffNeedsReview(session)) return new(Messages["handoff_recovery_required"], true, "");
        if (session.Str("handoff_state") == "pending") return new("Handing lighting back to UniFi…", false, "waiting");
        if (session.Str("handoff_state") == "unconfirmed") return new(Messages["handoff_unconfirmed"], true, "");
        if (HandoffComplete(session))
            return new("Lighting returned to UniFi. Your saved logins and profiles are kept. Connect when you want EE to control lighting again.", false, "online");
        if (session.Truthy("automatic_connect_paused") && !session.Truthy("logged_in"))
            return new("Automatic switch connection is paused. Connect when you want EE to control lighting again.", false, "");
        return null;
    }

    /// <summary>
    /// Asks EE to stop its lighting and return control to UniFi (requestHandoff). Returns null when the caller is
    /// no longer active.
    /// </summary>
    public static async Task<HandoffResult?> RequestHandoff(Func<string, string, JsonNode?, Task<JsonNode?>> request, Func<bool>? isActive = null)
    {
        isActive ??= () => true;
        try
        {
            var session = await request("/api/session/handoff", "POST", null);
            if (!isActive()) return null;
            if (HandoffComplete(session)) return new HandoffResult(session as JsonObject, null);
            throw new InvalidOperationException("Handoff could not be confirmed.");
        }
        catch (Exception error)
        {
            if (!isActive()) return null;
            JsonNode? session = null;
            try { session = await request("/api/session", "GET", null); }
            catch (Exception) { }
            if (!isActive()) return null;
            return new HandoffResult(session as JsonObject, HandoffComplete(session) ? null
                : HandoffNeedsReview(session) ? Messages["handoff_recovery_required"]
                : ConnectionError(error, "Handoff could not be confirmed. Refresh connection status before reconnecting."));
        }
    }

    // ---- UniFi Network controller

    public static readonly IReadOnlyDictionary<string, string> ControllerMessages = new Dictionary<string, string>(StringComparer.Ordinal)
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

    /// <summary>
    /// The message for a controller error (controllerError). <paramref name="detail"/> holds the operation and
    /// upstream status of a "controller_error".
    /// </summary>
    public static string ControllerErrorFor(string? code, JsonNode? detail, bool timedOut = false, string fallback = DefaultControllerError)
    {
        if (code == "controller_error")
        {
            var steps = new Dictionary<string, string> { ["login"] = "sign-in", ["sites"] = "site discovery", ["devices"] = "switch discovery", ["clients"] = "client discovery" };
            if (detail.Str("operation") is { } operation && steps.TryGetValue(operation, out var step))
            {
                var upstream = detail.Int("upstream_status");
                var status = upstream is >= 400 and <= 599 ? $" (HTTP {upstream})" : "";
                return $"UniFi Network could not complete {step}{status}. Try connecting again.";
            }
        }
        if (code is not null && ControllerMessages.TryGetValue(code, out var message)) return message;
        return timedOut || code == "timeout" ? "UniFi Network did not respond in time. Check the controller and try again." : fallback;
    }

    public static string ControllerError(Exception? error, string fallback = DefaultControllerError) =>
        ControllerErrorFor(CodeOf(error), (error as ApiException)?.Details, error is TimeoutException, fallback);

    /// <summary>A controller error object from the workspace state: its details are the object itself.</summary>
    public static string ControllerError(JsonNode? error, string fallback = DefaultControllerError) =>
        ControllerErrorFor(error.Str("code"), error.At("details") as JsonObject ?? error.At("detail") as JsonObject ?? error, false, fallback);

    public static ReconnectControls ControllerReconnectControls(JsonNode? current, bool? draft)
    {
        var supported = current.Bool("preferences_supported") == true;
        var saved = current.Bool("auto_reconnect") == true;
        var chosen = draft ?? saved;
        return new ReconnectControls(supported, chosen, chosen != saved,
            supported && (chosen ? current.Bool("can_save_login") == true && (!saved || current.Bool("saved_login_matches_connection") == false) : saved));
    }

    public static bool ControllerReconnectConfirmed(JsonNode? current, bool? enabled) =>
        enabled is { } value && current.Bool("preferences_supported") == true && !current.Truthy("saved_login_error") &&
        current.Bool("auto_reconnect") == value && current.Bool("saved_login_available") == value &&
        (!value || current.Bool("saved_login_matches_connection") == true);

    /// <summary>The HTTPS origin of a controller address with no path, query, fragment or account details.</summary>
    public static string? ControllerOrigin(string? value)
    {
        if (value is null || value.Any(char.IsWhiteSpace)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url)) return null;
        var search = url.Query.Length > 1 ? url.Query : "";
        var hash = url.Fragment.Length > 1 ? url.Fragment : "";
        return url.Scheme == "https" && url.Host.Length > 0 && url.UserInfo.Length == 0 && url.AbsolutePath is "" or "/" && search.Length == 0 && hash.Length == 0
            ? Origin(url) : null;
    }

    static string Origin(Uri url) => url.IsDefaultPort ? $"{url.Scheme}://{url.Host}" : $"{url.Scheme}://{url.Host}:{url.Port.ToString(CultureInfo.InvariantCulture)}";

    static readonly string[] ControllerKinds = ["self_hosted", "unifi_os", "cloud_connector"];

    /// <summary>The sign-in methods each controller type supports here (controllerSupportedMethods).</summary>
    public static Dictionary<string, List<string>> ControllerSupportedMethods(JsonNode? current)
    {
        var allowed = new Dictionary<string, string[]> { ["self_hosted"] = ["password"], ["unifi_os"] = ["password", "api_key"], ["cloud_connector"] = ["api_key"] };
        if (current is not JsonObject obj || !obj.ContainsKey("supported_auth_methods"))
            return new() { ["self_hosted"] = ["password"], ["unifi_os"] = ["password"], ["cloud_connector"] = [] };
        var advertised = obj["supported_auth_methods"];
        return allowed.ToDictionary(entry => entry.Key, entry => entry.Value
            .Where(method => advertised.At(entry.Key) is JsonArray list && list.Any(item => item.Str() == method)).ToList());
    }

    static string? OrNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    public static bool SameControllerTarget(ControllerTarget? target, ControllerValues values) =>
        !string.IsNullOrEmpty(target?.Url) && ControllerOrigin(values.Url?.Trim()) == ControllerOrigin(target.Url) &&
        (OrNull(target.ControllerKind) ?? "self_hosted") == values.ControllerKind &&
        (OrNull(target.AuthMethod) ?? "password") == (OrNull(values.AuthMethod) ?? "password") &&
        OrNull(target.ConsoleId) == OrNull(values.ConsoleId);

    /// <summary>The first problem with the controller form, as the field to focus and its message (controllerConnectionProblem).</summary>
    public static (string Field, string Message)? ControllerConnectionProblem(ControllerValues values, JsonNode? current)
    {
        var methods = ControllerSupportedMethods(current);
        var method = OrNull(values.AuthMethod) ?? "password";
        if (values.ControllerKind is null || !ControllerKinds.Contains(values.ControllerKind) || methods[values.ControllerKind].Count == 0)
            return ("controller_kind", ControllerMessages["invalid_controller_kind"]);
        if (!methods[values.ControllerKind].Contains(method)) return ("auth_method", ControllerMessages["invalid_auth_method"]);
        var origin = ControllerOrigin(values.Url?.Trim());
        if (origin is null) return ("url", "Enter the controller’s direct HTTPS address and port, without a page path, query, or account details.");
        if (values.ControllerKind == "cloud_connector")
        {
            if (values.Url?.Trim() != "https://api.ui.com") return ("url", ControllerMessages["invalid_cloud_origin"]);
            if (!Regex.IsMatch(values.ConsoleId ?? "", "^[A-Za-z0-9_-]{1,128}(?::[0-9]{1,20})?$")) return ("console_id", ControllerMessages["invalid_console_id"]);
        }
        else if (!string.IsNullOrEmpty(values.ConsoleId)) return ("console_id", ControllerMessages["invalid_console_id"]);
        var savedTargets = new List<ControllerTarget?>();
        if (current.Truthy("configured")) savedTargets.Add(ControllerTarget.From(current));
        if (current.Truthy("pending_site_choice")) savedTargets.Add(ControllerTarget.From(current.At("pending_site_choice")));
        var reuse = savedTargets.Any(target => SameControllerTarget(target, values));
        if (method == "api_key")
        {
            if (string.IsNullOrEmpty(values.ApiKey) && reuse) return null;
            if (string.IsNullOrEmpty(values.ApiKey)) return ("api_key", ControllerMessages["api_key_required"]);
            if (!PrintableKey(values.ApiKey)) return ("api_key", ControllerMessages["invalid_api_key"]);
            return null;
        }
        var username = values.Username?.Trim() ?? "";
        var password = values.Password ?? "";
        if (username.Length == 0 && password.Length == 0 && reuse) return null;
        if (username.Length == 0) return ("username", "Enter your local UniFi username.");
        if (password.Length == 0) return ("password", "Enter your UniFi password.");
        return null;
    }

    /// <summary>JavaScript's Number(text): surrounding spaces are ignored, empty text is 0, anything else not a number is NaN.</summary>
    public static double JsNumber(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return 0;
        if (value.Length > 2 && value[0] == '0' && value[1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B')
        {
            var radix = value[1] is 'x' or 'X' ? 16 : value[1] is 'o' or 'O' ? 8 : 2;
            try { return Convert.ToInt64(value[2..], radix); }
            catch (Exception) { return double.NaN; }
        }
        if (value is "Infinity" or "+Infinity") return double.PositiveInfinity;
        if (value == "-Infinity") return double.NegativeInfinity;
        if (!Regex.IsMatch(value, @"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$")) return double.NaN;
        return double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Number.isInteger for a JavaScript number.</summary>
    public static bool IsInteger(double value) => double.IsFinite(value) && Math.Floor(value) == value;

    /// <summary>An API key of 1 to 4096 printable ASCII characters without spaces.</summary>
    public static bool PrintableKey(string? value) => value is { Length: >= 1 and <= 4096 } && value.All(c => c is >= '\x21' and <= '\x7e');

    // ---- Private access through Tailscale

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

    public static string PrivateAccessError(string? code) =>
        code is not null && PrivateAccessMessages.TryGetValue(code, out var message) ? message : "Private access could not be confirmed. Refresh status before trying again.";

    /// <summary>A private access error: a code string or an object with a code.</summary>
    public static string PrivateAccessError(JsonNode? error) => PrivateAccessError(error.Str() ?? error.Str("code"));

    public static string PrivateAccessError(Exception? error) => PrivateAccessError(CodeOf(error));

    static bool HasControl(string value) => value.Any(c => c <= ' ' || c == '\u007f');

    const string Label = "[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?";

    /// <summary>The origin of a private workspace address on a Tailscale ts.net name with an explicit port (privateAccessUrl).</summary>
    public static string? PrivateAccessUrl(string? value)
    {
        if (value is null || HasControl(value) || !Uri.TryCreate(value, UriKind.Absolute, out var url)) return null;
        if (url.Scheme != "https" || url.IsDefaultPort || url.Port is < 1024 or > 65535 || url.UserInfo.Length > 0 || url.Query.Length > 1 ||
            url.Fragment.Length > 1 || url.AbsolutePath != "/" || !Regex.IsMatch(url.Host, $"^{Label}\\.{Label}\\.ts\\.net$"))
            return null;
        return Origin(url);
    }

    /// <summary>A Tailscale download or approval page for the setup step, or null (privateAccessSetupUrl).</summary>
    public static string? PrivateAccessSetupUrl(string? value, string? action)
    {
        if (value is null || HasControl(value) || !Regex.IsMatch(value, @"^https://(?:login\.tailscale\.com|tailscale\.com)/[^#]*$")) return null;
        var path = Regex.Replace(value.Split('?')[0], "^https://[^/]+", "");
        if (path.Contains('%') || path.Contains('\\') || path.Split('/').Any(part => part is "." or "..")) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url)) return null;
        if (url.UserInfo.Length > 0 || !url.IsDefaultPort || url.Fragment.Length > 1) return null;
        var origin = Origin(url);
        var search = url.Query.Length > 1;
        if (action == "install" && origin == "https://tailscale.com" && url.AbsolutePath == "/download" && !search) return value;
        if (action == "approve" && origin == "https://login.tailscale.com" &&
            (Regex.IsMatch(url.AbsolutePath, "^/a/[A-Za-z0-9_-]+$") && !search || url.AbsolutePath == "/f/serve" || url.AbsolutePath == "/admin/machines" && !search))
            return value;
        return null;
    }
}
