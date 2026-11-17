using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Theme;
using EtherlightingEvolved.Native.Shell;
using EtherlightingEvolved.Native.Ui;
using UiKit = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Settings;

/// <summary>
/// Port of renderControllerConnection (preview/connections.mjs): the UniFi Network section of Settings. It connects
/// a self-hosted, UniFi OS or cloud connector controller, asks for a site, a verification code or certificate trust
/// when needed, and saves the automatic reconnect preference.
/// </summary>
public sealed class ControllerConnection : Border
{
    readonly Func<string, string, JsonNode?, Task<JsonNode?>> request;
    readonly Func<JsonObject, Task> onChanged;
    readonly TextBlock introduction = UiKit.HelpText("Connect UniFi Network to show controller details for your switch.");
    readonly Button reload = UiKit.Button("Refresh controller", "quiet");
    readonly StateLine state = new("Checking controller connection…", "is-disconnected");
    readonly DetailsList details = new();
    readonly TextBlock capabilityHint = UiKit.HelpText();
    readonly Notice message = new(connection: true, ownMargin: false);
    readonly Border reconnectSection;
    readonly CheckRow reconnectRow = new("Reconnect controller automatically");
    readonly CheckBox reconnect;
    readonly TextBlock reconnectHelp = UiKit.HelpText("Save this controller login securely on this PC.");
    readonly TextBlock reconnectHint = UiKit.HelpText();
    readonly Button applyPreference = UiKit.Button("Save reconnect preference", "primary");
    readonly Notice preferenceNotice = new(connection: true);
    readonly StackPanel form = new() { Spacing = 18, MaxWidth = 480 };
    readonly Button editConnection = UiKit.Button("Edit connection");
    readonly TextBlock editorHint = UiKit.HelpText("Connection edits are not applied.");
    readonly SelectBox kind = new();
    readonly TextBlock kindHelp = UiKit.Small();
    readonly SelectBox auth = new();
    readonly TextBlock authHelp = UiKit.Small();
    readonly StackPanel authField;
    readonly TextBox address = UiKit.Input(placeholder: "https://controller:8443", maxLength: 2048);
    readonly TextBox username = UiKit.Input(maxLength: 128);
    readonly TextBox password = UiKit.Input();
    readonly StackPanel usernameField;
    readonly SecretField passwordRow;
    readonly TextBox apiKey = UiKit.Input(maxLength: 4096);
    readonly SecretField apiKeyField;
    readonly TextBlock keyHelp = UiKit.Small();
    readonly SelectBox consoleId = new();
    readonly StackPanel consoleField = new() { Spacing = 7 };
    readonly Button findConsoles = UiKit.Button("Find consoles", "quiet");
    readonly TextBox verification = UiKit.Input(maxLength: 8);
    readonly Disclosure twoFactor;
    readonly SelectBox site = new();
    readonly StackPanel siteField;
    readonly Button submit = UiKit.Button("Connect controller", "primary");
    readonly Button disconnect = UiKit.Button("Disconnect controller", "quiet");
    readonly Button cancelChange = UiKit.Button("Cancel connection change", "quiet");
    readonly FlowRow connectionActions;
    readonly Border trustPanel;
    readonly TextBlock fingerprint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New, monospace") };
    readonly Button trust = UiKit.Button("Trust this certificate and connect");
    readonly TextBlock addressHelp = UiKit.HelpText();
    readonly TextBlock credentialHelp = UiKit.HelpText();
    readonly Dictionary<string, string> methodDrafts = new(StringComparer.Ordinal);
    List<(string Id, string Name, bool Blocked)> consoles = [];
    JsonObject current = [];
    JsonObject? challenge;
    string? certificate, liveStatusKey;
    Task? pending;
    bool active = true, dirty, editingConnection, savingPreference, discovering, programmatic, siteRequired, verificationRequired;
    bool? reconnectDraft;
    string siteContextKey = "", localAddress = "", lastKind = "self_hosted", cleanForm;

    public ControllerConnection(Func<string, string, JsonNode?, Task<JsonNode?>> request, Func<JsonObject, Task> onChanged)
    {
        this.request = request;
        this.onChanged = onChanged;
        reconnect = reconnectRow.Box;
        // section.controller-connection.connection-card.panel
        BorderThickness = new Thickness(0, 1, 0, 0);
        Padding = new Thickness(0, 20);
        Margin = new Thickness(0, 20, 0, 0);
        this.Bind(BorderBrushProperty, "line");

        var title = new StackPanel { Children = { SettingsKit.H2("UniFi Network", "accent"), introduction } };
        var header = new DockPanel();
        reload.VerticalAlignment = VerticalAlignment.Top;
        reload.Margin = new Thickness(18, 0, 0, 0);
        DockPanel.SetDock(reload, Dock.Right);
        header.Children.Add(reload);
        header.Children.Add(title);
        capabilityHint.IsVisible = false;

        var (reconnectFrame, reconnectBody) = SettingsKit.AutomationSection();
        reconnectSection = reconnectFrame;
        reconnectSection.IsVisible = false;
        foreach (var item in new Control[] { reconnectRow, reconnectHelp, reconnectHint, new FlowRow(10, applyPreference), preferenceNotice }) reconnectBody.Children.Add(item);

        editConnection.IsVisible = false;
        editorHint.IsVisible = false;
        var kindField = UiKit.Field("Controller type", kind);
        kindField.Children.Add(kindHelp);
        authField = UiKit.Field("Sign-in method", auth);
        authField.Children.Add(authHelp);
        var addressField = UiKit.Field("Controller address", address);
        usernameField = UiKit.Field("Username", username);
        passwordRow = new SecretField("Controller password", password);
        apiKeyField = new SecretField("API key", apiKey);
        apiKeyField.Children.Add(keyHelp);
        consoleField.Children.Add(new TextBlock { Text = "Console", FontSize = 13, LineHeight = 18 }.Bind(TextBlock.ForegroundProperty, "muted"));
        consoleField.Children.Add(consoleId);
        consoleField.Children.Add(findConsoles);
        consoleField.Children.Add(UiKit.Small("Enter an API key, then find and choose your console."));
        var verificationField = UiKit.Field("Verification code", verification, "Enter your account’s current verification code.");
        verificationField.Margin = new Thickness(0, 14, 0, 0);
        verificationField.MaxWidth = 150;
        verificationField.HorizontalAlignment = HorizontalAlignment.Left;
        twoFactor = new Disclosure("Two-factor authentication", verificationField);
        site.SetOptions([("", "Automatic", false)], "");
        siteField = UiKit.Field("UniFi site", site);
        siteField.IsVisible = false;
        cancelChange.IsVisible = false;
        disconnect.IsVisible = false;
        var formActions = new FlowRow(10, submit, cancelChange);
        connectionActions = new FlowRow(10, editConnection, disconnect);
        var trustBody = new StackPanel { Spacing = 12 };
        trustBody.Children.Add(SettingsKit.P("Verify this SHA-256 fingerprint against your controller, then trust it to continue.", "muted"));
        fingerprint.Bind(TextBlock.ForegroundProperty, "muted");
        trustBody.Children.Add(fingerprint);
        trust.HorizontalAlignment = HorizontalAlignment.Stretch;
        trustBody.Children.Add(trust);
        trustPanel = new Border { Padding = new Thickness(14, 12), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Child = trustBody, IsVisible = false };
        trustPanel.Bind(BackgroundProperty, "notice-surface").Bind(BorderBrushProperty, "line");
        var setupHelp = new Disclosure("Where do I find these details?", addressHelp, credentialHelp);
        foreach (var item in new Control[] { kindField, authField, addressField, usernameField, passwordRow, apiKeyField, consoleField, setupHelp, twoFactor, siteField, trustPanel, formActions })
            form.Children.Add(item);
        form.HorizontalAlignment = HorizontalAlignment.Stretch;

        var body = new BlockStack();
        body.Children.Add(header);
        body.Children.Add(BlockStack.Margins(state, 16));
        body.Children.Add(BlockStack.Margins(details, 16, 16));
        body.Children.Add(BlockStack.Margins(capabilityHint, 5));
        body.Children.Add(BlockStack.Margins(message, 16));
        body.Children.Add(BlockStack.Margins(reconnectSection, 20));
        body.Children.Add(connectionActions);
        body.Children.Add(BlockStack.Margins(editorHint, 5));
        body.Children.Add(BlockStack.Margins(form, 22));
        Child = body;

        RenderChoices("self_hosted", "password");
        ShowConsoles();
        ShowKind();
        cleanForm = FormValues();

        // Edits in the form (the form's input event)
        foreach (var box in new[] { address, username, password, apiKey, verification })
            box.TextChanged += (_, _) =>
            {
                if (!programmatic) dirty = true;
            };
        site.Changed += () => dirty = true;
        address.TextChanged += (_, _) =>
        {
            if (programmatic) return;
            ResetControllerChallenge();
            ShowSites();
            SetBusy();
        };
        kind.Changed += () =>
        {
            var chosen = kind.Value;
            methodDrafts[lastKind] = auth.Value;
            if (chosen == "cloud_connector" && lastKind != chosen)
            {
                localAddress = address.Text ?? "";
                SetText(address, "https://api.ui.com");
            }
            else if (lastKind == "cloud_connector" && chosen != lastKind) SetText(address, localAddress);
            dirty = true;
            var methods = Connections.ControllerSupportedMethods(current).GetValueOrDefault(chosen) ?? [];
            RenderMethods(methods, methodDrafts.TryGetValue(chosen, out var method) && method.Length > 0 ? method : "password", useDefault: true);
            lastKind = chosen;
            ResetControllerChallenge();
            ShowKind();
            ShowSites();
            SetBusy();
        };
        auth.Changed += () =>
        {
            dirty = true;
            methodDrafts[kind.Value] = auth.Value;
            ResetControllerChallenge();
            ShowKind();
            ShowSites();
            SetBusy();
        };
        consoleId.Changed += () =>
        {
            dirty = true;
            ResetControllerChallenge();
            ShowSites();
            SetBusy();
        };
        apiKey.TextChanged += (_, _) =>
        {
            if (programmatic) return;
            consoles = [];
            ShowConsoles("");
            ResetControllerChallenge();
            ShowSites();
            SetBusy();
        };
        findConsoles.Click += (_, _) => FindConsoles();
        username.TextChanged += (_, _) =>
        {
            if (programmatic) return;
            SetText(verification, "");
            verificationRequired = false;
        };
        submit.Click += (_, _) => Connect();
        foreach (var box in new[] { address, username, password, apiKey, verification })
            box.KeyDown += (_, e) =>
            {
                if (e.Key != Avalonia.Input.Key.Enter) return;
                e.Handled = true;
                Connect();
            };
        trust.Click += (_, _) =>
        {
            if (challenge is null || kind.Value == "cloud_connector" || !Connections.SameTarget(challenge, FormTarget())) return;
            certificate = challenge.Str("fingerprint");
            challenge = null;
            trustPanel.IsVisible = false;
            Connect();
        };
        reload.Click += (_, _) => Perform(() => request("/api/controller", "GET", null), "Checking controller status…");
        editConnection.Click += (_, _) =>
        {
            if (!active || pending is not null) return;
            editingConnection = !editingConnection;
            SetBusy();
            if (editingConnection) kind.Focus();
            else editConnection.Focus();
        };
        cancelChange.Click += (_, _) =>
        {
            if (current.Truthy("pending_site_choice")) Perform(() => request("/api/controller/cancel", "POST", null),"Canceling connection change…", "cancel");
        };
        disconnect.Click += (_, _) => Perform(() => request("/api/controller/disconnect", "POST", null),"Disconnecting controller…", "disconnect");
        reconnect.IsCheckedChanged += (_, _) =>
        {
            if (programmatic) return;
            reconnectDraft = reconnect.IsChecked == true;
            preferenceNotice.Set();
            SetBusy();
        };
        applyPreference.Click += (_, _) => ApplyPreference();
        Perform(() => request("/api/controller", "GET", null), "Checking controller status…");
    }

    void SetText(TextBox box, string text)
    {
        if (box.Text == text) return;
        programmatic = true;
        try { box.Text = text; }
        finally { programmatic = false; }
    }

    void Status(string text = "", bool error = false, string tone = "") => message.Set(text, error, tone);

    void RenderChoices(string selectedKind, string selectedMethod)
    {
        var supported = Connections.ControllerSupportedMethods(current);
        var kinds = new[] { ("self_hosted", "Self-hosted Network"), ("unifi_os", "UniFi Console or OS Server"), ("cloud_connector", "UniFi cloud connector") }
            .Where(item => supported[item.Item1].Count > 0).Select(item => (item.Item1, item.Item2, false)).ToList();
        kind.SetOptions(kinds, supported.TryGetValue(selectedKind, out var list) && list.Count > 0 ? selectedKind : "");
        RenderMethods(supported.GetValueOrDefault(kind.Value) ?? [], selectedMethod);
        authField.IsVisible = supported.Values.Any(item => item.Contains("api_key"));
    }

    void RenderMethods(List<string> methods, string selectedMethod, bool useDefault = false)
    {
        auth.SetOptions(methods.Select(value => (value, value == "api_key" ? "API key" : "Controller account", false)),
            methods.Contains(selectedMethod) ? selectedMethod : useDefault ? methods.FirstOrDefault() ?? "" : "");
    }

    bool SupportsVerification() => kind.Value == "self_hosted" && auth.Value == "password";

    void ShowKind()
    {
        var selfHosted = kind.Value == "self_hosted";
        var cloud = kind.Value == "cloud_connector";
        var key = auth.Value == "api_key";
        kindHelp.Text = cloud ? "Find a console through UniFi Site Manager."
            : selfHosted ? "Standalone UniFi Network installed on a PC or server. HTTPS usually uses port 8443."
            : "A Cloud Gateway, Cloud Key running UniFi OS, or UniFi OS Server. HTTPS usually uses port 443.";
        address.Watermark = selfHosted ? "https://controller:8443" : "https://console";
        address.IsReadOnly = cloud;
        usernameField.IsVisible = !key;
        passwordRow.IsVisible = !key;
        apiKeyField.IsVisible = key;
        twoFactor.IsVisible = SupportsVerification();
        consoleField.IsVisible = cloud;
        if (!SupportsVerification())
        {
            twoFactor.Open = false;
            verificationRequired = false;
        }
        authHelp.Text = key ? "Read switch and port details with an API key." : "Show device names and port details with a local UniFi account.";
        keyHelp.Text = cloud ? "Create a key in Site Manager → Settings → API Keys." : "Create a key in UniFi Network → Control Plane → Integrations.";
        addressHelp.Text = cloud ? "The cloud connector uses UniFi’s fixed HTTPS address."
            : "Use the direct HTTPS address you open for UniFi on your network. Enter only the address and port, without a page path.";
        credentialHelp.Text = key ? keyHelp.Text
            : "Use a local UniFi account with Network access to the site that contains your switch. You can create a separate local account in UniFi’s administrator settings.";
    }

    void ShowConsoles(string? selected = null)
    {
        selected ??= consoleId.Value;
        var options = new List<(string, string, bool)> { ("", "Choose a console", false) };
        if (selected.Length > 0 && consoles.All(item => item.Id != selected)) options.Add((selected, "Selected console", false));
        options.AddRange(consoles.Select(item => (item.Id, item.Name, item.Blocked)));
        consoleId.SetOptions(options, consoles.FirstOrDefault(item => item.Id == selected) is { Id: not null } found && found.Blocked ? "" : selected);
    }

    string FormValues() => new JsonArray(kind.Value, auth.Value, address.Text ?? "", username.Text ?? "", password.Text ?? "", apiKey.Text ?? "", consoleId.Value,
        verification.Text ?? "", site.Value).ToJsonString();

    bool FormChanged() => FormValues() != cleanForm;

    Connections.Target FormTarget() =>
        new(address.Text ?? "", kind.Value, auth.Value, kind.Value == "cloud_connector" ? consoleId.Value.Trim() : null);

    bool MatchesForm(JsonNode? target) => Connections.SameTarget(target, FormTarget());

    void SyncTarget()
    {
        if (dirty && FormChanged()) return;
        dirty = false;
        var target = current["pending_site_choice"] as JsonObject
            ?? (current.Truthy("url") ? current : current["saved_target"] as JsonObject ?? current);
        SetText(address, target.Str("url") ?? "");
        RenderChoices(target.Str("controller_kind") is { Length: > 0 } chosenKind ? chosenKind : "self_hosted", target.Str("auth_method") is { Length: > 0 } method ? method : "password");
        ShowConsoles(target.Str("console_id") is { Length: > 0 } console ? console : "");
        lastKind = kind.Value;
        if (kind.Value != "cloud_connector") localAddress = address.Text ?? "";
        ShowKind();
    }

    void ShowSites()
    {
        var candidate = current.Truthy("pending_site_choice") ? current["pending_site_choice"] as JsonObject : null;
        JsonObject? target = candidate is not null && MatchesForm(candidate) ? candidate : MatchesForm(current) ? current : null;
        var isCandidate = target is not null && ReferenceEquals(target, candidate);
        var sites = target.Objects("sites").ToList();
        var key = target is not null
            ? $"{(isCandidate ? "pending" : "active")}|{Connections.ControllerOrigin(target.Str("url")) ?? "null"}|{Or(target.Str("controller_kind"), "self_hosted")}|{Or(target.Str("auth_method"), "password")}|{Or(target.Str("console_id"), "")}"
            : "";
        var selected = key == siteContextKey ? site.Value : isCandidate ? "" : target.Str("site") ?? "";
        siteContextKey = key;
        var options = new List<(string, string, bool)> { ("", isCandidate || sites.Count > 1 ? "Choose a site" : "Automatic", false) };
        options.AddRange(sites.Select(item => (item.Str("id") ?? "", Or(item.Str("name"), item.Str("id") ?? ""), false)));
        site.SetOptions(options, sites.Any(item => item.Str("id") == selected) ? selected : "");
        siteRequired = isCandidate || target is not null && current.Str("error.code") == "site_required";
        siteField.IsVisible = sites.Count > 0;
        cancelChange.IsVisible = candidate is not null;
    }

    static string Or(string? value, string fallback) => value is { Length: > 0 } ? value : fallback;

    void PendingSiteMessage() => Status("Choose the site that contains your switch to finish this connection change.");

    bool RememberOnConnect() => current.Bool("remember_on_connect_supported") == true
        && Connections.ControllerReconnectControls(current, reconnectDraft) is { Supported: true, Checked: true };

    void SetBusy()
    {
        if (!active) return;
        var busy = pending is not null;
        foreach (var control in FormControls()) control.IsEnabled = !busy;
        auth.IsEnabled = !busy && (Connections.ControllerSupportedMethods(current).TryGetValue(kind.Value, out var methods) ? methods.Count : 0) >= 2;
        username.IsEnabled = password.IsEnabled = !busy && auth.Value != "api_key";
        apiKey.IsEnabled = !busy && auth.Value == "api_key";
        verification.IsEnabled = !busy && SupportsVerification();
        consoleId.IsEnabled = !busy && kind.Value == "cloud_connector";
        findConsoles.IsVisible = current.Bool("console_discovery_supported") == true;
        findConsoles.IsEnabled = !(busy || kind.Value != "cloud_connector" || auth.Value != "api_key" || current.Bool("console_discovery_supported") != true ||
                                   !Connections.ValidApiKey(apiKey.Text ?? ""));
        findConsoles.SetLabel(discovering ? "Finding consoles…" : consoles.Count > 0 ? "Refresh consoles" : "Find consoles");
        reload.IsEnabled = !busy;
        disconnect.IsEnabled = !busy;
        var editorRequired = current.Bool("connected") != true || current.Truthy("pending_site_choice") || challenge is not null;
        var editorOpen = editorRequired || editingConnection;
        form.IsVisible = editorOpen;
        editConnection.IsVisible = !(current.Bool("connected") != true || editorRequired);
        editConnection.IsEnabled = !busy;
        editConnection.SetLabel(editorOpen ? "Hide connection details" : "Edit connection");
        editorHint.IsVisible = !(editorOpen || !FormChanged());
        submit.SetLabel(busy && !savingPreference && !discovering ? "Connecting…"
            : RememberOnConnect() ? "Connect and save login"
            : current.Truthy("pending_site_choice") && MatchesForm(current["pending_site_choice"]) ? "Connect selected site"
            : current.Truthy("connected") ? "Update connection" : "Connect controller");
        var preference = Connections.ControllerReconnectControls(current, reconnectDraft);
        reconnectSection.IsVisible = preference.Supported;
        programmatic = true;
        try { reconnect.IsChecked = preference.Checked; }
        finally { programmatic = false; }
        reconnect.IsEnabled = !busy;
        applyPreference.IsEnabled = !(busy || !preference.CanApply);
        applyPreference.SetLabel(savingPreference ? "Saving…" : "Save reconnect preference");
        reconnectHelp.Text = preference.Changed && !preference.Checked
            ? $"Saving this preference removes the saved controller login.{(current.Truthy("connected") ? " The current connection stays active." : "")}"
            : preference.Checked ? "Save this controller login securely on this PC and reconnect when EE opens."
            : "Enable this to save the controller login and reconnect when EE opens.";
        reconnectHint.Text = RememberOnConnect() && editorOpen ? ""
            : preference.Changed && preference.Checked && !current.Truthy("can_save_login") ? "Connect your controller to save this login."
            : preference.Checked && current.Truthy("auto_reconnect") && current.Truthy("can_save_login") && current.Bool("saved_login_matches_connection") == false
                ? "Save reconnect preference to save the current controller login."
            : preference.Checked && current.Truthy("auto_reconnect") && current.Str("reconnect_state") == "paused" ? "Reconnect will resume when EE opens again." : "";
        reconnectHint.IsVisible = reconnectHint.Text.Length > 0;
    }

    IEnumerable<Control> FormControls() =>
        [kind, auth, address, username, password, apiKey, consoleId, findConsoles, verification, site, trust, submit, cancelChange, passwordRow.Children[1], apiKeyField.Children[1]];

    void ShowDetails(JsonObject value)
    {
        introduction.Text = value.Truthy("connected") ? "Controller details for this switch." : "Connect UniFi Network to show controller details for your switch.";
        var siteId = value.Str("site");
        var siteName = value.Objects("sites").FirstOrDefault(item => item.Str("id") == siteId)?.Str("name") is { Length: > 0 } named ? named : value.At("site") is { } raw && raw.Truthy() ? Text(raw) : null;
        var facts = new List<(string, string)>();
        if (siteName is not null) facts.Add(("Site", siteName));
        if (value.At("switch_name") is { } switchName && switchName.Truthy()) facts.Add(("Switch", Text(switchName)));
        if (value.Truthy("connected")) facts.Add(("Ports identified", value.At("mapped_ports") is { } mapped ? Text(mapped) : "0"));
        details.Set(facts);
        details.IsVisible = !details.IsEmpty;
        disconnect.IsVisible = value.Truthy("configured");
        capabilityHint.Text = "Connect with a controller account to show device names.";
        capabilityHint.IsVisible = value.Truthy("connected") && value.Str("auth_method") == "api_key" && value.Bool("capabilities.client_mapping") == false;
    }

    static string Text(JsonNode node) => node.Str() ?? (node.Num() is { } number ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : node.ToJsonString());

    void Show(JsonObject? value)
    {
        if (!active) return;
        current = value ?? [];
        state.Set(AppShell.ControllerStatus(current, false));
        ShowDetails(current);
        SyncTarget();
        ShowSites();
        if (!dirty) cleanForm = FormValues();
        if (current.Truthy("error")) Status(Connections.ControllerError(current["error"], "Controller data is unavailable. Check the connection and try again."), true);
        else if (current.Truthy("pending_site_choice")) PendingSiteMessage();
        if (current.Truthy("saved_login_error"))
            preferenceNotice.Set(Connections.ControllerError(current["saved_login_error"], "The saved controller login needs attention. Connect your controller again."), true);
        else preferenceNotice.Set();
        if (SupportsVerification() && current.Str("error.code") is "two_factor_required" or "verification_failed")
        {
            editingConnection = true;
            twoFactor.Open = true;
            verificationRequired = true;
        }
        SetBusy();
    }

    void Perform(Func<Task<JsonNode?>> action, string label, string actionKind = "status", bool remember = false)
    {
        if (!active || pending is not null) return;
        var canceledTarget = actionKind == "cancel" && current.Truthy("pending_site_choice") ? current["pending_site_choice"] : null;
        var resetCanceledForm = canceledTarget is not null && MatchesForm(canceledTarget);
        Status(label);
        pending = Run();
        SetBusy();

        async Task Run()
        {
            bool focusVerification = false, focusSite = false, focusSummary = false;
            await Task.Yield();
            try
            {
                var result = await action() as JsonObject ?? [];
                if (!active) return;
                Show(result);
                focusVerification = SupportsVerification() && result.Str("error.code") is "two_factor_required" or "verification_failed";
                focusSite = result.Truthy("pending_site_choice") && MatchesForm(result["pending_site_choice"]) || result.Str("error.code") == "site_required";
                var canceled = actionKind == "cancel" && !result.Truthy("pending_site_choice");
                if (!result.Truthy("error") && !result.Truthy("pending_site_choice"))
                    Status(actionKind == "status" ? "" : canceled ? "Connection change canceled." : result.Truthy("connected") ? "Controller connected."
                        : actionKind == "disconnect" ? "Controller disconnected." : "Controller not connected.", false, AppShell.ControllerStatus(result, false).Tone);
                var connected = actionKind == "connect" && result.Truthy("connected") && !result.Truthy("error") && !result.Truthy("pending_site_choice") && MatchesForm(result);
                var disconnected = actionKind == "disconnect" && !result.Truthy("configured") && !result.Truthy("error");
                if (connected || disconnected)
                {
                    dirty = false;
                    SetText(password, "");
                    SetText(apiKey, "");
                    SetText(verification, "");
                    verificationRequired = false;
                    twoFactor.Open = false;
                    challenge = null;
                    trustPanel.IsVisible = false;
                }
                if (connected)
                {
                    SetText(username, "");
                    editingConnection = false;
                    focusSummary = true;
                    if (remember)
                    {
                        if (Connections.ControllerReconnectConfirmed(result, true))
                        {
                            reconnectDraft = null;
                            Status("Controller connected and login saved.");
                        }
                        else
                        {
                            reconnectDraft = true;
                            preferenceNotice.Set("Controller connected. Select Save reconnect preference to retry saving this login.", true);
                        }
                    }
                }
                if (canceled && resetCanceledForm)
                {
                    dirty = false;
                    SetText(username, "");
                    SetText(password, "");
                    SetText(apiKey, "");
                    SetText(verification, "");
                    verificationRequired = false;
                    twoFactor.Open = false;
                    certificate = null;
                    challenge = null;
                    trustPanel.IsVisible = false;
                    SetText(address, result.Str("url") ?? "");
                    SyncTarget();
                    ShowSites();
                    editingConnection = false;
                    focusSummary = true;
                }
                if (disconnected)
                {
                    certificate = null;
                    editingConnection = false;
                }
                if (!dirty) cleanForm = FormValues();
                await onChanged(result);
            }
            catch (Exception exception)
            {
                if (!active) return;
                var code = (exception as ApiException)?.Code;
                var value = (exception as ApiException)?.Details.Str("certificate_sha256");
                if (code is "two_factor_required" or "verification_failed")
                {
                    if (SupportsVerification())
                    {
                        editingConnection = true;
                        twoFactor.Open = true;
                        verificationRequired = true;
                        focusVerification = true;
                        Status(Connections.ControllerError(exception), true);
                    }
                    else Status(Connections.ControllerMessages["controller_local_account_required"], true);
                }
                else if (code is "certificate_untrusted" or "certificate_changed" && kind.Value == "cloud_connector")
                {
                    challenge = null;
                    certificate = null;
                    trustPanel.IsVisible = false;
                    Status("The UniFi cloud certificate could not be verified. Check this computer’s date and trusted certificates, then try again.", true);
                }
                else if (code is "certificate_untrusted" or "certificate_changed" && Regex.IsMatch(value ?? "", "^[a-fA-F0-9]{64}$"))
                {
                    var target = FormTarget();
                    challenge = new JsonObject
                    {
                        ["fingerprint"] = value, ["url"] = target.Url, ["controller_kind"] = target.ControllerKind, ["auth_method"] = target.AuthMethod, ["console_id"] = target.ConsoleId,
                    };
                    fingerprint.Text = string.Join(" ", value!.ToUpperInvariant().Chunk(4).Select(part => new string(part)));
                    trustPanel.IsVisible = true;
                    Status(code == "certificate_changed" ? "The controller certificate changed. Verify the new fingerprint before connecting."
                        : "The controller uses a certificate that needs your approval.", true);
                }
                else Status(Connections.ControllerError(exception), true);
            }
            finally
            {
                pending = null;
                SetBusy();
                if (active && focusVerification) verification.Focus();
                else if (active && focusSite) site.Focus();
                else if (active && focusSummary && editConnection.IsVisible) editConnection.Focus();
                else if (active && actionKind == "cancel") submit.Focus();
            }
        }
    }

    void Connect()
    {
        if (!active || pending is not null) return;
        var target = FormTarget();
        var problem = Connections.ControllerConnectionProblem(target, username.Text ?? "", password.Text ?? "", apiKey.Text ?? "", current);
        if (problem is { } found)
        {
            Status(found.Message, true);
            Control field = found.Field switch
            {
                "url" => address, "username" => username, "password" => password, "controller_kind" => kind, "auth_method" => auth, "api_key" => apiKey, _ => consoleId,
            };
            field.Focus();
            return;
        }
        if (target.ControllerKind == "cloud_connector" && consoles.Any(item => item.Id == target.ConsoleId && item.Blocked))
        {
            Status("Choose an available console.", true);
            consoleId.Focus();
            return;
        }
        if (siteRequired && site.Value.Length == 0)
        {
            Status(Connections.ControllerMessages["site_required"], true);
            site.Focus();
            return;
        }
        // form.reportValidity(): the verification code is the one constraint the checks above leave.
        var code = verification.Text ?? "";
        if (verification.IsEnabled && (verificationRequired && code.Length == 0 || code.Length > 0 && !Regex.IsMatch(code, "^[0-9]{6,8}$")))
        {
            twoFactor.Open = true;
            Status(code.Length == 0 ? "Please fill out this field." : "Please match the requested format.", true);
            verification.Focus();
            return;
        }
        var payload = new JsonObject { ["url"] = target.Url!.Trim(), ["controller_kind"] = target.ControllerKind, ["site"] = site.Value.Length > 0 ? site.Value : null };
        var remember = RememberOnConnect();
        if (remember) payload["remember"] = true;
        if (current.ContainsKey("supported_auth_methods")) payload["auth_method"] = target.AuthMethod;
        if (target.ControllerKind == "cloud_connector") payload["console_id"] = target.ConsoleId;
        else payload["certificate_sha256"] = certificate;
        if (target.AuthMethod == "api_key") payload["api_key"] = apiKey.Text ?? "";
        else
        {
            payload["username"] = (username.Text ?? "").Trim();
            payload["password"] = password.Text ?? "";
            if (SupportsVerification()) payload["verification_code"] = code.Trim();
        }
        SetText(verification, "");
        Perform(() => request("/api/controller/connect", "POST", payload), "Connecting to UniFi Network…", "connect", remember);
    }

    void FindConsoles()
    {
        if (!active || pending is not null || kind.Value != "cloud_connector" || auth.Value != "api_key" || current.Bool("console_discovery_supported") != true) return;
        if (!Connections.ValidApiKey(apiKey.Text ?? ""))
        {
            Status(Connections.ControllerMessages["api_key_required"], true);
            apiKey.Focus();
            return;
        }
        discovering = true;
        Status("Finding consoles…");
        pending = Run();
        SetBusy();

        async Task Run()
        {
            await Task.Yield();
            try
            {
                var result = await request("/api/controller/consoles", "POST", new JsonObject { ["api_key"] = apiKey.Text ?? "" });
                if (!active) return;
                if (result.At("consoles") is not JsonArray list || list.Count > 1000 || list.Any(item => item is not JsonObject entry ||
                        !Regex.IsMatch(entry.Str("id") ?? "", @"^[A-Za-z0-9_-]{1,128}(?::[0-9]{1,20})?$") || entry.Str("name") is not { } name ||
                        name.Trim().Length == 0 || name.Length > 512 || entry.Bool("blocked") is null))
                    throw new ApiException("", "api_response_incomplete", 0, false, null);
                consoles = list.OfType<JsonObject>().Select(entry => (entry.Str("id")!, entry.Str("name")!, entry.Bool("blocked") == true)).ToList();
                ShowConsoles();
                if (consoles.Count == 0) Status(Connections.ControllerMessages["no_consoles"], true);
                else if (consoles.All(item => item.Blocked)) Status("These consoles are blocked in UniFi. Check console access in Site Manager.", true);
                else Status("Choose the console to connect.");
            }
            catch (Exception exception)
            {
                if (active) Status(Connections.ControllerError(exception), true);
            }
            finally
            {
                pending = null;
                discovering = false;
                SetBusy();
                if (active) consoleId.Focus();
            }
        }
    }

    void ApplyPreference()
    {
        var preference = Connections.ControllerReconnectControls(current, reconnectDraft);
        if (!active || pending is not null || !preference.CanApply) return;
        var enabled = preference.Checked;
        savingPreference = true;
        preferenceNotice.Set("Saving controller preference…");
        pending = Run();
        SetBusy();

        async Task Run()
        {
            await Task.Yield();
            try
            {
                var result = await request("/api/controller/preferences", "PUT", new JsonObject { ["auto_reconnect"] = enabled }) as JsonObject ?? [];
                if (!active) return;
                Show(result);
                if (!Connections.ControllerReconnectConfirmed(result, enabled))
                {
                    preferenceNotice.Set(Connections.ControllerError(result["saved_login_error"],
                        "The setting could not be confirmed. Select Refresh controller before trying again."), true);
                    return;
                }
                reconnectDraft = null;
                preferenceNotice.Set(enabled ? "Automatic reconnect enabled. Controller login saved." : "Automatic reconnect disabled. Saved controller login removed.");
                try { await onChanged(result); }
                catch (Exception)
                {
                    if (active) Status("The setting was saved. Select Refresh controller to update its details.", true);
                }
            }
            catch (Exception exception)
            {
                if (active) preferenceNotice.Set(Connections.ControllerError(exception, "The controller setting could not be saved. Try Save reconnect preference again."), true);
            }
            finally
            {
                pending = null;
                savingPreference = false;
                SetBusy();
            }
        }
    }

    void ResetControllerChallenge()
    {
        certificate = null;
        challenge = null;
        trustPanel.IsVisible = false;
        siteContextKey = "";
        siteField.IsVisible = false;
        siteRequired = false;
        site.SetOptions([("", "Automatic", false)], "");
        SetText(verification, "");
        verificationRequired = false;
        twoFactor.Open = false;
    }

    /// <summary>A live controller status from the stream (cleanup.update).</summary>
    public void Update(JsonNode? value, bool unavailable = false)
    {
        if (!active || pending is not null) return;
        var presentation = AppShell.ControllerStatus(value, unavailable);
        var keyParts = new JsonArray(presentation.Label, presentation.Tone);
        foreach (var name in new[]
                 {
                     "configured", "controller_kind", "auth_method", "console_id", "supported_auth_methods", "capabilities", "pending_site_choice", "site", "sites", "switch_name",
                     "mapped_ports", "error.code", "error.operation", "error.upstream_status", "preferences_supported", "auto_reconnect", "can_save_login", "saved_login_available",
                     "saved_login_matches_connection", "saved_login_error", "reconnect_state", "remember_on_connect_supported", "saved_target", "url",
                 })
            keyParts.Add(value.At(name)?.DeepClone());
        var key = keyParts.ToJsonString();
        if (key == liveStatusKey) return;
        liveStatusKey = key;
        state.Set(presentation);
        if (unavailable)
        {
            Status();
            return;
        }
        current = value as JsonObject ?? [];
        ShowDetails(current);
        SyncTarget();
        ShowSites();
        if (!dirty) cleanForm = FormValues();
        if (current.Truthy("error")) Status(Connections.ControllerError(current["error"]), true);
        else if (current.Truthy("pending_site_choice")) PendingSiteMessage();
        else if (!current.Truthy("connected") || current.Truthy("stale") || message.IsError) Status();
        SetBusy();
    }

    public bool HasChanges => FormChanged() || Connections.ControllerReconnectControls(current, reconnectDraft).Changed;

    public async Task<bool> CanLeave()
    {
        if (pending is { } task)
        {
            try { await task; } catch (Exception) { }
        }
        return true;
    }

    public void Close()
    {
        active = false;
        SetText(password, "");
        SetText(apiKey, "");
        SetText(verification, "");
        certificate = null;
        challenge = null;
    }
}
