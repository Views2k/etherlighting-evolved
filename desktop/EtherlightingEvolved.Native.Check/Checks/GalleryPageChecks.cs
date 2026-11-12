using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Pages.Effects;
using EtherlightingEvolved.Native.Shell;

namespace EtherlightingEvolved.Native.Check.Checks;

internal static class GalleryPageChecks
{
    public static async Task RunAsync(AppShell shell, Action<bool, string> check)
    {
        if (shell.Workspace.Session.Bool("simulated") != true)
            throw new InvalidOperationException("Gallery checks require a simulator session.");
        var studio = shell.Studio;
        var layout = PortTopology.LightingPorts(shell.Workspace.State);
        var originalPreferences = Preferences.GetItem("ee.ui-preferences.v1");
        Buttons all = () => shell.GetLogicalDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).ToList();
        Button Named(string name) => all().First(button => Equals(button.Content, name));
        Button Accessible(string name) => all().First(button => AutomationProperties.GetName(button) == name);
        Button Card(string name) => all().First(button => AutomationProperties.GetName(button)?.StartsWith(name + ". ", StringComparison.Ordinal) == true);
        TextBox Search() => shell.GetLogicalDescendants().OfType<TextBox>().First(box => AutomationProperties.GetName(box) == "Search effects");
        static void Click(Button button)
        {
            if (!button.IsEnabled) throw new InvalidOperationException("The requested button is disabled.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }
        void SearchFor(string value)
        {
            Search().Text = value;
            Dispatcher.UIThread.RunJobs();
        }
        async Task WaitForLighting()
        {
            var limit = System.Diagnostics.Stopwatch.StartNew();
            while (shell.PageName != "Lighting" && limit.Elapsed < TimeSpan.FromSeconds(3))
                await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
        try
        {
            Preferences.SetItem("ee.ui-preferences.v1", """{"version":1,"favoriteEffects":[],"recentEffects":[],"inspectorCollapsed":true,"favoriteColors":["#123456"]}""");
            studio.GalleryState.Clear();
            await shell.Navigate("Lighting", true);
            Dispatcher.UIThread.RunJobs();
            Click(Named("None"));
            await shell.Navigate("Effects", true);
            check(shell.GetLogicalDescendants().OfType<EffectsPage>().Any(), "Effects opens the native gallery");
            check(!Card("Activity Glow").IsEnabled, "the gallery previews effects with no port selected and disables choosing one");
            Click(Named("Favorites"));
            check(all().Any(button => Equals(button.Content, "Show all effects")), "an empty favorite list offers Show all effects");
            Click(Named("Show all effects"));
            SearchFor("nonexistent gallery check");
            check(shell.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text == "No effects match your search."),
                "an unmatched search shows its own empty state");
            Click(Named("Show all effects"));
            Click(Named("Activity"));
            SearchFor("activity glow");
            Click(Accessible("Add Activity Glow to favorites"));
            check(Json.Parse(Preferences.GetItem("ee.ui-preferences.v1") ?? "{}").Bool("inspectorCollapsed") == true,
                "starring an effect preserves the inspector preference");
            Click(Named("Favorites"));
            check(Accessible("Remove Activity Glow from favorites").IsEnabled, "filtering and starring work without a port selection");
            Click(Named("Select ports"));
            await WaitForLighting();
            Click(Named("Ethernet"));
            await shell.Navigate("Effects", true);
            check(Search().Text == "activity glow" && Named("Favorites").Classes.Contains("selected"), "the search and category survive navigation");
            var include = shell.GetLogicalDescendants().OfType<CheckBox>().First(box => Equals(box.Content, "Include SFP lights"));
            check(include.IsEffectivelyVisible && include.IsChecked == false, "an Ethernet selection can include SFP lights explicitly");
            include.IsChecked = true;
            Click(Card("Activity Glow"));
            await WaitForLighting();
            check(shell.PageName == "Lighting" && layout.Copper.Concat(layout.Sfp).All(id => EffectDraft.EffectDraftForPort(studio.Draft, id).Str("id") == "activity_glow"),
                "choosing Activity Glow stages Ethernet and included SFP ports in the normal Lighting inspector");
            Click(Named("Discard"));
            Click(Named("SFP"));
            await shell.Navigate("Effects", true);
            Click(Named("All"));
            SearchFor("");
            Click(Named("SFP lighting"));
            check(Card("Gentle pulse").IsEnabled && !all().Any(button => AutomationProperties.GetName(button)?.StartsWith("Activity Glow. ") == true),
                "the SFP library shows its optical effects");
            Click(Card("Gentle pulse"));
            await WaitForLighting();
            check(shell.PageName == "Lighting" && layout.Sfp.All(id => studio.Draft.Str($"sfp.{id}.effect") == "pulse"),
                "an optical gallery choice stages the native SFP settings and returns to Lighting");
            Click(Named("Discard"));
            await shell.Navigate("Effects", true);
            Click(Named("Recent"));
            check(Card("Gentle pulse").IsEnabled, "the Recent filter restores a chosen SFP effect");
            Click(Named("Port effects"));
            check(Card("Activity Glow").IsEnabled, "Ethernet and SFP recents keep distinct identifiers");
            var stored = Json.Parse(Preferences.GetItem("ee.ui-preferences.v1") ?? "{}");
            check(stored.Bool("inspectorCollapsed") == false, "choosing an effect reopens the Lighting inspector");
            check(stored.Arr("favoriteColors").Select(color => color.Str()).SequenceEqual(["#123456"]),
                "the gallery preserves saved colors while updating favorites and recents");
        }
        finally
        {
            await shell.Navigate("Lighting", true);
            if (studio.HasEdits && all().FirstOrDefault(button => Equals(button.Content, "Discard")) is { } discard)
                Click(discard);
            studio.GalleryState.Clear();
            if (originalPreferences is null) Preferences.RemoveItem("ee.ui-preferences.v1");
            else Preferences.SetItem("ee.ui-preferences.v1", originalPreferences);
        }
    }

    delegate List<Button> Buttons();
}
