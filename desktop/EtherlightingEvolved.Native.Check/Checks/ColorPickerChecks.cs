using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Pages.Lighting;
using EtherlightingEvolved.Native.Shell;
using EtherlightingEvolved.Native.Ui;

namespace EtherlightingEvolved.Native.Check.Checks;

internal static class ColorPickerChecks
{
    public static async Task RunAsync(AppShell shell, int port, Action<bool, string> check)
    {
        var window = (Window)TopLevel.GetTopLevel(shell)!;
        var size = (window.Width, window.Height);
        var studio = (LightingStudio)shell.Studio;
        var overlay = shell.Overlay;
        const string favorite = "#41BC90";
        const string finalColor = "#E656AF";
        var originalPreferences = Preferences.GetItem(ColorLibraryDialog.ColorPreferences);
        var send = shell.Workspace.WorkspaceRequest();
        var originalScene = SecondaryModel.SceneFingerprint((await send("/api/scene", "GET", null)).Obj()["scene"]);
        var effect = EffectRenderer.DefaultConfig("static");
        effect["ports"] = Js.NumberArray(new[] { port });
        var label = EffectRenderer.EffectById("static").Objects("params").First(p => p.Str("type") == "color").Str("label")!;
        LightingPage Page() => (LightingPage)shell.CurrentPage!;
        TextBox Field(string name) => overlay.GetLogicalDescendants().OfType<StackPanel>()
            .Where(s => s.Children.OfType<TextBlock>().Any(t => t.Text == name))
            .SelectMany(s => s.Children.OfType<TextBox>()).Single();
        Point At(Control control, double x = .5, double y = .5) =>
            control.TranslatePoint(new Point(control.Bounds.Width * x, control.Bounds.Height * y), window)!.Value;
        void Click(Point point)
        {
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }
        void Open(string name)
        {
            var button = Page().GetLogicalDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetName(b) == name);
            button.BringIntoView(); Dispatcher.UIThread.RunJobs();
            Click(At(button));
            if (!overlay.HasDialog) throw new InvalidOperationException("The Lighting swatch did not open its picker.");
        }
        void Done(string scenario)
        {
            var done = overlay.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Done"));
            // A routed Click or scrolling the button into view would miss a clipped footer.
            Click(At(done));
            check(!overlay.HasDialog, "Done is reachable with a single click " + scenario);
            if (overlay.HasDialog) overlay.CloseAll();
        }
        string? Fixed() => studio.Draft.Obj("colors")[port.ToString()].Str("color");
        try
        {
            window.Width = 1280; window.Height = 800; Dispatcher.UIThread.RunJobs();
            await studio.ChooseEffect(effect);
            Open(label);
            var plane = overlay.GetVisualDescendants().OfType<Control>()
                .Single(c => AutomationProperties.GetName(c)?.StartsWith("Saturation and value") == true);
            var start = At(plane, .15, .25); var end = At(plane, .75, .65);
            window.MouseMove(start); window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var picked = Field("Hex color").Text;
            Page().Update(); Page().Frame();
            Done("after dragging the effect spectrum and refreshing Lighting");
            check(picked != effect.Obj("params").Str("color") && picked == EffectDraft.EffectDraftForPort(studio.Draft, port).Str("params.color"),
                "the selected effect keeps the color chosen on the spectrum");

            studio.Discard();
            var fixedMode = Page().GetLogicalDescendants().OfType<Button>().First(b => Equals(b.Content, "Fixed color"));
            fixedMode.BringIntoView(); Dispatcher.UIThread.RunJobs(); Click(At(fixedMode));
            Open("Color");
            Field("Red").Focus(); Dispatcher.UIThread.RunJobs();
            overlay.CloseAll(); Dispatcher.UIThread.RunJobs();
            check(!overlay.HasDialog && !studio.HasEdits, "closing a picker with an unchanged RGB field focused leaves the scene clean");

            var preferences = Json.Parse(originalPreferences ?? "{}").Obj();
            preferences["favoriteColors"] = Js.StringArray(new[] { favorite });
            Preferences.SetItem(ColorLibraryDialog.ColorPreferences, preferences.ToJsonString());
            Open("Color");
            var builder = overlay.GetLogicalDescendants().OfType<Expander>().Single(e => Equals(e.Header, "Save palette"));
            builder.IsExpanded = true; Dispatcher.UIThread.RunJobs();
            var name = Field("Palette name");
            name.Focus(); name.Text = "Desk colors";
            var swatch = overlay.GetLogicalDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetName(b) == "Use " + favorite);
            var redInput = Field("Red");
            redInput.Focus();
            redInput.Text = redInput.Text == "24" ? "25" : "24";
            swatch.BringIntoView(); Dispatcher.UIThread.RunJobs(); Click(At(swatch));
            check(Fixed() == favorite && Field("Hex color").Text == favorite,
                "one click selects a favorite while a pending RGB edit loses focus");
            check(builder.IsExpanded && Field("Palette name").Text == "Desk colors",
                "choosing a color keeps the palette builder open and its typed name intact");
            Done("after choosing a favorite with the palette builder open");

            Open("Color");
            var hex = Field("Hex color");
            hex.Focus(); hex.Text = "#1256AF";
            Done("with an edited hex field focused");
            check(Fixed() == "#1256AF", "Done keeps the hex color in the selected port's fixed-color preview");

            window.Width = 1280; window.Height = 400; Dispatcher.UIThread.RunJobs();
            Open("Color");
            var red = Field("Red");
            red.Focus(); red.Text = "230";
            Done("with an edited RGB field on a 1280 by 400 rack display");
            check(Fixed() == finalColor, "Done keeps the updated red channel and the other two channels");
            var preview = (await send("/api/scene", "GET", null)).Obj();
            check(SecondaryModel.SceneFingerprint(preview["scene"]) == originalScene,
                "choosing spectrum, favorite, RGB and hex colors only changes the preview");
            await studio.Apply();
            var saved = await send("/api/scene", "GET", null);
            check(string.Equals(saved.Str($"scene.overrides.{port}.color"), finalColor, StringComparison.OrdinalIgnoreCase)
                && !studio.HasEdits && studio.CanUndoApply, "Apply saves the selected port color and enables Undo");
            await studio.UndoLastApply();
        }
        finally
        {
            overlay.CloseAll();
            if (studio.HasEdits) studio.Discard();
            if (originalPreferences is null) Preferences.RemoveItem(ColorLibraryDialog.ColorPreferences);
            else Preferences.SetItem(ColorLibraryDialog.ColorPreferences, originalPreferences);
            (window.Width, window.Height) = size;
            Dispatcher.UIThread.RunJobs();
        }
    }
}
