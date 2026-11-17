using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Theme;
using EtherlightingEvolved.Native.Ui;
using static EtherlightingEvolved.Native.Pages.Connections;
using UiKit = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Connect;

/// <summary>
/// LAN access, a port of renderLanAccess (preview/connections.mjs). On the computer running EE it reads
/// /api/lan-access, turns LAN access on and off, changes its port, shows the addresses, issues pairing codes and lists
/// the paired devices. A window on another device (remote) shows only where to change it, and a LAN window (lan) also
/// says that its own connection is not encrypted.
/// </summary>
public sealed class LanAccessPanel : UserControl
{
    public const string Statement = "LAN access uses plain HTTP, so traffic between your devices and this PC is not encrypted. Anyone who can watch or change traffic on your network can read workspace data, such as the switch address and SSH username, port names, profiles and schedules, and can copy a device’s access key. With that key they can do anything a paired device can, including changing lighting and fans and handing lighting back to UniFi, until you remove that device or turn off LAN access. EE does not send switch or UniFi passwords over LAN access, because switch login and UniFi Network setup work only on this PC. Use Tailscale for an encrypted connection.";
    public const string Scope = "Paired devices can use lighting, effects, automation, profiles, schedules and port names, can control fans in the desktop app, and can reconnect to the saved switch. Switch login, UniFi Network setup and these settings are available only on this PC.";
    public const string Firewall = "Before you turn on LAN access, make sure Windows lists this network as private. If Windows then asks about Node.js JavaScript Runtime, allow it on private networks only. EE’s service runs on Node.js. EE does not check the firewall, so if a device cannot open the address, check the firewall settings on this PC.";
    public const string LanWindow = "This device uses LAN access. Its connection to the computer running EE is not encrypted.";
    public const string HostOnly = "Change this setting in EE on the host computer.";
    const int DefaultPort = 4280;
    static readonly Regex DeviceId = new("^[0-9a-f]{16}\\z");
    static readonly Regex AddressUrl = new("^http://((?:\\d{1,3}\\.){3}\\d{1,3}):\\d{4,5}\\z");
    static readonly Dictionary<string, string> Ended = new(StringComparer.Ordinal)
    {
        ["expired"] = "The pairing code expired. Choose Pair a device to show a new one.",
        ["attempts"] = "The pairing code ended after 5 wrong attempts. If no one at your devices entered them, another device on your network may be trying to pair. Choose Pair a device to show a new one.",
        ["failed"] = "The device could not be saved. Check the local data folder, then choose Pair a device to show a new code.",
        ["used"] = "The pairing code was used.",
    };

    readonly Func<string, string, JsonNode?, Task<JsonNode?>> request;
    readonly Func<string, string, string, Task<bool>> confirm;
    readonly CancellationTokenRegistration abortRegistration;
    readonly StackPanel section = new() { Spacing = 12, Classes = { "lan-access", "automation-section" } };
    readonly Button refresh = UiKit.Button("Refresh LAN access", "quiet");
    readonly StateLine state = new("Checking LAN access…", "waiting");
    readonly CheckRow label = new("Allow devices on my network");
    readonly CheckBox toggle;
    readonly TextBlock readOnly = UiKit.HelpText(LanAdminRequired);
    readonly Button apply = UiKit.Button("Apply setting", "primary");
    readonly FlowRow actions = new(10) { Classes = { "connection-actions" } };
    readonly TextBlock firewall = UiKit.HelpText(Firewall);
    readonly Disclosure advanced = new("Connection options");
    readonly TextBox port = UiKit.Input();
    readonly Button resetPort = UiKit.Button("Reset port", "quiet");
    readonly StackPanel addresses = new() { Spacing = 8, IsVisible = false };
    readonly StackPanel addressList = new() { Spacing = 8 };
    readonly StackPanel pairing = new() { Spacing = 8, IsVisible = false };
    readonly Button pair = UiKit.Button("Pair a device");
    readonly StackPanel codeBlock = new() { Spacing = 6, IsVisible = false };
    readonly SelectableTextBlock codeText = new() { FontSize = 28, FontWeight = Avalonia.Media.FontWeight.SemiBold, LetterSpacing = 2 };
    readonly TextBlock codeHelp = UiKit.HelpText();
    readonly Button newCode = UiKit.Button("New code", "quiet");
    readonly StackPanel devicesBlock = new() { Spacing = 8, IsVisible = false };
    readonly StackPanel deviceList = new() { Spacing = 8 };
    readonly TextBlock noDevices = UiKit.HelpText("No devices are paired.");
    readonly TextBlock limit = UiKit.HelpText(LanDeviceLimit);
    readonly Notice message = new(connection: true);
    readonly DispatcherTimer countdown, poll;
    bool active, updating;
    JsonObject? current;
    bool? draft;
    string? portDraft, pending, announced, addressKey, deviceKey;
    HashSet<string>? knownDevices;
    Task completion = Task.CompletedTask;

    /// <param name="confirm">Asks a question with a title, a message and the accept action's label (confirmAction).</param>
    public LanAccessPanel(Func<string, string, JsonNode?, Task<JsonNode?>> request, Func<string, string, string, Task<bool>> confirm, bool remote = false,
        bool lan = false, CancellationToken signal = default)
    {
        this.request = request;
        this.confirm = confirm;
        active = !signal.IsCancellationRequested;
        toggle = label.Box;
        countdown = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RenderCode());
        poll = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (_, _) => await Poll());
        var title = ConnectionUi.Heading("LAN access");
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 18, Classes = { "connection-heading" } };
        title.VerticalAlignment = VerticalAlignment.Top;
        header.Children.Add(title);
        Content = section;
        section.Children.Add(header);
        if (remote)
        {
            active = false;
            if (lan) section.Children.Add(Warning(LanWindow));
            section.Children.Add(UiKit.HelpText(HostOnly));
            return;
        }
        refresh.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(refresh, 1);
        header.Children.Add(refresh);
        readOnly.IsVisible = false;
        actions.Children.Add(apply);
        var portField = UiKit.Field("LAN port", port);
        portField.Margin = new Thickness(0, 14, 0, 0);
        portField.Width = 150;
        portField.HorizontalAlignment = HorizontalAlignment.Left;
        resetPort.IsVisible = false;
        resetPort.HorizontalAlignment = HorizontalAlignment.Left;
        advanced.Body.Children.Add(portField);
        advanced.Body.Children.Add(resetPort);
        advanced.Body.Children.Add(UiKit.HelpText("Use a free port between 1024 and 65535. Turn off LAN access before changing it. Do not forward this port on your router."));
        addresses.Children.Add(UiKit.HelpText("Address for other devices"));
        addresses.Children.Add(addressList);
        codeBlock.Children.Add(UiKit.HelpText("Pairing code"));
        codeBlock.Children.Add(codeText);
        codeBlock.Children.Add(codeHelp);
        codeBlock.Children.Add(newCode);
        pairing.Children.Add(pair);
        pairing.Children.Add(codeBlock);
        devicesBlock.Children.Add(UiKit.H3("Paired devices"));
        devicesBlock.Children.Add(deviceList);
        devicesBlock.Children.Add(noDevices);
        devicesBlock.Children.Add(limit);
        foreach (var child in new Control[]
                 {
                     state, UiKit.HelpText("Open EE on phones and other computers on your network without Tailscale. Each device needs a pairing code from this PC."),
                     Warning(Statement), UiKit.HelpText(Scope), label, readOnly, actions, firewall, advanced, addresses, pairing, devicesBlock, message,
                 })
            section.Children.Add(child);
        if (!active) return;

        toggle.IsCheckedChanged += (_, _) =>
        {
            if (updating || !active || pending is not null || !CanConfigure()) return;
            draft = toggle.IsChecked == true == Enabled() ? null : toggle.IsChecked == true;
            Status();
            Render();
        };
        port.TextChanging += (_, _) =>
        {
            if (updating || !active || pending is not null || !CanChangePort()) return;
            var text = port.Text ?? "";
            portDraft = text == CurrentPort().ToString(CultureInfo.InvariantCulture) ? null : text;
            Status(ValidPort() ? "" : "Enter a whole-number port between 1024 and 65535.", !ValidPort());
            Render();
        };
        resetPort.Click += (_, _) =>
        {
            if (!active || pending is not null || !CanConfigure()) return;
            portDraft = null;
            Status();
            Render();
            (port.IsEnabled ? (Control)port : refresh).Focus();
        };
        apply.Click += (_, _) => _ = Start("apply");
        refresh.Click += (_, _) => _ = Start("refresh");
        pair.Click += (_, _) => _ = Start("code");
        newCode.Click += (_, _) => _ = Start("code");
        abortRegistration = signal.Register(Close);
        Render();
        _ = Start("refresh");
    }

    /// <summary>The unencrypted-traffic statement, set off by a rule in the danger color.</summary>
    static Border Warning(string text) =>
        new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 8), Child = UiKit.HelpText(text), Classes = { "lan-access-warning" } }
            .Bind(Border.BorderBrushProperty, "danger");

    /// <summary>Ends the panel (the renderer's cleanup): its timers stop and later results are ignored.</summary>
    public void Close()
    {
        active = false;
        countdown.Stop();
        poll.Stop();
        abortRegistration.Dispose();
    }

    public bool HasChanges => draft is not null || portDraft is not null;

    public async Task<bool> CanLeave()
    {
        await completion;
        return true;
    }

    void Status(string text = "", bool error = false) => message.Set(text, error);

    bool CanConfigure() => current.Bool("can_configure") == true;

    bool Enabled() => current.Bool("enabled") == true;

    bool Ready() => Enabled() && current.Bool("ready") == true;

    bool CanRetry() => Enabled() && current.Bool("ready") != true;

    // A turn-off that could not be saved is applied again with the setting left off.
    bool RevokeUnsaved() => current.Str("error.code") == "lan_access_revoke_unsaved";

    List<JsonObject> Devices() => current.Objects("devices").Where(device => device.Str("id") is { } id && DeviceId.IsMatch(id) && device.Str("name") is not null).ToList();

    int DeviceLimit() => current.Int("max_devices") ?? 16;

    string? ActiveCode() => current.Str("pairing.code") is { Length: 6 } code && code.All(char.IsAsciiDigit) ? code : null;

    int CurrentPort() => current.Int("port") is { } value and >= 1024 and <= 65535 ? value : DefaultPort;

    bool CanChangePort() => CanConfigure() && current.Bool("can_change_port") == true && !Enabled() && current.Bool("ready") != true;

    bool ValidPort()
    {
        var text = portDraft ?? CurrentPort().ToString(CultureInfo.InvariantCulture);
        var number = JsNumber(text);
        return Regex.IsMatch(text, @"^\d+$") && number >= 1024 && number <= 65535;
    }

    bool HasPortChange() => portDraft is not null && JsNumber(portDraft) != CurrentPort();

    bool Unchanged(bool chosen, bool portChange) => chosen == Enabled() && !portChange && !(chosen && CanRetry()) && !(!chosen && RevokeUnsaved());

    static bool PrivateAddress(string? url)
    {
        if (url is null || AddressUrl.Match(url) is not { Success: true } match) return false;
        var parts = match.Groups[1].Value.Split('.');
        if (parts.Any(part => part.Length > 1 && part[0] == '0' || int.Parse(part, CultureInfo.InvariantCulture) > 255)) return false;
        var bytes = parts.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        return bytes is [10, _, _, _] or [172, >= 16 and <= 31, _, _] or [192, 168, _, _];
    }

    static string When(string? value, string format) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            ? time.ToLocalTime().ToString(format, CultureInfo.InvariantCulture) : "";

    void RenderCode()
    {
        if (ActiveCode() is not { } code)
        {
            codeText.Text = "";
            codeHelp.Text = "";
            return;
        }
        var left = DateTime.TryParse(current.Str("pairing.expires_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expires)
            ? Math.Max(0, (int)Math.Ceiling((expires.ToUniversalTime() - DateTime.UtcNow).TotalSeconds)) : 0;
        codeText.Text = $"{code[..3]} {code[3..]}";
        codeHelp.Text = $"Open the address above on the other device and enter this code. It works once and expires in {left / 60}:{left % 60:00}.";
    }

    // The lists are rebuilt only when they change, so a periodic refresh keeps focus on their buttons.
    void RenderAddresses(List<(string Url, string Interface)> list)
    {
        var key = string.Join('\n', list.Select(item => item.Url + " " + item.Interface));
        if (key == addressKey) return;
        addressKey = key;
        addressList.Children.Clear();
        foreach (var (url, name) in list)
        {
            var copy = UiKit.Button("Copy address", "quiet");
            copy.Click += async (_, _) =>
            {
                if (!active) return;
                try
                {
                    if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) throw new InvalidOperationException("clipboard_unavailable");
                    await clipboard.SetTextAsync(url);
                    if (active) Status("LAN address copied.");
                }
                catch (Exception)
                {
                    if (active) Status("Select the address and copy it.");
                }
            };
            addressList.Children.Add(new FlowRow(10, new SelectableTextBlock { Text = $"{url} ({name})", VerticalAlignment = VerticalAlignment.Center }, copy));
        }
    }

    void RenderDevices(List<JsonObject> list)
    {
        var key = string.Join('\n', list.Select(device => device.ToJsonString()));
        if (key == deviceKey) return;
        deviceKey = key;
        deviceList.Children.Clear();
        foreach (var device in list)
        {
            var details = new StackPanel { Spacing = 2 };
            details.Children.Add(new TextBlock { Text = device.Str("name"), FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            details.Children.Add(UiKit.HelpText($"{(device.Str("client") == "app" ? "Desktop app" : "Browser")} · Paired {When(device.Str("paired_at"), "MMM d, yyyy")} from {device.Str("paired_from")}"));
            if (device.Str("last_used_at") is { } used)
                details.Children.Add(UiKit.HelpText($"Last used from {device.Str("last_address")} at {When(used, "h:mm tt")}"));
            var remove = UiKit.Button("Remove", "quiet");
            var id = device.Str("id")!;
            remove.Click += (_, _) => _ = Start("remove", id);
            remove.VerticalAlignment = VerticalAlignment.Center;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
            Grid.SetColumn(remove, 1);
            row.Children.Add(details);
            row.Children.Add(remove);
            deviceList.Children.Add(row);
        }
    }

    void Render()
    {
        if (!active) return;
        updating = true;
        try
        {
            var on = Enabled();
            var chosen = draft ?? on;
            var busy = pending is not null;
            var configurable = CanConfigure();
            var available = current.Bool("available") == true;
            toggle.IsChecked = chosen;
            toggle.IsEnabled = !busy && configurable;
            var portChange = HasPortChange();
            apply.IsEnabled = !(busy || !configurable || !ValidPort() || portChange && !CanChangePort() || Unchanged(chosen, portChange));
            apply.Content = pending == "apply" ? "Applying…" : chosen == on && CanRetry() ? "Retry LAN access" : "Apply setting";
            refresh.IsEnabled = !busy;
            label.IsVisible = actions.IsVisible = firewall.IsVisible = available;
            readOnly.IsVisible = available && !configurable;
            advanced.IsVisible = configurable;
            var portText = portDraft ?? CurrentPort().ToString(CultureInfo.InvariantCulture);
            if (port.Text != portText) port.Text = portText;
            port.IsEnabled = !busy && CanChangePort();
            resetPort.IsVisible = portDraft is not null;
            resetPort.IsEnabled = !busy;
            var shown = Ready()
                ? current.Objects("addresses").Where(item => PrivateAddress(item.Str("url")) && item.Str("interface") is not null)
                    .Select(item => (item.Str("url")!, item.Str("interface")!)).ToList()
                : [];
            addresses.IsVisible = shown.Count > 0;
            RenderAddresses(shown);
            var code = ActiveCode();
            var list = Devices();
            var full = list.Count >= DeviceLimit();
            pairing.IsVisible = configurable && Ready();
            pair.IsVisible = code is null;
            pair.IsEnabled = !busy && !full;
            codeBlock.IsVisible = code is not null;
            newCode.IsEnabled = !busy && !full;
            RenderCode();
            devicesBlock.IsVisible = configurable && (Ready() || list.Count > 0);
            RenderDevices(list);
            noDevices.IsVisible = list.Count == 0;
            limit.IsVisible = full;
            if (current is null) state.Set(busy ? "Checking LAN access…" : LanAccessFallback, busy ? "waiting" : "is-disconnected");
            else if (!available) state.Set("LAN access is not available in the demo.", "is-disconnected");
            else if (Ready())
                state.Set(shown.Count > 0 ? "LAN access is on" : "LAN access is on, but this PC has no private network address. Connect it to your network, then refresh.",
                    shown.Count > 0 ? "online" : "waiting");
            else if (current.Truthy("error")) state.Set(LanAccessError(current["error"]), "is-disconnected");
            else state.Set(on ? LanAccessFallback : "LAN access is off", on ? "waiting" : "is-disconnected");
        }
        finally { updating = false; }
    }

    // While a code is shown, the countdown runs every second and the status is read every 2 seconds to see it used.
    void SyncTimers()
    {
        if (!active || ActiveCode() is null || !CanConfigure())
        {
            countdown.Stop();
            poll.Stop();
            return;
        }
        countdown.Start();
        poll.Start();
    }

    async Task Poll()
    {
        if (!active || pending is not null) return;
        try
        {
            var latest = await request("/api/lan-access", "GET", null);
            if (active && pending is null) Update(latest);
        }
        catch (Exception error)
        {
            if (!active) return;
            countdown.Stop();
            poll.Stop();
            Status(LanAccessError(error), true);
        }
    }

    // The reason a code ended is shown once; a code used by a device that appeared since it was shown names that device.
    void AnnouncePairing()
    {
        if (current.Str("pairing.ended") is not { } ended || !Ended.TryGetValue(ended, out var text)) return;
        var key = current.Str("pairing.expires_at") + "|" + ended;
        if (key == announced) return;
        announced = key;
        var added = ended == "used" && knownDevices is { } known ? Devices().FirstOrDefault(device => !known.Contains(device.Str("id")!)) : null;
        Status(added is not null ? $"Paired {added.Str("name")}." : text, ended != "used");
    }

    /// <summary>Shows a LAN access status read elsewhere (cleanup.update).</summary>
    public void Update(JsonNode? value)
    {
        if (!active) return;
        if (value is not JsonObject obj || obj["enabled"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False) ||
            obj["available"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False) ||
            obj["ready"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException("invalid_lan_status");
        current = obj;
        if (draft == Enabled()) draft = null;
        if (portDraft is not null && JsNumber(portDraft) == CurrentPort()) portDraft = null;
        AnnouncePairing();
        Render();
        SyncTimers();
    }

    Task Start(string operation, string? deviceId = null)
    {
        if (pending is not null) return completion;
        completion = Perform(operation, deviceId);
        return completion;
    }

    async Task Perform(string operation, string? deviceId)
    {
        if (!active || pending is not null) return;
        var saving = operation == "apply";
        var wanted = draft ?? Enabled();
        var portChange = HasPortChange();
        var wantedPort = (int)JsNumber(portDraft ?? CurrentPort().ToString(CultureInfo.InvariantCulture));
        var wasEnabled = Enabled();
        var device = operation == "remove" ? Devices().FirstOrDefault(item => item.Str("id") == deviceId) : null;
        if (saving && (!CanConfigure() || !ValidPort() || portChange && !CanChangePort() || Unchanged(wanted, portChange))) return;
        if (operation is "code" or "remove" && !CanConfigure() || operation == "remove" && device is null) return;
        pending = operation;
        Render();
        Status();
        try
        {
            if (saving && !wanted && wasEnabled && Devices().Count > 0 && !await confirm("Turn off LAN access?",
                    "All paired devices are removed and disconnected. Each one needs a new pairing code after you turn LAN access on again.", "Turn off"))
                return;
            if (!active) return;
            if (operation == "code") knownDevices = Devices().Select(item => item.Str("id")!).ToHashSet(StringComparer.Ordinal);
            var payload = new JsonObject { ["enabled"] = wanted };
            if (portChange) payload["port"] = wantedPort;
            var result = saving ? await request("/api/lan-access", "PUT", payload)
                : operation == "code" ? await request("/api/lan-access/code", "POST", null)
                : operation == "remove" ? await request($"/api/lan-access/devices/{device!.Str("id")}", "DELETE", null)
                : await request("/api/lan-access", "GET", null);
            if (!active) return;
            Update(result);
            if (result.Truthy("error") && (saving || operation == "remove")) Status(LanAccessError(result.At("error")), true);
            else if (saving && (result.Bool("enabled") != wanted || portChange && result.Int("port") != wantedPort))
                Status("The setting could not be confirmed. Refresh status before trying again.", true);
            else if (saving) Status(wanted ? "LAN access enabled." : portChange && !wasEnabled ? "LAN port saved." : "LAN access disabled.");
            else if (operation == "remove") Status($"Removed {device!.Str("name")}. It needs a new pairing code to connect again.");
        }
        catch (Exception error)
        {
            if (active)
            {
                Status(LanAccessError(error), true);
                // The gateway was started by another program, or restarted: show the read-only status.
                if (error is ApiException { Code: "admin_required" })
                {
                    try
                    {
                        var latest = await request("/api/lan-access", "GET", null);
                        if (active) Update(latest);
                    }
                    catch (Exception) { }
                }
                if (current is null) state.Set(LanAccessFallback, "is-disconnected");
            }
        }
        finally
        {
            pending = null;
            if (active)
            {
                Render();
                if (saving) (toggle.IsEnabled ? (Control)toggle : refresh).Focus();
            }
        }
    }
}
