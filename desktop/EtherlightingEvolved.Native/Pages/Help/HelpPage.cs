using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EtherlightingEvolved.Native.Pages.Settings;
using EtherlightingEvolved.Native.Shell;
using EtherlightingEvolved.Native.Theme;

namespace EtherlightingEvolved.Native.Pages.Help;

static class Registration
{
    [ModuleInitializer]
    internal static void Register() => PageRegistry.Register("Help", shell => new HelpPage(shell));
}

/// <summary>
/// Port of renderHelp (preview/secondary.mjs): the Help page's chapters, each a card that opens to show its steps.
/// It reads only the port layout of the current state, so it also works over the connection view before a switch
/// is signed in (AppShell.ShowLoginHelp).
/// </summary>
public sealed class HelpPage : Page
{
    public HelpPage(AppShell shell) : base(shell)
    {
        var (copper, sfp) = PortTopology.LightingPorts(Workspace.State);
        var sfpScope = sfp.Count > 0 ? $"SFP {string.Join(", ", sfp)}" : "the SFP indicators";
        var chapters = new List<(string Title, string[] Paragraphs)>
        {
            ("Change a port’s lighting", [
                "Open Lighting and select a port. All ports selects every Ethernet and SFP port. Ethernet and SFP select those ports together.",
                "Turn on Select multiple to add or remove ports with a click or tap. Shift-click selects a range; Ctrl-click on Windows or Command-click on Mac adds individual ports.",
                "In Port controls, choose a fixed color or an effect, then adjust its settings. Apply changes sends the changes; Discard returns to your applied settings.",
                "Open Workspace for Find port, Saved selections, History, groups, profiles, and Undo last apply. Find port searches port numbers, port names, and associated clients.",
                "Saved selections keeps named sets of ports. Recall one to select those ports, then choose the lighting you want to preview.",
                "Copy appearance keeps a selected port’s settings. Select compatible ports and choose Paste appearance to preview the same look before applying it.",
            ]),
            ("Preview and apply changes", [
                "Editing colors, brightness, or effect settings creates a preview. The Apply bar identifies changes waiting to be sent.",
                "The port map previews those changes. Show applied lets you compare them with the applied lighting; Show preview returns to your edits.",
                "Lighting previews stay available while you browse. Return to Lighting to continue editing, or save the preview as a profile. Apply changes sends it to the switch.",
                "Undo last apply restores the setup from before the most recent lighting or profile apply. It is available in Lighting and Profiles while that setup is still current.",
                "Refreshing the page restores your selection and pending lighting draft. Review the preview before applying it.",
                "History in Workspace lists earlier lighting setups for this switch. Review an entry and choose Restore to apply it.",
                "When another device changes the lighting, Review lighting changes shows both edits. Reapply compatible edits or choose which settings to keep.",
            ]),
            ("Brightness and colors", [
                "Default brightness sets the brightness for effects and SFP lighting.",
                "Fixed Ethernet colors and rules can set their own brightness instead of following the default. Adjust an SFP color’s brightness beside its color picker.",
                "Each effect color’s brightness adjusts that color within the effect.",
                "A fixed port color stays on that port. Follow group effect restores the port’s effect. Color swatches open the color picker; enter a hex value for an exact color.",
                "Save favorite colors in the picker for quick access. Named palettes keep a set of colors together for reuse.",
            ]),
            ("Choose an effect", [
                "Port effects run on the selected Ethernet and SFP ports. When only Ethernet ports are selected, turn on Include SFP lights to add the switch’s SFP indicators.",
                "Favorite effects are easy to find again. Recent effects keep the effects you have chosen close at hand.",
                "In Port controls, open Saved effect settings to name the current effect and its settings. Choose a saved configuration to preview it on the selected ports.",
                "Speed controls how quickly an effect moves or completes a cycle. Color controls set its primary and secondary colors.",
                "Random color options vary the colors you enable. Leave the primary color fixed and randomize the background for a multicolor Marquee.",
                "PoE Usage blends between Low power and High power using each port’s delivered watts. Logarithmic scaling makes low-power differences visible; Fade speed controls how quickly colors follow a change.",
            ]),
            ("Understand the port map", [
                "The illuminated socket shows the lighting color. The link-speed legend explains the dot beneath each port.",
                "Port details shows current traffic, link speed, PoE power, lighting source, and recent readings. Choose Identify for 10 seconds to locate a physical port, or Stop to finish early.",
                "The uplink symbol identifies the port connecting to the rest of your network. PoE symbols and watts describe power supplied by a port.",
                "Choose SFP lighting in Effects for the indicator’s own color and animation controls. Link status lets the switch manage the indicator.",
            ]),
            ("Sync SFP lights", [
                $"Select an SFP port, then choose a sync mode in Port controls. Together shares one effect, color palette, and timing across {sfpScope}.",
                .. copper.Count > 0
                    ? new[] { $"Follow Ethernet port tracks a chosen Ethernet port’s color and brightness across {sfpScope}. Targeted SFP automations can override it." }
                    : [],
                "Choose Return to SFP lighting to remove a selected indicator from its port effect and restore its saved SFP settings. Apply changes confirms the preview.",
            ]),
            ("Create an automation", [
                "Create a rule, choose when it should match, select the ports, then choose what they should show.",
                "Use All ports, Ethernet, or SFP to select targets quickly. Rules are checked from top to bottom; the first matching rule wins.",
                "Apply rule saves its conditions and appearance. Apply priority saves changes to the rule order. Time rules follow the EE computer’s local time.",
                "For an SFP alert, select the Anomaly template, select the SFP targets, choose the alert color, and apply the rule.",
                "Test rules evaluates your edits and priority for a selected port and time. Review the matching conditions and lighting source before applying.",
                "Open Schedules to choose weekdays, a time on the EE computer, and a profile, brightness, handoff, or resume action. Review the next run and last result, then save the schedule.",
            ]),
            ("Save and reuse profiles", [
                "Save current as profile captures your applied lighting. Save preview as profile keeps unapplied changes so you can apply the complete setup later.",
                "Select a saved profile to review it, then choose Apply profile. The Applied label identifies a profile matching the current setup.",
                "Replace with applied setup updates the selected saved profile after confirmation. Rename and Duplicate organize profiles without changing your lighting.",
            ]),
            ("Control the switch fans", [
                "Open Fans to see the CPU, SFP and port chip temperatures and the speed of both fans. The readings refresh every 5 seconds.",
                "Switch control leaves the fans to the switch’s own controller. Curve sets the fan speed from the CPU temperature: Low is the quietest and Max the coolest. Manual runs both fans at the speed you choose. Thermal target runs the fans as slowly as possible while keeping the CPU at or below the temperature you choose.",
                "Every mode speeds the fans up near the switch’s temperature limits. If a sensor cannot be read or EE’s fan control stops, the switch’s own controller takes over.",
                "Fan control runs on the switch and continues while EE is closed. After a switch restart the switch controls the fans until EE connects again and restores your choice.",
            ]),
            ("Choose an app theme", [
                "Open Settings, choose Appearance, then select a dark or light theme. The change appears immediately and is saved on this device.",
            ]),
            ("Connection and recovery", [
                "Settings shows the current connection and which application controls the lighting. Refresh connection checks again. Last session shows the latest shutdown or cleanup result.",
                "Choose Handoff to UniFi in Settings to stop EE lighting and return control to UniFi. Your saved logins, profiles, and lighting setup are kept. EE waits until you connect again.",
                "When you want to resume EE, choose Connect with saved login or enter your SSH login. Choose Retry handoff when it is offered. If EE requests a recovery review, keep it disconnected until that review is complete.",
                "Connect UniFi Network in Settings to display device names from your controller. Enter its HTTPS address and account details. If prompted, verify the controller certificate fingerprint before approving it.",
                "To reconnect after restarting or updating EE, check Reconnect controller automatically and choose Connect and save login. For an already connected controller, choose Save reconnect preference. To remove its saved login, clear the checkbox and save the preference.",
                "If applying lighting fails, keep your changes and retry after the connection returns. A saved profile gives you a known setup to apply.",
                "The Demo badge identifies simulated ports used to explore the interface.",
            ]),
        };
        // Over the connection view the page sits in a dialog whose scroll bar needs its own gutter, as in the browser.
        var page = new StackPanel { Margin = new Thickness(0, 0, Workspace.Ready ? 0 : 8, 0) };
        page.Children.Add(SettingsKit.ViewHeader("Help", "Start with your ports, preview a change, then apply it."));
        // div.help-grid: the cards fill the width up to 960px.
        var grid = new StackPanel();
        for (var index = 0; index < chapters.Count; index++) grid.Children.Add(Card(chapters[index].Title, chapters[index].Paragraphs, index == 0));
        page.Children.Add(SettingsKit.LeftMax(grid, 960));
        Content = page;
    }

    /// <summary>details.help-card: a 15px summary with a chevron on the right, then the steps in muted text.</summary>
    static Control Card(string title, IEnumerable<string> paragraphs, bool open)
    {
        var heading = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, LineHeight = 21.75, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var chevron = new Border
        {
            Width = 6, Height = 6, BorderThickness = new Thickness(0, 0, 1.5, 1.5), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            RenderTransformOrigin = RelativePoint.Center,
        }.Bind(Border.BorderBrushProperty, "muted");
        var row = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Right);
        row.Children.Add(chevron);
        row.Children.Add(heading);
        var summary = new Border { Child = row, Background = Brushes.Transparent };
        var steps = new StackPanel { Spacing = 10, Margin = new Thickness(20, 14, 0, 0), MaxWidth = 76 * SettingsKit.Ch - 20, HorizontalAlignment = HorizontalAlignment.Left };
        // The steps are p elements, whose own 1.5 line height replaces the card's 1.65.
        foreach (var text in paragraphs)
            steps.Children.Add(new TextBlock { Text = text, FontSize = 13, LineHeight = 19.5, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "muted"));
        var details = new Disclosure(summary, steps) { Open = open };
        void Paint()
        {
            heading.Bind(TextBlock.ForegroundProperty, summary.IsPointerOver ? "pink" : details.Open ? "accent" : "text");
            chevron.RenderTransform = new RotateTransform(details.Open ? 225 : 45);
        }
        details.Toggled += Paint;
        summary.PropertyChanged += (_, e) =>
        {
            if (e.Property == InputElement.IsPointerOverProperty) Paint();
        };
        Paint();
        return new Border { Padding = new Thickness(0, 20), BorderThickness = new Thickness(0, 1, 0, 0), Child = details }.Bind(Border.BorderBrushProperty, "line");
    }
}
