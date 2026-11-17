using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Pages.Connect;
using EtherlightingEvolved.Native.Ui;

namespace EtherlightingEvolved.Native.Check.Checks;

/// <summary>The Tailscale private access panel on a computer without Tailscale, where private access stays optional.</summary>
internal static class PrivateAccessPanelChecks
{
    public static void Run(Action<bool, string> check)
    {
        // The live gateway's answer when private access was never turned on: EE has not run Tailscale, so nothing is
        // available and nothing failed.
        var off = new JsonObject
        {
            ["available"] = false, ["enabled"] = false, ["ready"] = false, ["url"] = null, ["error"] = null, ["setup_action"] = null,
            ["action_url"] = null, ["audience"] = "owner", ["https_port"] = 8444, ["can_change_port"] = true, ["can_configure"] = true,
            ["can_close_workspace"] = true, ["admin_required"] = false, ["local"] = true,
        };
        var install = off.DeepClone().AsObject();
        install["setup_action"] = "install";
        install["action_url"] = "https://tailscale.com/download";
        install["error"] = new JsonObject { ["code"] = "tailscale_not_installed" };
        var requests = new List<string>();
        var panel = new PrivateAccessPanel((path, method, body) =>
        {
            requests.Add($"{method} {path} {body?.ToJsonString()}".TrimEnd());
            return Task.FromResult<JsonNode?>((method == "PUT" && body.Bool("enabled") == true ? install : off).DeepClone());
        });
        var window = new Window { Width = 700, Height = 800, Content = new ScrollViewer { Content = panel } };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            check(State(panel) == "Private access is off" && Toggle(panel) is { IsEffectivelyVisible: true, IsEnabled: true, IsChecked: false }
                  && Find(panel, "Apply setting") is { IsEffectivelyVisible: true, IsEnabled: false } && !Shown(panel, "Install Tailscale")
                  && requests.SequenceEqual(["GET /api/private-access"]),
                "without Tailscale, private access shows as off with Use Tailscale and no setup step");

            Toggle(panel).IsChecked = true;
            Click(panel, "Apply setting");
            check(requests.Last() == """PUT /api/private-access {"enabled":true}""" && State(panel) == "Set up Tailscale" && Shown(panel, "Install Tailscale")
                  && Toggle(panel) is { IsEffectivelyVisible: true, IsEnabled: true, IsChecked: true } && Find(panel, "Apply setting") is { IsEffectivelyVisible: true, IsEnabled: true },
                "turning private access on without Tailscale shows the install step and keeps Use Tailscale and Apply");

            Toggle(panel).IsChecked = false;
            var cancelable = !panel.HasChanges && Find(panel, "Apply setting")?.IsEnabled == true;
            Click(panel, "Apply setting");
            check(cancelable && requests.Last() == """PUT /api/private-access {"enabled":false}""" && requests.Count == 3 && State(panel) == "Private access is off"
                  && !Shown(panel, "Install Tailscale") && Find(panel, "Apply setting")?.IsEnabled == false,
                "Apply with Use Tailscale cleared ends the Tailscale setup");
        }
        finally
        {
            panel.Close();
            window.Close();
        }
    }

    static string State(Control panel) => panel.GetLogicalDescendants().OfType<StateLine>().First().Label;

    static CheckBox Toggle(Control panel) => panel.GetLogicalDescendants().OfType<CheckBox>().First();

    static Button? Find(Control root, string label) => root.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => Equals(button.Content, label));

    static bool Shown(Control root, string label) => Find(root, label) is { IsEffectivelyVisible: true };

    static void Click(Control root, string label)
    {
        Find(root, label)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }
}
