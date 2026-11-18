using System.Globalization;
using System.Text;
using Avalonia.Controls;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Shell;
using EtherlightingEvolved.Native.Theme;
using EtherlightingEvolved.Native.Ui;
using UiKit = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Connect;

/// <summary>
/// Pairs this computer with EE at a LAN address, as the browser workspace's pairing screen (renderLanPairing) does: the
/// code shown on the computer running EE and a name for this computer. A successful pairing gives the workspace its
/// device key and saves the key for this address.
/// </summary>
public static class LanPairingDialog
{
    public const string RevokedIntro = "This computer is not paired. Enter a new pairing code from the computer running EE to continue.";
    public const string NameHelp = "This name appears in the list of paired devices on the computer running EE.";
    public const string Statement = "This connection uses plain HTTP and is not encrypted. Anyone who can watch or change traffic on your network can read workspace data and this computer’s access key, and can use the key to control EE. EE does not send switch or UniFi passwords over it.";
    public const string Warning = "EE on another device never asks for switch or UniFi passwords. If it does, do not enter them.";
    const string KeyUnsaved = "This computer is paired, but its access key could not be saved, so it needs a new pairing code after EE restarts. Check the application data folder.";
    const string KeysNewer = "This computer is paired, but its access key was not saved, because a newer version of EE saved the access keys on this computer. It needs a new pairing code after EE restarts. Update EE to keep the key.";

    static readonly Dictionary<string, string> Refusals = new(StringComparer.Ordinal)
    {
        ["lan_pairing_failed"] = "The pairing code is wrong or has expired. Check the code on the computer running EE, or choose Pair a device there to show a new one.",
        ["lan_pairing_save_failed"] = "The device could not be saved on the computer running EE. Ask for a new pairing code there and try again.",
        ["invalid"] = "Enter the 6-digit pairing code and a name of up to 40 characters for this device.",
    };

    public static string Intro(Uri address) =>
        $"Enter the pairing code shown on the computer running EE at {address.Authority}. On that computer, open Settings, go to LAN access and choose Pair a device.";

    /// <summary>The six digits of a code as typed, which may group them with spaces or hyphens, or null (pairingCode).</summary>
    public static string? PairingCode(string? text)
    {
        var digits = (text ?? "").Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        return digits.Length == 6 && digits.All(char.IsAsciiDigit) ? digits : null;
    }

    /// <summary>A device name, trimmed: 1 to 40 characters without control, format or separator characters, or null (deviceName).</summary>
    public static string? DeviceName(string? text)
    {
        var name = (text ?? "").Trim();
        var runes = name.EnumerateRunes().ToList();
        return runes.Count is >= 1 and <= 40 && !runes.Any(rune => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) ? name : null;
    }

    /// <summary>This computer's name, cut to 40 characters, as the name to offer.</summary>
    public static string DefaultName => new(Environment.MachineName.EnumerateRunes().Take(40).SelectMany(rune => rune.ToString()).ToArray());

    /// <summary>Shows the dialog over the shell. True once this computer is paired; false when the user cancels.</summary>
    public static async Task<bool> Show(AppShell shell, bool revoked)
    {
        var api = shell.Workspace.Api;
        var code = UiKit.Input(null, null, null, 7);
        var name = UiKit.Input(DefaultName, "For example, Kitchen iPad");
        var error = UiKit.Text("", "error", wrap: true);
        var cancel = UiKit.Button("Cancel");
        var pair = UiKit.Button("Pair", "primary");
        var content = UiKit.Column(14,
            UiKit.H2("Pair with EE"),
            UiKit.HelpText(revoked ? RevokedIntro : Intro(api.BaseUri)),
            UiKit.Field("Pairing code", code),
            UiKit.Field("Device name", name, NameHelp),
            new Border { BorderThickness = new Avalonia.Thickness(3, 0, 0, 0), Padding = new Avalonia.Thickness(10, 8), Child = UiKit.HelpText(Statement) }
                .Bind(Border.BorderBrushProperty, "danger"),
            UiKit.HelpText(Warning),
            error,
            UiKit.Row(8, cancel, pair));
        var dialog = shell.Overlay.Show(content, width: 460);
        var sending = false;
        string? unsaved = null;
        cancel.Click += (_, _) => { if (!sending) dialog.Close("cancel"); };
        dialog.Canceling = () => !sending;
        pair.Click += async (_, _) =>
        {
            if (sending) return;
            if (PairingCode(code.Text) is not { } digits)
            {
                error.Text = "Enter the 6-digit code.";
                code.Focus();
                return;
            }
            if (DeviceName(name.Text) is not { } label)
            {
                error.Text = "Enter a name for this device, up to 40 characters.";
                name.Focus();
                return;
            }
            sending = true;
            pair.Content = "Pairing…";
            pair.IsEnabled = cancel.IsEnabled = false;
            error.Text = "";
            try
            {
                var key = await api.PairAsync(digits, label);
                api.DeviceKey = key;
                try { DeviceKeys.Set(api.BaseUri, key); }
                catch (NewerDeviceKeysException) { unsaved = KeysNewer; }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { unsaved = KeyUnsaved; }
                sending = false;
                dialog.Close("paired");
            }
            catch (ApiException failure)
            {
                error.Text = failure.Code is { } refusal && Refusals.TryGetValue(refusal, out var message) ? message : Connections.LanUnreachable(OperatingSystem.IsMacOS());
                code.Text = "";
                code.Focus();
            }
            finally
            {
                sending = false;
                pair.Content = "Pair";
                pair.IsEnabled = cancel.IsEnabled = true;
            }
        };
        var paired = await dialog.Result == "paired";
        if (unsaved is not null) shell.Notify(unsaved, true);
        return paired;
    }
}
