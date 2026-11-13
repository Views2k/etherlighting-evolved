using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Shell;
using EtherlightingEvolved.Native.Theme;
using EtherlightingEvolved.Native.Ui;
using PM = EtherlightingEvolved.Native.Core.Model.PortMap;
using U = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Lighting;

public sealed partial class LightingPage : Page
{
    [ModuleInitializer]
    internal static void Register()
    {
        PageRegistry.StudioFactory = shell => new LightingStudio(shell);
        PageRegistry.Register("Lighting", shell => new LightingPage(shell));
    }
    readonly LightingStudio editor;
    readonly Grid layout = new() { RowDefinitions = new("Auto,*,Auto") }, body = new();
    readonly Border toolbar = new(), dock = new(), inspectorFrame = new();
    readonly Grid dockRow = new() { ColumnSpacing = 12, RowSpacing = 6 };
    readonly ScrollViewer mapScroll = new(), inspectorScroll = new();
    readonly Grid canvas = new() { ColumnDefinitions = new("*,Auto"), RowDefinitions = new("Auto,Auto,Auto,Auto,Auto") };
    readonly Grid rackFaceplate = new() { ColumnDefinitions = new("*,Auto"), RowDefinitions = new("Auto,Auto,Auto,0,Auto"), VerticalAlignment = VerticalAlignment.Top };
    readonly ScrollViewer rackPortScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly List<Control> portRows = [];
    readonly Grid head = new() { ColumnDefinitions = new("Auto,Auto,*"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 12, RowSpacing = 2 };
    readonly Grid footer = new() { ColumnDefinitions = new("*,Auto") };
    WrapPanel selectorRow = U.Wrap(7);
    bool rackLayout;
    readonly StackPanel inspector = U.Column(14);
    readonly TextBlock title = U.H1(""), mapStatus = U.P("", "muted"), legend = U.Text("", "muted", true);
    readonly Dictionary<int, PortTile> tiles = [];
    readonly Dictionary<string, EffectClockPair> clocks = [];
    readonly Dictionary<int, EffectAnimationState> animations = [];
    readonly Stopwatch watch = Stopwatch.StartNew();
    readonly DispatcherTimer paint = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer meterExpiry = new();
    ColorFrames frames = new();
    readonly Dictionary<int, double[]> liveColors = [];
    readonly HashSet<int> seenPorts = [];
    bool movingPreview;
    double settleAt;
    JsonObject? quickProfile;
    List<JsonObject> profiles = [];
    long lastTelemetry;
    double? scheduledExpiry;
    string shape = "", layoutMode = "", lastMode = "";
    bool hiddenInspector, mobileInspector, alive = true;
    TextBlock summary = U.Text(""), levelLabel = U.Text(""), selection = U.Text("");
    WrapPanel actions = U.Wrap();
    Button apply = U.Button("Apply changes"), compare = U.Button("Show applied"), undo = U.Button("Undo last apply"), mode = U.Button(""), details = U.Button("Port details");
    Slider master = null!;
    JsonObject State => Workspace.State;
    IEnumerable<JsonObject> Ports => State.Objects("ports").Concat(State.Objects("sfp"));
    public LightingPage(AppShell shell) : base(shell)
    {
        editor = (LightingStudio)Studio;
        hiddenInspector = Json.Parse(Preferences.GetItem(ColorPreferences) ?? "{}").Bool("inspectorCollapsed") == true;
        toolbar.Padding = new Thickness(16, 3); toolbar.BorderThickness = new Thickness(0, 0, 0, 1); Surface(toolbar, "toolbar-surface");
        dock.Padding = new Thickness(16, 8); dock.BorderThickness = new Thickness(0, 1, 0, 0); Surface(dock, "toolbar-surface");
        canvas.Margin = new Thickness(20, 14); mapScroll.Content = canvas;
        inspector.Margin = new Thickness(16); inspectorScroll.Content = inspector; inspectorFrame.Child = inspectorScroll;
        inspectorFrame.BorderThickness = new Thickness(1, 0, 0, 0); Surface(inspectorFrame, "surface");
        body.Children.Add(mapScroll); body.Children.Add(inspectorFrame);
        layout.Children.Add(toolbar); layout.Children.Add(body); Grid.SetRow(body, 1); layout.Children.Add(dock); Grid.SetRow(dock, 2); Content = layout;
        editor.Changed += OnEdited; paint.Tick += (_, _) => PaintFrame(); meterExpiry.Tick += (_, _) => { meterExpiry.Stop(); PaintBandwidth(); }; SizeChanged += (_, _) => Reflow();
        BuildDock(); BuildCanvas(); BuildInspector(); BuildToolbar(); Reflow(); Update(); WakePreview();
        connection = (Workspace.Connected, Workspace.Disconnected, Workspace.Busy);
        Workspace.ConnectionChanged += ConnectionChanged;
        inspectorFrame.SizeChanged += (_, _) => PaginateRack();
        _ = LoadProfiles();
    }
    public override bool FullBleed => true;
    static void Surface(Border b, string token) { b.Bind(Border.BackgroundProperty, token).Bind(Border.BorderBrushProperty, "line"); }
    static ScrollViewer Scroll(Control content) => new() { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    Button Action(string label, Func<Task> action, string type = "quiet") => U.Button(label, type, () => Shell.Run(action));
    async Task LoadProfiles()
    {
        try
        {
            var data = await Workspace.WorkspaceRequest()("/api/profiles", "GET", null);
            if (!alive) return; profiles = data.Objects("profiles").ToList(); quickProfile = profiles.FirstOrDefault(); BuildToolbar();
        }
        catch { if (alive) BuildToolbar(); }
    }
    void OnEdited(bool rebuild)
    {
        if (!alive) return;
        if (rebuild) { BuildInspector(); BuildToolbar(); }
        UpdateControls(); WakePreview();
    }
    void BuildToolbar()
    {
        var scopes = PM.PortScopeOptions(State).Objects().ToList();
        var chosen = scopes.FirstOrDefault(x => PortTopology.PortIds(x["ports"]).SequenceEqual(editor.Selected));
        var options = scopes.Select(x => (x.Str("value")!, x.Str("label")!)).ToList();
        if (chosen is null) options.Insert(0, ("", "Selected ports"));
        var scope = U.Select(options, chosen.Str("value") ?? "", id => { if (scopes.FirstOrDefault(x => x.Str("value") == id) is { } o) _ = Shell.Run(() => editor.SelectFromMap(PortTopology.PortIds(o["ports"]))); });
        AutomationProperties.SetName(scope, "Port group"); scope.MinWidth = 140;
        var profileOptions = profiles.Select(p => (p.Str("id")!, p.Str("name")!)).ToList();
        var profileSelect = U.Select(profileOptions.Count > 0 ? profileOptions : [("", "No saved profiles")], quickProfile.Str("id") ?? "");
        profileSelect.IsEnabled = profileOptions.Count > 0; AutomationProperties.SetName(profileSelect, "Profile");
        var use = Action("Apply profile", async () => { if (quickProfile is not null) await editor.ApplyProfile(quickProfile); });
        void Profile() { var notice = quickProfile is null ? "" : ProfilePreview.ProfileLayoutNotice(quickProfile["scene"], State); use.IsEnabled = quickProfile is not null && notice.Length == 0; use.Tip(notice); }
        profileSelect.SelectionChanged += (_, _) => { quickProfile = profiles.FirstOrDefault(p => p.Str("id") == profileSelect.Value()); Profile(); }; Profile();
        undo = Action("Undo last apply", editor.UndoLastApply); mode = U.Button("", "quiet", ToggleMode);
        scope.Width = 180; profileSelect.Width = 174; toolbar.Child = U.Wrap(14, scope, Action("Groups", Groups), profileSelect, use, Action("Profiles", () => Shell.Navigate("Profiles")), undo, Action("History", History), Action("Saved selections", () => Library("selection")), Action("Find port", FindPort), mode);
        UpdateControls();
    }
    void ToggleMode()
    {
        editor.Edit(d => { if (!d.Remove("mode")) d["mode"] = State.Str("mode") == "running" ? "released" : "running"; return d; }, true);
    }
    void BuildDock()
    {
        var initial = editor.Draft.Num("brightness") ?? State.Num("brightness") ?? 100;
        levelLabel = U.Text($"{initial:0}%", "muted");
        master = U.Range(0, 100, 1, initial, v => { if (syncingMaster) return; levelLabel.Text = $"{v:0}%"; editor.Set("brightness", (int)v); });
        master.Width = 150; AutomationProperties.SetName(master, "Default brightness");
        compare = U.Button("Show applied", "quiet", () => { editor.Compare = !editor.Compare; UpdateControls(); WakePreview(); });
        apply = Action("Apply changes", editor.Apply, "primary");
        summary = U.Text("", "muted", true); actions = U.Wrap(6, summary, compare, U.Button("Discard", "quiet", editor.Discard), apply);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        dockRow.Children.Add(U.Wrap(12, U.Text("Default brightness", "muted"), master, levelLabel, U.Button("Map key", "quiet", MapKey))); dockRow.Children.Add(actions);
        dock.Child = dockRow;
    }
    readonly Grid banks = new();
    readonly List<(bool Optical, UniformGrid Bank, StackPanel Heading, Border Section)> sections = [];
    readonly List<(Button Button, string Scope)> chips = [];
    readonly WrapPanel legendItems = U.Wrap(13);
    readonly UplinkBandwidthMeter bandwidthPanel = new();
    readonly Button openInspector = U.Button("Port controls", "quiet");
    Button rackWorkspace = U.Button("Workspace", "quiet");
    string legendSignature = "";
    void BuildCanvas()
    {
        var badge = U.Text("Demo", "small muted"); badge.IsVisible = Workspace.Session.Bool("simulated") == true;
        title.FontSize = 19; title.VerticalAlignment = badge.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(title); head.Children.Add(badge); Grid.SetColumn(badge, 1);
        head.Children.Add(mapStatus); Grid.SetRow(mapStatus, 1); Grid.SetColumnSpan(mapStatus, 3);
        canvas.Children.Add(head);
        portRows.Add(head);
        rackPortScroll.Content = rackFaceplate;
        Grid.SetColumnSpan(rackPortScroll, 2);
        canvas.Children.Add(bandwidthPanel);
        var selectors = U.Wrap(0);
        foreach (var (label, target) in new[] { ("All ports", "all"), ("Ethernet", "copper"), ("SFP", "sfp"), ("None", "none") })
        {
            var btn = Action(label, () => editor.SelectFromMap(target switch { "all" => editor.All, "copper" => editor.Copper, "sfp" => editor.Optical, _ => [] })); chips.Add((btn, target)); selectors.Children.Add(btn);
        }
        var multi = U.Button("Select multiple", "quiet"); multi.Classes.Set("selected", editor.Multiple); multi.Click += (_, _) => { editor.Multiple = !editor.Multiple; multi.Classes.Set("selected", editor.Multiple); };
        details = Action("Port details", PortDetails); rackWorkspace = U.Button("Workspace", "quiet", RackTools);
        openInspector.Click += ShowInspector;
        selectorRow = U.Wrap(7, selectors, multi, details, openInspector, rackWorkspace);
        AddRow(selectorRow, 1);
        AddRow(banks, 2);
        selection = U.Text("", "small muted");
        footer.Children.Add(legendItems); footer.Children.Add(selection); Grid.SetColumn(selection, 1); AddRow(footer, 4);
        BuildBanks();

        void AddRow(Control control, int row)
        {
            canvas.Children.Add(control);
            portRows.Add(control);
            Grid.SetRow(control, row);
            Grid.SetColumnSpan(control, 2);
        }
    }
    void SetRackLayout(bool rack)
    {
        if (rackLayout == rack) return;
        rackLayout = rack;
        var from = rack ? canvas : rackFaceplate;
        var to = rack ? rackFaceplate : canvas;
        foreach (var row in portRows) { from.Children.Remove(row); to.Children.Add(row); }
        if (rack) canvas.Children.Add(rackPortScroll);
        else canvas.Children.Remove(rackPortScroll);
        mapScroll.VerticalScrollBarVisibility = rack ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        canvas.RowDefinitions = new(rack ? "*,Auto" : "Auto,Auto,Auto,Auto,Auto");

        // A rack window is short and wide, so rows that stack in a normal window share a line: the switch name
        // and its status sit beside the selection buttons, and the map legend moves into the bottom bar.
        Grid.SetRow(selectorRow, rack ? 0 : 1); Grid.SetColumn(selectorRow, rack ? 1 : 0); Grid.SetColumnSpan(selectorRow, rack ? 1 : 2);
        selectorRow.HorizontalAlignment = rack ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        head.VerticalAlignment = rack ? VerticalAlignment.Center : VerticalAlignment.Top;
        Grid.SetRow(mapStatus, rack ? 0 : 1); Grid.SetColumn(mapStatus, rack ? 2 : 0); Grid.SetColumnSpan(mapStatus, rack ? 1 : 3);
        mapStatus.TextWrapping = rack ? TextWrapping.NoWrap : TextWrapping.Wrap;
        mapStatus.TextTrimming = rack ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        mapStatus.VerticalAlignment = VerticalAlignment.Center;
        footer.IsVisible = !rack;
        (legendItems.Parent as Panel)?.Children.Remove(legendItems);
        if (rack) dockRow.Children.Add(legendItems); else footer.Children.Insert(0, legendItems);
        Grid.SetColumn(legendItems, rack ? 1 : 0);
        legendItems.HorizontalAlignment = rack ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        legendItems.VerticalAlignment = VerticalAlignment.Center;
        // The preview summary stays on the same line as its buttons; its full text is in the tooltip.
        summary.TextWrapping = rack ? TextWrapping.NoWrap : TextWrapping.Wrap;
        summary.TextTrimming = rack ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        summary.VerticalAlignment = VerticalAlignment.Center;

        // The bottom bar sits under the port map only, so Port controls uses the full height of the window.
        (dock.Parent as Panel)?.Children.Remove(dock);
        (rack ? body : layout).Children.Add(dock);
        Grid.SetRow(dock, rack ? 1 : 2);
        body.RowDefinitions = new(rack ? "*,Auto" : "*");
        Grid.SetRowSpan(inspectorFrame, rack ? 2 : 1);
        inspector.Margin = rack ? new Thickness(12, 10) : new Thickness(16);
        inspector.Spacing = rack ? 6 : 14;
        bandwidthPanel.SetCompact(rack);
        BuildInspector();
        UpdateControls();
    }
    void BuildBanks()
    {
        shape = string.Join(",", editor.Copper) + "|" + string.Join(",", editor.Optical);
        tiles.Clear(); sections.Clear(); banks.Children.Clear();
        foreach (var (sfp, ids) in new[] { (false, editor.Copper), (true, editor.Optical) })
        {
            if (ids.Count == 0) continue;
            var h = U.Text(sfp ? "SFP" : "ETHERNET", "small muted"); h.FontWeight = FontWeight.SemiBold; h.FontSize = 10;
            var range = U.Text("Ports " + PortTopology.PortRanges(ids), "small muted"); range.FontSize = 10; range.HorizontalAlignment = HorizontalAlignment.Right;
            var captions = new Grid { ColumnDefinitions = new("*,Auto") }; captions.Children.Add(h); captions.Children.Add(range); Grid.SetColumn(range, 1);
            var heading = U.Column(0, captions); var bank = new UniformGrid { Columns = Columns(sfp, ids.Count), HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var id in ids) { var tile = new PortTile(id, sfp, SelectTile); tiles.Add(id, tile); bank.Children.Add(tile); }
            var section = new Border { Child = U.Column(4, heading, bank) }; section.Bind(Border.BorderBrushProperty, "line");
            banks.Children.Add(section); sections.Add((sfp, bank, heading, section));
        }
        Frame(); Reflow(true);
    }
    void SetInspectorCollapsed(bool value)
    {
        hiddenInspector = value;
        var prefs = Json.Parse(Preferences.GetItem(ColorPreferences) ?? "{}").Obj(); prefs["inspectorCollapsed"] = value;
        try { Preferences.SetItem(ColorPreferences, prefs.ToJsonString()); } catch (Exception ex) { Shell.Notify(ex.Message, true); }
    }
    internal void RevealEditor()
    {
        if ((Bounds.Width > 0 ? Bounds.Width : Shell.Bounds.Width) >= 701 && hiddenInspector) SetInspectorCollapsed(false);
        mobileInspector = true; Reflow(true); inspectorScroll.Focus();
    }
    void ShowInspector(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => RevealEditor();
    void SelectTile(int id, KeyModifiers mods)
    {
        var (ports, anchor) = PM.SelectPort(editor.All, editor.Selected, editor.Anchor, id, editor.Multiple || mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta), mods.HasFlag(KeyModifiers.Shift));
        _ = Shell.Run(async () => { if (await editor.SelectFromMap(ports)) editor.Anchor = anchor; });
    }
    int Columns(bool sfp, int count)
    {
        var w = mapScroll.Bounds.Width > 20 ? mapScroll.Bounds.Width : Math.Max(400, Bounds.Width - 360);
        var cap = count >= 20 ? 12 : 8; return Math.Max(1, Math.Min(count, Math.Max(1, Math.Min(sfp && count <= 4 ? 4 : cap, (int)((w - 32) / 88)))));
    }
    void Reflow(bool force = false)
    {
        var narrow = Bounds.Width > 0 && Bounds.Width < 701;
        var rack = Bounds.Width >= 900 && Bounds.Height is > 220 and < 450;
        var right = !hiddenInspector && !narrow;
        var width = Bounds.Width > 0 ? Bounds.Width - (right ? (rack ? 320 : 360) : 0) : 920;
        var inline = width >= 640 && editor.Copper.Count is > 0 and <= 8 && editor.Optical.Count is > 0 and <= 2;
        var meterBelow = rack || narrow || width < 680;
        var showChart = rack && width >= 530;
        var state = $"{narrow}:{rack}:{right}:{inline}:{meterBelow}:{showChart}:{mobileInspector}:{Columns(false, editor.Copper.Count)}";
        if (state == layoutMode && !force) return; layoutMode = state;
        dockRow.ColumnDefinitions = new(narrow ? "*" : "Auto,*"); dockRow.RowDefinitions = new(narrow ? "Auto,Auto" : "Auto"); Grid.SetColumn(actions, narrow ? 0 : 1); Grid.SetRow(actions, narrow ? 1 : 0);
        body.ColumnDefinitions = new(right ? (rack ? "*,320" : "*,360") : "*"); Grid.SetColumn(inspectorFrame, right ? 1 : 0);
        inspectorFrame.IsVisible = right || narrow && mobileInspector; mapScroll.IsVisible = !narrow || !mobileInspector;
        toolbar.IsVisible = !rack; openInspector.IsVisible = !right; rackWorkspace.IsVisible = rack;
        canvas.Margin = new Thickness(rack ? 12 : 20, rack ? 5 : 14);
        // Room left in the rack's bottom bar after the brightness controls and the three preview buttons.
        summary.MaxWidth = rack ? Math.Max(0, width - 790) : double.PositiveInfinity;
        SetRackLayout(rack);
        Grid.SetRow(bandwidthPanel, rack ? 1 : meterBelow ? 3 : 0);
        Grid.SetColumn(bandwidthPanel, meterBelow ? 0 : 1);
        Grid.SetColumnSpan(bandwidthPanel, meterBelow ? 2 : 1);
        bandwidthPanel.HorizontalAlignment = showChart ? HorizontalAlignment.Stretch
            : meterBelow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        bandwidthPanel.ShowChart(showChart);
        foreach (var child in portRows.Append<Control>(bandwidthPanel))
            child.Margin = new Thickness(0, Grid.GetRow(child) == 0 ? 0 : rack ? 6 : 14, 0, 0);
        banks.ColumnDefinitions = inline ? new ColumnDefinitions($"{editor.Copper.Count}*,{editor.Optical.Count}*") : new ColumnDefinitions("*");
        banks.RowDefinitions = inline ? new RowDefinitions("Auto") : new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", Math.Max(1, sections.Count))));
        for (var i = 0; i < sections.Count; i++)
        {
            var entry = sections[i]; Grid.SetRow(entry.Section, inline ? 0 : i); Grid.SetColumn(entry.Section, inline ? i : 0);
            entry.Section.Padding = new Thickness(inline && entry.Optical ? 8 : 0, 0, 0, 0);
            entry.Section.BorderThickness = new Thickness(inline && entry.Optical ? 1 : 0, 0, 0, 0); entry.Heading.IsVisible = !(rack && inline);
            entry.Bank.Columns = inline ? entry.Bank.Children.Count : Columns(entry.Optical, entry.Bank.Children.Count);
            foreach (var t in entry.Bank.Children.OfType<PortTile>()) t.Compact = rack;
        }
        if (mapScroll.IsVisible && alive) WakePreview();
    }
    void RackTools()
    {
        var content = U.Column(12, U.H2("Workspace")); var dlg = Shell.Overlay.Show(content, 475);
        var bar = toolbar.Child; toolbar.Child = null; content.Children.Add(bar!); content.Children.Add(U.Button("Done", "quiet", () => dlg.Close()));
        _ = Restore();
        async Task Restore() { await dlg.Result; content.Children.Remove(bar!); if (alive) BuildToolbar(); }
    }
    void UpdateControls()
    {
        title.Text = PM.SwitchTitle(State); foreach (var (button, scope) in chips) { bool selected = scope switch { "all" => editor.Selected.Count > 0 && editor.Selected.Count == tiles.Count, "copper" => editor.Selected.Count > 0 && editor.Selected.SequenceEqual(editor.Copper), "sfp" => editor.Selected.Count > 0 && editor.Selected.SequenceEqual(editor.Optical), _ => editor.Selected.Count == 0 }; button.Classes.Set("selected", selected); button.Classes.Set("primary", selected); }
        details.IsEnabled = editor.Selected.Count > 0;
        foreach (var (id, tile) in tiles) tile.Selected = editor.Selected.Contains(id);
        selection.Text = editor.Scope();
        mapStatus.Text = Workspace.Disconnected || !Workspace.Connected ? "Last confirmed colors · Connection interrupted" : State.Str("mode") == "released" ? "UniFi controls the lighting" : editor.HasEdits && !editor.Compare ? "Previewing unapplied lighting" : "";
        mapStatus.IsVisible = !string.IsNullOrEmpty(mapStatus.Text);
        actions.IsVisible = editor.HasEdits;
        legendItems.IsVisible = !rackLayout || !editor.HasEdits;
        summary.Text = editor.DraftSummary(); ToolTip.SetTip(summary, rackLayout ? summary.Text : null); apply.Content = editor.HasPendingPlan ? "Retry remaining changes" : "Apply changes";
        apply.IsEnabled = Workspace.Busy == 0 && Workspace.Connected && !Workspace.Disconnected && invalidFields.Count == 0;
        compare.Content = editor.Compare ? "Show preview" : "Show applied";
        undo.IsEnabled = editor.CanUndoApply; mode.Content = editor.Draft.ContainsKey("mode") ? "Cancel control preview" : State.Str("mode") == "running" ? "Preview return to UniFi" : "Preview EE lighting";
        mode.IsEnabled = master.IsEnabled = inspector.IsEnabled = !editor.HasPendingPlan && Workspace.Busy == 0;
        if (!master.IsKeyboardFocusWithin && !master.IsPointerOver)
        {
            var level = editor.Draft.Num("brightness") ?? State.Num("brightness") ?? 100;
            if (master.Value != level) { syncingMaster = true; try { master.Value = level; } finally { syncingMaster = false; } }
            levelLabel.Text = $"{level:0}%";
        }
    }
    bool syncingMaster;
    (bool Connected, bool Disconnected, int Busy) connection;
    void ConnectionChanged()
    {
        var current = (Workspace.Connected, Workspace.Disconnected, Workspace.Busy);
        if (current == connection) return;
        if (current.Connected != connection.Connected || current.Disconnected != connection.Disconnected) PaintBandwidth();
        connection = current; UpdateControls();
    }
    public override void Update()
    {
        if (!alive) return;
        var next = string.Join(",", editor.Copper) + "|" + string.Join(",", editor.Optical);
        if (shape != next) { BuildBanks(); BuildInspector(); BuildToolbar(); }
        var mode = State.Str("mode") ?? ""; if (mode != lastMode) { lastMode = mode; UpdateControls(); }
        PaintTelemetry(); UpdateControls();
    }
    public override void Frame()
    {
        if (!alive || Shell.Hidden) return;
        var now = watch.Elapsed.TotalMilliseconds;
        var gamma = State.Num("output.gamma") ?? 2.2; bool changed = false;
        seenPorts.Clear();
        foreach (var port in Ports)
        {
            if (port.Int("port") is not { } id || !tiles.ContainsKey(id) || !seenPorts.Add(id)) continue;
            liveColors.TryGetValue(id, out var previous);
            if (previous is not null && port["display_rgb"] is JsonArray { Count: 3 } value
                && value[0].Num() == previous[0] && value[1].Num() == previous[1] && value[2].Num() == previous[2]) continue;
            var color = PortDisplay.PortDisplayRgb(port, gamma);
            if (previous is not null && previous.AsSpan().SequenceEqual(color)) continue;
            liveColors[id] = color; changed = true;
        }
        if (liveColors.Count != seenPorts.Count)
            foreach (var missing in liveColors.Keys.Where(id => !seenPorts.Contains(id)).ToList()) { liveColors.Remove(missing); changed = true; }
        if (changed || frames.Frames.Count == 0)
        {
            frames.Push(liveColors, now); settleAt = now + frames.Delay + 80;
            if (!paint.IsEnabled) paint.Start();
        }
        if (now - lastTelemetry >= 250) { lastTelemetry = (long)now; PaintTelemetry(); }
        else if (bandwidthPanel.NeedsReading(Workspace.Bandwidth)) PaintBandwidth();
    }
    void WakePreview()
    {
        if (!alive || Shell.Hidden) return;
        movingPreview = false;
        if (editor.HasEdits && !editor.Compare)
        {
            foreach (var effect in EffectDraft.EffectDrafts(editor.Draft))
            {
                var spec = Workspace.Effects.Objects().FirstOrDefault(p => p.Str("id") == effect.Str("id"));
                if (spec.Str("category") != "static" || effect.Obj("params").Any(k => k.Key.StartsWith("random_", StringComparison.Ordinal) && k.Value.Bool() == true)) { movingPreview = true; break; }
            }
            if (editor.Draft.Obj("sfp").Any(pair => pair.Value.Str("mode") == "color" && pair.Value.Str("effect") is "pulse" or "two_color" or "color_cycle")) movingPreview = true;
        }
        PaintFrame();
        if (movingPreview && !paint.IsEnabled) paint.Start();
    }
    void PaintFrame()
    {
        if (!alive || Shell.Hidden || !mapScroll.IsVisible) { paint.Stop(); return; }
        var time = watch.Elapsed.TotalMilliseconds;
        var confirmedColors = frames.Sample(time);
        Dictionary<int, double[]>? preview = null;
        if (editor.HasEdits && !editor.Compare) preview = DraftPreview.PreviewDraftColors(State, editor.Draft, time / 1000, clocks, animations);
        foreach (var (id, tile) in tiles)
        {
            var rgb = preview?.GetValueOrDefault(id) ?? confirmedColors.GetValueOrDefault(id.ToString());
            tile.Previewed = preview?.ContainsKey(id) == true; if (rgb is not null) tile.PaintColor(rgb);
        }
        if (!movingPreview && time >= settleAt) paint.Stop();
    }
    void PaintTelemetry()
    {
        RecordHistory(); var speeds = new SortedDictionary<int, (string Key, string Label)>();
        foreach (var p in Ports) if (p.Int("port") is { } id && tiles.TryGetValue(id, out var tile))
            {
                tile.Read(p); var link = PM.LinkStatus(p); var key = link["key"]?.ToString() ?? "unknown";
                speeds[int.TryParse(key, out var rate) ? rate : key == "offline" ? int.MaxValue - 2 : key == "disabled" ? int.MaxValue - 1 : int.MaxValue] = (key, link.Str("label") ?? "Not reported");
            }
        var signature = string.Join(",", speeds.Values.Select(x => x.Key));
        if (signature != legendSignature)
        {
            legendSignature = signature; legendItems.Children.Clear();
            foreach (var (key, label) in speeds.Values)
            {
                var dot = new Border { Width = 7, Height = 7, Background = PortTile.SpeedBrush(key), VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(1) };
                legendItems.Children.Add(U.Row(4, dot, U.Text(label, "small muted")));
            }
            legendItems.Children.Add(U.Row(4, new Icon("chevron", 13) { RenderTransform = new RotateTransform(-90) }, U.Text("Uplink", "small muted")));
            legendItems.Children.Add(U.Text("PoE", "small muted"));
        }
        PaintBandwidth();
    }
    void PaintBandwidth()
    {
        var now = Environment.TickCount64;
        var deadline = bandwidthPanel.RenderReading(Workspace.Bandwidth, now);
        if (deadline is null || Shell.Hidden) { meterExpiry.Stop(); scheduledExpiry = null; return; }
        if (scheduledExpiry == deadline && meterExpiry.IsEnabled) return;
        scheduledExpiry = deadline;
        meterExpiry.Interval = TimeSpan.FromMilliseconds(Math.Max(1, Math.Ceiling(deadline.Value - now)));
        meterExpiry.Start();
    }
    public override void VisibilityChanged(bool visible)
    {
        bandwidthPanel.SetPageVisible(visible);
        if (!visible) { paint.Stop(); meterExpiry.Stop(); watch.Stop(); return; }
        if (alive) { watch.Start(); frames = new(); Frame(); PaintTelemetry(); WakePreview(); }
    }
    public override void Leave() { alive = false; paint.Stop(); meterExpiry.Stop(); bandwidthPanel.SetPageVisible(false); editor.Changed -= OnEdited; Workspace.ConnectionChanged -= ConnectionChanged; watch.Stop(); }
    void MapKey()
    {
        var content = U.Column(14, U.H2("Port map key"), U.P(string.Join("    ", PM.LinkSpeeds.Select(s => s.Label))), U.P("↑ Uplink     ϟ PoE"), U.P(PM.SelectionHint("Windows", editor.Multiple)), U.P("Default brightness applies to effects and SFP lighting. Fixed port colors and rules with their own brightness keep that setting."));
        var dlg = Shell.Overlay.Show(content, 475); content.Children.Add(U.Wrap(8, Action("Lighting help", async () => { dlg.Close(); await Shell.Navigate("Help"); }), U.Button("Done", "primary", () => dlg.Close())));
    }
}
