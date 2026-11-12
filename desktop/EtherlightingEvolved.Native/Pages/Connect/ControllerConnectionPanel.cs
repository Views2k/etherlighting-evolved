using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Shell;
using EtherlightingEvolved.Native.Theme;
using static EtherlightingEvolved.Native.Pages.Connect.Connections;

namespace EtherlightingEvolved.Native.Pages.Connect;

/// <summary>
/// The UniFi Network controller connection in Settings, a port of renderControllerConnection
/// (preview/connections.mjs): controller type, sign-in method, address, account or API key, cloud consoles,
/// two-factor codes, site choice, certificate trust, the automatic reconnect preference and disconnecting.
/// </summary>
public sealed class ControllerConnectionPanel : UserControl
{
    sealed record Challenge(string Fingerprint, ControllerTarget Target);

    sealed record ConsoleItem(string Id, string Name, bool Blocked);

    readonly Func<string, string, JsonNode?, Task<JsonNode?>> request;
    readonly Func<JsonNode?, Task> onChanged;
    readonly TextBlock introduction = ConnectionUi.HelpText("Connect UniFi Network to show controller details for your switch.");
    readonly Button reload = ConnectionUi.Button("Refresh controller", "quiet");
    readonly StateLine state = new("Checking controller connection…", "is-disconnected");
    readonly Grid details = new() { ColumnDefinitions = new ColumnDefinitions { new(0.7, GridUnitType.Star) { MinWidth = 130 }, new(1.3, GridUnitType.Star) }, RowSpacing = 13, ColumnSpacing = 20 };
    readonly TextBlock capabilityHint = ConnectionUi.HelpText();
    readonly Notice message = new();
    readonly BlockStack reconnectSection = new();
    readonly StackPanel reconnectLabel;
    readonly CheckBox reconnect;
    readonly TextBlock reconnectHelp = ConnectionUi.HelpText("Save this controller login securely on this PC.");
    readonly TextBlock reconnectHint = ConnectionUi.HelpText();
    readonly Button applyPreference = ConnectionUi.Button("Save reconnect preference", "primary");
    readonly Notice preferenceNotice = new();
    readonly StackPanel form = new() { Spacing = 18 };
    readonly Grid formHost;
    readonly Button editConnection = ConnectionUi.Button("Edit connection");
    readonly TextBlock editorHint = ConnectionUi.HelpText("Connection edits are not applied.");
    readonly SelectBox kind = new();
    readonly TextBlock kindHelp = ConnectionUi.Small();
    readonly SelectBox auth = new();
    readonly TextBlock authHelp = ConnectionUi.Small();
    readonly StackPanel authField;
    readonly TextBox address = ConnectionUi.Input(2048, "https://controller:8443");
    readonly TextBox username = ConnectionUi.Input(128);
    readonly TextBox password = ConnectionUi.Input(password: true);
    readonly StackPanel usernameField;
    readonly StackPanel passwordRow;
    readonly TextBox apiKey = ConnectionUi.Input(4096, password: true);
    readonly StackPanel apiKeyField;
    readonly TextBlock keyHelp = ConnectionUi.Small();
    readonly SelectBox consoleId = new();
    readonly StackPanel consoleField;
    readonly Button findConsoles = ConnectionUi.Button("Find consoles", "quiet");
    readonly TextBox verification = ConnectionUi.Input(8);
    readonly Disclosure twoFactor = new("Two-factor authentication");
    readonly SelectBox site = new();
    readonly StackPanel siteField;
    readonly Button submit = ConnectionUi.Button("Connect controller", "primary");
    readonly Button disconnect = ConnectionUi.Button("Disconnect controller", "quiet");
    readonly Button cancelChange = ConnectionUi.Button("Cancel connection change", "quiet");
    readonly Border trustPanel;
    readonly SelectableTextBlock fingerprint = new() { FontSize = 12, FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"), TextWrapping = TextWrapping.Wrap };
    readonly Button trust = ConnectionUi.Button("Trust this certificate and connect");
    readonly Disclosure setupHelp = new("Where do I find these details?");
    readonly TextBlock addressHelp = ConnectionUi.HelpText();
    readonly TextBlock credentialHelp = ConnectionUi.HelpText();
    readonly List<Control> formElements;
    readonly Dictionary<string, string> methodDrafts = new(StringComparer.Ordinal);
    bool active = true, dirty, editingConnection, savingPreference, discovering, verificationRequired, siteRequired, setting;
    Task? pending;
    JsonObject current = [];
    string? certificate;
    Challenge? challenge;
    bool? reconnectDraft;
    string? liveStatusKey;
    string siteContextKey = "", localAddress = "", lastKind = "self_hosted", cleanForm;
    List<ConsoleItem> consoles = [];

    public ControllerConnectionPanel(Func<string, string, JsonNode?, Task<JsonNode?>> request, Func<JsonNode?, Task>? onChanged = null)
    {
        this.request = request;
        this.onChanged = onChanged ?? (_ => Task.CompletedTask);
        var title = new StackPanel { Children = { ConnectionUi.Heading("UniFi Network"), introduction } };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 18, Classes = { "connection-heading" } };
        reload.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(reload, 1);
        header.Children.Add(title);
        header.Children.Add(reload);
        state.Margin = new Thickness(0, 16, 0, 0);
        details.Margin = new Thickness(0, 16);
        capabilityHint.IsVisible = false;

        (reconnectLabel, reconnect) = ConnectionUi.CheckRow("Reconnect controller automatically", "muted", 28);
        var preferenceActions = new FlowRow(10, applyPreference) { Classes = { "connection-actions" } };
        // section.connection-reconnect.automation-section: a rule above, 18px padding, 12px between rows.
        var reconnectRows = new StackPanel { Spacing = 12, Children = { reconnectLabel, reconnectHelp, reconnectHint, preferenceActions, preferenceNotice } };
        reconnectSection.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 18, 0, 0), Child = reconnectRows }.Bind(Border.BorderBrushProperty, "line"));
        reconnectSection.Margin = new Thickness(0, 20, 0, 0);
        reconnectSection.IsVisible = false;

        editConnection.IsVisible = false;
        editorHint.IsVisible = false;
        var kindField = ConnectionUi.Field("Controller type", kind);
        kindField.Children.Add(kindHelp);
        authField = ConnectionUi.Field("Sign-in method", auth);
        authField.Children.Add(authHelp);
        var addressField = ConnectionUi.Field("Controller address", address);
        usernameField = ConnectionUi.Field("Username", username);
        (passwordRow, var revealPassword) = ConnectionUi.PasswordField("Controller password", password);
        (apiKeyField, var revealKey) = ConnectionUi.PasswordField("API key", apiKey);
        apiKeyField.Children.Add(keyHelp);
        findConsoles.HorizontalAlignment = HorizontalAlignment.Stretch;
        consoleField = new StackPanel { Spacing = 7, Classes = { "field" }, Children = { ConnectionUi.FieldLabel("Console"), consoleId, findConsoles, ConnectionUi.Small("Enter an API key, then find and choose your console.") } };
        var verificationField = ConnectionUi.Field("Verification code", verification, "Enter your account’s current verification code.");
        verificationField.Margin = new Thickness(0, 14, 0, 0);
        verificationField.Width = 150;
        verificationField.HorizontalAlignment = HorizontalAlignment.Left;
        twoFactor.Body.Children.Add(verificationField);
        site.SetOptions([("", "Automatic", false)]);
        siteField = ConnectionUi.Field("UniFi site", site);
        siteField.IsVisible = false;
        cancelChange.IsVisible = false;
        disconnect.IsVisible = false;
        var actions = new FlowRow(10, submit, cancelChange) { Classes = { "connection-actions" } };
        var connectionActions = new FlowRow(10, editConnection, disconnect) { Classes = { "connection-actions" } };
        trust.HorizontalAlignment = HorizontalAlignment.Stretch;
        trustPanel = new Border
        {
            IsVisible = false, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "Verify this SHA-256 fingerprint against your controller, then trust it to continue.", FontSize = 13, LineHeight = ConnectionUi.HelpLine, TextWrapping = TextWrapping.Wrap },
                    fingerprint, trust,
                },
            },
        }.Bind(Border.BackgroundProperty, "notice-surface").Bind(Border.BorderBrushProperty, "line");
        trustPanel[!Avalonia.Controls.Documents.TextElement.ForegroundProperty] = Look.Res("muted");
        setupHelp.Body.Children.Add(addressHelp);
        setupHelp.Body.Children.Add(credentialHelp);
        foreach (var child in new Control[] { kindField, authField, addressField, usernameField, passwordRow, apiKeyField, consoleField, setupHelp, twoFactor, siteField, trustPanel, actions })
            form.Children.Add(child);
        formElements = [kind, auth, address, username, password, revealPassword, apiKey, revealKey, consoleId, findConsoles, verification, site, trust, submit, cancelChange];

        // section.controller-connection.connection-card.panel, at most 720px wide; its form at most 480px.
        formHost = ConnectionUi.LeftColumn(form, 480);
        formHost.Margin = new Thickness(0, 22, 0, 0);
        var section = new BlockStack(header, state, details, capabilityHint, message, reconnectSection, connectionActions, editorHint, formHost);
        var card = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 20), Child = section, Classes = { "controller-connection", "connection-card", "panel" },
        }.Bind(Border.BorderBrushProperty, "line");
        var host = ConnectionUi.LeftColumn(card, 720);
        host.Margin = new Thickness(0, 20, 0, 0);
        Content = host;

        RenderChoices("self_hosted", "password");
        ShowConsoles();
        ShowKind();
        cleanForm = FormValues();

        foreach (var input in new[] { address, username, password, apiKey, verification })
            input.TextChanging += (_, _) =>
            {
                if (!setting) dirty = true;
            };
        foreach (var input in new[] { address, username, password, apiKey, verification })
            input.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                if (submit.IsEffectivelyEnabled) Connect();
            };
        address.TextChanging += (_, _) =>
        {
            if (setting) return;
            ResetControllerChallenge();
            ShowSites();
            SetBusy();
        };
        kind.Changed += () =>
        {
            var chosen = kind.Value;
            methodDrafts[lastKind] = auth.Value;
            Set(() =>
            {
                if (chosen == "cloud_connector" && lastKind != chosen)
                {
                    localAddress = address.Text ?? "";
                    address.Text = "https://api.ui.com";
                }
                else if (lastKind == "cloud_connector" && chosen != lastKind) address.Text = localAddress;
            });
            dirty = true;
            RenderChoices(chosen, methodDrafts.GetValueOrDefault(chosen) is { Length: > 0 } method ? method : "password", true);
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
        site.Changed += () => dirty = true;
        apiKey.TextChanging += (_, _) =>
        {
            if (setting) return;
            dirty = true;
            consoles = [];
            ShowConsoles("");
            ResetControllerChallenge();
            ShowSites();
            SetBusy();
        };
        username.TextChanging += (_, _) =>
        {
            if (setting) return;
            Set(() => verification.Text = "");
            verificationRequired = false;
        };
        findConsoles.Click += (_, _) => FindConsoles();
        submit.Click += (_, _) => Connect();
        trust.Click += (_, _) =>
        {
            if (challenge is null || kind.Value == "cloud_connector" || !SameControllerTarget(challenge.Target, Values())) return;
            certificate = challenge.Fingerprint;
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
            if (current.Truthy("pending_site_choice")) Perform(() => request("/api/controller/cancel", "POST", null), "Canceling connection change…", "cancel");
        };
        disconnect.Click += (_, _) => Perform(() => request("/api/controller/disconnect", "POST", null), "Disconnecting controller…", "disconnect");
        reconnect.IsCheckedChanged += (_, _) =>
        {
            if (setting) return;
            reconnectDraft = reconnect.IsChecked == true;
            PreferenceStatus();
            SetBusy();
        };
        applyPreference.Click += (_, _) => ApplyPreference();
        Perform(() => request("/api/controller", "GET", null), "Checking controller status…");
    }

    public bool HasChanges => FormChanged() || ControllerReconnectControls(current, reconnectDraft).Changed;

    public async Task<bool> CanLeave()
    {
        if (pending is { } running) await running;
        return true;
    }

    /// <summary>Ends the panel (the renderer's cleanup): secrets are cleared and later results ignored.</summary>
    public void Close()
    {
        active = false;
        Set(() =>
        {
            password.Text = "";
            apiKey.Text = "";
            verification.Text = "";
        });
        certificate = null;
        challenge = null;
    }

    /// <summary>Shows the controller status from a workspace frame (cleanup.update).</summary>
    public void Update(JsonNode? value, bool unavailable = false)
    {
        if (!active || pending is not null) return;
        var presentation = AppShell.ControllerStatus(value, unavailable);
        var key = new JsonArray(
            new JsonArray(presentation.Label, presentation.Tone), value.At("configured")?.DeepClone(), value.At("controller_kind")?.DeepClone(),
            value.At("auth_method")?.DeepClone(), value.At("console_id")?.DeepClone(), value.At("supported_auth_methods")?.DeepClone(),
            value.At("capabilities")?.DeepClone(), value.At("pending_site_choice")?.DeepClone(), value.At("site")?.DeepClone(), value.At("sites")?.DeepClone(),
            value.At("switch_name")?.DeepClone(), value.At("mapped_ports")?.DeepClone(), value.At("error.code")?.DeepClone(), value.At("error.operation")?.DeepClone(),
            value.At("error.upstream_status")?.DeepClone(), value.At("preferences_supported")?.DeepClone(), value.At("auto_reconnect")?.DeepClone(),
            value.At("can_save_login")?.DeepClone(), value.At("saved_login_available")?.DeepClone(), value.At("saved_login_matches_connection")?.DeepClone(),
            value.At("reconnect_state")?.DeepClone()).ToJsonString();
        if (key == liveStatusKey) return;
        liveStatusKey = key;
        state.Show(presentation.Label, presentation.Tone);
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
        if (current.Truthy("error")) Status(ControllerError(current["error"]), true);
        else if (current.Truthy("pending_site_choice")) PendingSiteMessage();
        else if (!current.Truthy("connected") || current.Truthy("stale") || message.IsError) Status();
        SetBusy();
    }

    void Set(Action change)
    {
        var was = setting;
        setting = true;
        try { change(); }
        finally { setting = was; }
    }

    void Status(string text = "", bool error = false, string tone = "") => message.Set(text, error, tone);

    void PreferenceStatus(string text = "", bool error = false) => preferenceNotice.Set(text, error);

    void StartPending(Func<Task> work)
    {
        var task = work();
        if (!task.IsCompleted) pending = task;
    }

    void RenderChoices(string selectedKind, string selectedMethod, bool useDefault = false)
    {
        var supported = ControllerSupportedMethods(current);
        var kinds = new List<(string, string, bool)>();
        foreach (var (value, label) in new[] { ("self_hosted", "Self-hosted Network"), ("unifi_os", "UniFi Console or OS Server"), ("cloud_connector", "UniFi cloud connector") })
            if (supported[value].Count > 0) kinds.Add((value, label, false));
        kind.SetOptions(kinds);
        kind.Value = supported.TryGetValue(selectedKind, out var forKind) && forKind.Count > 0 ? selectedKind : "";
        var methods = supported.TryGetValue(kind.Value, out var list) ? list : [];
        auth.SetOptions(methods.Select(value => (value, value == "api_key" ? "API key" : "Controller account", false)));
        auth.Value = methods.Contains(selectedMethod) ? selectedMethod : useDefault ? methods.FirstOrDefault() ?? "" : "";
        authField.IsVisible = supported.Values.Any(entry => entry.Contains("api_key"));
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
            twoFactor.IsOpen = false;
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
        foreach (var item in consoles) options.Add((item.Id, item.Name, item.Blocked));
        consoleId.SetOptions(options);
        consoleId.Value = consoles.FirstOrDefault(item => item.Id == selected)?.Blocked == true ? "" : selected;
    }

    string FormValues() => new JsonArray(kind.Value, auth.Value, address.Text ?? "", username.Text ?? "", password.Text ?? "", apiKey.Text ?? "", consoleId.Value,
        verification.Text ?? "", site.Value).ToJsonString();

    bool FormChanged() => FormValues() != cleanForm;

    ControllerTarget FormTarget() => new(address.Text ?? "", kind.Value, auth.Value, kind.Value == "cloud_connector" ? consoleId.Value.Trim() : null);

    ControllerValues Values()
    {
        var target = FormTarget();
        return new ControllerValues(target.Url, target.ControllerKind, target.AuthMethod, target.ConsoleId, username.Text ?? "", password.Text ?? "", apiKey.Text ?? "");
    }

    bool MatchesForm(JsonNode? target) => SameControllerTarget(ControllerTarget.From(target), Values());

    void SyncTarget()
    {
        if (dirty && FormChanged()) return;
        dirty = false;
        var target = current.At("pending_site_choice").Truthy() ? current.At("pending_site_choice") : current;
        Set(() =>
        {
            if (target.Str("url") is { Length: > 0 } url) address.Text = url;
        });
        RenderChoices(target.Str("controller_kind") is { Length: > 0 } controllerKind ? controllerKind : "self_hosted",
            target.Str("auth_method") is { Length: > 0 } method ? method : "password");
        ShowConsoles(target.Str("console_id") ?? "");
        lastKind = kind.Value;
        if (kind.Value != "cloud_connector") localAddress = address.Text ?? "";
        ShowKind();
    }

    void ShowSites()
    {
        var candidate = current.At("pending_site_choice");
        if (!candidate.Truthy()) candidate = null;
        var target = candidate is not null && MatchesForm(candidate) ? candidate : MatchesForm(current) ? current : null;
        var isCandidate = target is not null && ReferenceEquals(target, candidate);
        var sites = target.Arr("sites").OfType<JsonObject>().ToList();
        var key = target is not null
            ? $"{(isCandidate ? "pending" : "active")}|{ControllerOrigin(target.Str("url"))}|{(target.Str("controller_kind") is { Length: > 0 } k ? k : "self_hosted")}|" +
              $"{(target.Str("auth_method") is { Length: > 0 } m ? m : "password")}|{target.Str("console_id") ?? ""}"
            : "";
        var selected = key == siteContextKey ? site.Value : isCandidate ? "" : Text(target.At("site"));
        siteContextKey = key;
        var options = new List<(string, string, bool)> { ("", isCandidate || sites.Count > 1 ? "Choose a site" : "Automatic", false) };
        foreach (var item in sites) options.Add((Text(item["id"]), item.Str("name") is { Length: > 0 } name ? name : Text(item["id"]), false));
        site.SetOptions(options);
        site.Value = sites.Any(item => Text(item["id"]) == selected) ? selected : "";
        siteRequired = isCandidate || (target is not null && current.Str("error.code") == "site_required");
        siteField.IsVisible = sites.Count > 0;
        cancelChange.IsVisible = candidate is not null;
    }

    /// <summary>A JSON value as the browser shows it in text: strings as they are, numbers in their shortest form.</summary>
    static string Text(JsonNode? value) => value is null ? "" : value.Str() ?? (value is JsonValue ? value.ToJsonString() : "");

    void PendingSiteMessage() => Status("Choose the site that contains your switch to finish this connection change.");

    void SetBusy()
    {
        if (!active) return;
        var busy = pending is not null;
        foreach (var control in formElements) control.IsEnabled = !busy;
        auth.IsEnabled = !(busy || (ControllerSupportedMethods(current).GetValueOrDefault(kind.Value)?.Count ?? 0) < 2);
        username.IsEnabled = password.IsEnabled = !(busy || auth.Value == "api_key");
        apiKey.IsEnabled = !(busy || auth.Value != "api_key");
        verification.IsEnabled = !(busy || !SupportsVerification());
        consoleId.IsEnabled = !(busy || kind.Value != "cloud_connector");
        findConsoles.IsVisible = current.Bool("console_discovery_supported") == true;
        findConsoles.IsEnabled = !(busy || kind.Value != "cloud_connector" || auth.Value != "api_key" || current.Bool("console_discovery_supported") != true ||
                                   !PrintableKey(apiKey.Text));
        findConsoles.Content = discovering ? "Finding consoles…" : consoles.Count > 0 ? "Refresh consoles" : "Find consoles";
        reload.IsEnabled = !busy;
        disconnect.IsEnabled = !busy;
        var editorRequired = !current.Truthy("connected") || current.Truthy("pending_site_choice") || challenge is not null;
        var editorOpen = editorRequired || editingConnection;
        formHost.IsVisible = editorOpen;
        editConnection.IsVisible = !(!current.Truthy("connected") || editorRequired);
        editConnection.IsEnabled = !busy;
        editConnection.Content = editorOpen ? "Hide connection details" : "Edit connection";
        editorHint.IsVisible = !(editorOpen || !FormChanged());
        submit.Content = busy && !savingPreference && !discovering ? "Connecting…"
            : current.Truthy("pending_site_choice") && MatchesForm(current.At("pending_site_choice")) ? "Connect selected site"
            : current.Truthy("connected") ? "Update connection" : "Connect controller";
        var preference = ControllerReconnectControls(current, reconnectDraft);
        reconnectSection.IsVisible = preference.Supported;
        Set(() => reconnect.IsChecked = preference.Checked);
        reconnect.IsEnabled = !busy;
        applyPreference.IsEnabled = !(busy || !preference.CanApply);
        applyPreference.Content = savingPreference ? "Saving…" : "Save reconnect preference";
        reconnectHelp.Text = preference.Changed && !preference.Checked
            ? $"Saving this preference removes the saved controller login.{(current.Truthy("connected") ? " The current connection stays active." : "")}"
            : preference.Checked ? "Save this controller login securely on this PC and reconnect when EE opens."
            : "Enable this to save the controller login and reconnect when EE opens.";
        reconnectHint.Text = preference.Changed && preference.Checked && !current.Truthy("can_save_login") ? "Connect your controller to save this login."
            : preference.Checked && current.Truthy("auto_reconnect") && current.Truthy("can_save_login") && current.Bool("saved_login_matches_connection") == false
                ? "Save reconnect preference to save the current controller login."
            : preference.Checked && current.Truthy("auto_reconnect") && current.Str("reconnect_state") == "paused" ? "Reconnect will resume when EE opens again."
            : "";
        reconnectHint.IsVisible = reconnectHint.Text.Length > 0;
    }

    void ShowDetails(JsonObject value)
    {
        details.Children.Clear();
        details.RowDefinitions.Clear();
        introduction.Text = value.Truthy("connected") ? "Controller details for this switch." : "Connect UniFi Network to show controller details for your switch.";
        var siteName = value.Objects("sites").FirstOrDefault(item => Json.Same(item["id"], value["site"]))?["name"] is { } named && named.Truthy() ? Text(named)
            : value.Truthy("site") ? Text(value["site"]) : null;
        var facts = new List<(string, string)>();
        if (siteName is not null) facts.Add(("Site", siteName));
        if (value.Truthy("switch_name")) facts.Add(("Switch", Text(value["switch_name"])));
        if (value.Truthy("connected")) facts.Add(("Ports identified", value["mapped_ports"] is { } mapped ? Text(mapped) : "0"));
        for (var row = 0; row < facts.Count; row++)
        {
            details.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = facts[row].Item1, FontSize = 13, LineHeight = ConnectionUi.BodyLine, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "muted");
            var data = new TextBlock { Text = facts[row].Item2, FontSize = 13, LineHeight = ConnectionUi.BodyLine, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(label, row);
            Grid.SetRow(data, row);
            Grid.SetColumn(data, 1);
            details.Children.Add(label);
            details.Children.Add(data);
        }
        details.IsVisible = facts.Count > 0;
        disconnect.IsVisible = value.Truthy("configured");
        capabilityHint.Text = "Connect with a controller account to show device names.";
        capabilityHint.IsVisible = value.Truthy("connected") && value.Str("auth_method") == "api_key" && value.Bool("capabilities.client_mapping") == false;
    }

    void Show(JsonNode? value)
    {
        if (!active) return;
        current = value as JsonObject ?? [];
        var presentation = AppShell.ControllerStatus(current, false);
        state.Show(presentation.Label, presentation.Tone);
        ShowDetails(current);
        SyncTarget();
        ShowSites();
        if (!dirty) cleanForm = FormValues();
        if (current.Truthy("error")) Status(ControllerError(current["error"], "Controller data is unavailable. Check the connection and try again."), true);
        else if (current.Truthy("pending_site_choice")) PendingSiteMessage();
        if (current.Truthy("saved_login_error"))
            PreferenceStatus(ControllerError(current["saved_login_error"], "The saved controller login needs attention. Connect your controller again."), true);
        else PreferenceStatus();
        if (SupportsVerification() && current.Str("error.code") is "two_factor_required" or "verification_failed")
        {
            editingConnection = true;
            twoFactor.IsOpen = true;
            verificationRequired = true;
        }
        SetBusy();
    }

    void ClearSecrets(bool includeUsername = false) => Set(() =>
    {
        if (includeUsername) username.Text = "";
        password.Text = "";
        apiKey.Text = "";
        verification.Text = "";
    });

    void Perform(Func<Task<JsonNode?>> action, string label, string actionKind = "status")
    {
        if (!active || pending is not null) return;
        bool focusVerification = false, focusSite = false, focusSummary = false;
        var canceledTarget = actionKind == "cancel" && current.Truthy("pending_site_choice") ? current.At("pending_site_choice") : null;
        var resetCanceledForm = canceledTarget is not null && MatchesForm(canceledTarget);
        Status(label);
        StartPending(async () =>
        {
            try
            {
                var result = await action();
                if (!active) return;
                Show(result);
                focusVerification = SupportsVerification() && result.Str("error.code") is "two_factor_required" or "verification_failed";
                focusSite = (result.Truthy("pending_site_choice") && MatchesForm(result.At("pending_site_choice"))) || result.Str("error.code") == "site_required";
                var canceled = actionKind == "cancel" && !result.Truthy("pending_site_choice");
                if (!result.Truthy("error") && !result.Truthy("pending_site_choice"))
                    Status(actionKind == "status" ? "" : canceled ? "Connection change canceled." : result.Truthy("connected") ? "Controller connected."
                        : actionKind == "disconnect" ? "Controller disconnected." : "Controller not connected.", false, AppShell.ControllerStatus(result, false).Tone);
                var connected = actionKind == "connect" && result.Truthy("connected") && !result.Truthy("error") && !result.Truthy("pending_site_choice") && MatchesForm(result);
                var disconnected = actionKind == "disconnect" && !result.Truthy("configured") && !result.Truthy("error");
                if (connected || disconnected)
                {
                    dirty = false;
                    ClearSecrets();
                    verificationRequired = false;
                    twoFactor.IsOpen = false;
                    challenge = null;
                    trustPanel.IsVisible = false;
                }
                if (connected)
                {
                    Set(() => username.Text = "");
                    editingConnection = false;
                    focusSummary = true;
                }
                if (canceled && resetCanceledForm)
                {
                    dirty = false;
                    ClearSecrets(includeUsername: true);
                    verificationRequired = false;
                    twoFactor.IsOpen = false;
                    certificate = null;
                    challenge = null;
                    trustPanel.IsVisible = false;
                    Set(() => address.Text = result.Str("url") ?? "");
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
            catch (Exception error)
            {
                if (!active) return;
                var code = error switch { ApiException api => api.Code, CodedException coded => coded.Code, _ => null };
                var value = (error as ApiException)?.Details.Str("certificate_sha256");
                if (code is "two_factor_required" or "verification_failed")
                {
                    if (SupportsVerification())
                    {
                        editingConnection = true;
                        twoFactor.IsOpen = true;
                        verificationRequired = true;
                        focusVerification = true;
                        Status(ControllerError(error), true);
                    }
                    else Status(ControllerErrorFor("controller_local_account_required", null), true);
                }
                else if (code is "certificate_untrusted" or "certificate_changed" && kind.Value == "cloud_connector")
                {
                    challenge = null;
                    certificate = null;
                    trustPanel.IsVisible = false;
                    Status("The UniFi cloud certificate could not be verified. Check this computer’s date and trusted certificates, then try again.", true);
                }
                else if (code is "certificate_untrusted" or "certificate_changed" && value is not null && Regex.IsMatch(value, "^[a-fA-F0-9]{64}$"))
                {
                    challenge = new Challenge(value, FormTarget());
                    fingerprint.Text = string.Join(' ', value.ToUpperInvariant().Chunk(4).Select(chunk => new string(chunk)));
                    trustPanel.IsVisible = true;
                    Status(code == "certificate_changed" ? "The controller certificate changed. Verify the new fingerprint before connecting." : "The controller uses a certificate that needs your approval.", true);
                }
                else Status(ControllerError(error), true);
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
        });
        SetBusy();
    }

    void Connect()
    {
        if (!active || pending is not null) return;
        var target = FormTarget();
        var problem = ControllerConnectionProblem(Values(), current);
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
            Status(ControllerErrorFor("site_required", null), true);
            site.Focus();
            return;
        }
        // The browser checks the verification code's required state and pattern before sending.
        var code = verification.Text ?? "";
        if (SupportsVerification() && twoFactor.IsVisible && (verificationRequired && code.Length == 0 || code.Length > 0 && !Regex.IsMatch(code, "^[0-9]{6,8}$")))
        {
            Status(code.Length == 0 ? ControllerMessages["two_factor_required"] : "Enter your account’s current verification code.", true);
            twoFactor.IsOpen = true;
            verification.Focus();
            return;
        }
        var payload = new JsonObject { ["url"] = target.Url!.Trim(), ["controller_kind"] = target.ControllerKind, ["site"] = site.Value.Length > 0 ? site.Value : null };
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
        Set(() => verification.Text = "");
        Perform(() => request("/api/controller/connect", "POST", payload), "Connecting to UniFi Network…", "connect");
    }

    void ResetControllerChallenge()
    {
        certificate = null;
        challenge = null;
        trustPanel.IsVisible = false;
        siteContextKey = "";
        siteField.IsVisible = false;
        siteRequired = false;
        site.SetOptions([("", "Automatic", false)]);
        site.Value = "";
        Set(() => verification.Text = "");
        verificationRequired = false;
        twoFactor.IsOpen = false;
    }

    void FindConsoles()
    {
        if (!active || pending is not null || kind.Value != "cloud_connector" || auth.Value != "api_key" || current.Bool("console_discovery_supported") != true) return;
        if (!PrintableKey(apiKey.Text))
        {
            Status(ControllerMessages["api_key_required"], true);
            apiKey.Focus();
            return;
        }
        discovering = true;
        Status("Finding consoles…");
        StartPending(async () =>
        {
            try
            {
                var result = await request("/api/controller/consoles", "POST", new JsonObject { ["api_key"] = apiKey.Text });
                if (!active) return;
                if (result.At("consoles") is not JsonArray list || list.Count > 1000 || list.Any(item =>
                        item is not JsonObject entry || !Regex.IsMatch(entry.Str("id") ?? "", "^[A-Za-z0-9_-]{1,128}(?::[0-9]{1,20})?$") ||
                        entry.Str("name") is not { } name || name.Trim().Length == 0 || name.Length > 512 || entry.Bool("blocked") is null))
                    throw new CodedException("api_response_incomplete");
                consoles = list.OfType<JsonObject>().Select(item => new ConsoleItem(item.Str("id")!, item.Str("name")!, item.Bool("blocked") == true)).ToList();
                ShowConsoles();
                if (consoles.Count == 0) Status(ControllerMessages["no_consoles"], true);
                else if (consoles.All(item => item.Blocked)) Status("These consoles are blocked in UniFi. Check console access in Site Manager.", true);
                else Status("Choose the console to connect.");
            }
            catch (Exception error)
            {
                if (active) Status(ControllerError(error), true);
            }
            finally
            {
                pending = null;
                discovering = false;
                SetBusy();
                if (active) consoleId.Focus();
            }
        });
        SetBusy();
    }

    void ApplyPreference()
    {
        var preference = ControllerReconnectControls(current, reconnectDraft);
        if (!active || pending is not null || !preference.CanApply) return;
        var enabled = preference.Checked;
        savingPreference = true;
        PreferenceStatus("Saving controller preference…");
        StartPending(async () =>
        {
            try
            {
                var result = await request("/api/controller/preferences", "PUT", new JsonObject { ["auto_reconnect"] = enabled });
                if (!active) return;
                Show(result);
                if (!ControllerReconnectConfirmed(result, enabled))
                {
                    PreferenceStatus(ControllerError(result.At("saved_login_error"), "The setting could not be confirmed. Select Refresh controller before trying again."), true);
                    return;
                }
                reconnectDraft = null;
                PreferenceStatus(enabled ? "Automatic reconnect enabled. Controller login saved." : "Automatic reconnect disabled. Saved controller login removed.");
                try { await onChanged(result); }
                catch (Exception)
                {
                    if (active) Status("The setting was saved. Select Refresh controller to update its details.", true);
                }
            }
            catch (Exception error)
            {
                if (active) PreferenceStatus(ControllerError(error, "The controller setting could not be saved. Try Save reconnect preference again."), true);
            }
            finally
            {
                pending = null;
                savingPreference = false;
                SetBusy();
            }
        });
        SetBusy();
    }
}
