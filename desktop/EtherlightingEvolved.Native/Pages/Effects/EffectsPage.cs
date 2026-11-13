using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Shell;
using EtherlightingEvolved.Native.Theme;
using Kit = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Effects;

internal static class Registration
{
    [ModuleInitializer]
    internal static void Register() => PageRegistry.Register("Effects", shell => new EffectsPage(shell));
}

public sealed class EffectsPage : Page
{
    readonly List<EffectCard> cards = [];
    List<EffectCard> visible = [];
    List<string> favorites = GalleryState.Favorites(), recent = GalleryState.Recent();
    readonly TextBlock target = Kit.Text("", wrap: true), note = Kit.Text("", "muted small", true);
    readonly TextBlock message = Kit.Text("", "muted", true);
    /// <summary>p.inline-error: why the gallery could not be drawn.</summary>
    readonly TextBlock failure = Kit.Text("", "danger", true);
    string? configurationKey;
    readonly Button choosePorts, ethernetTab, sfpTab;
    readonly CheckBox include;
    readonly TextBox search;
    readonly WrapPanel tabs, filters = Kit.Wrap(5), targetControls;
    readonly Dictionary<string, Button> categoryButtons = [];
    readonly GalleryPanel grid = new();
    readonly Border tabGroup;
    readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly StackPanel header, content = Kit.Column(0);
    readonly DockPanel body = new() { LastChildFill = true };
    readonly StackPanel empty;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly Stopwatch clock = Stopwatch.StartNew();
    string category, library;
    JsonArray? catalog;
    int[] selection = [], ports = [];
    bool compact, scrollHeader, synchronizing, left, attached, still, choosing;
    double previousTime;

    public EffectsPage(AppShell shell) : base(shell)
    {
        var saved = Studio.GalleryState;
        category = saved.Str("category") is { } group && GalleryState.Categories.Contains(group) ? group : "All";
        library = saved.Str("type") == "sfp" ? "sfp" : "ethernet";
        target.FontWeight = FontWeight.Medium;
        choosePorts = Kit.Button("Select ports", "quiet", () => Run(Studio.ChangeSelection));
        ethernetTab = Kit.Button("Port effects", "quiet", () => SetLibrary("ethernet"));
        sfpTab = Kit.Button("SFP lighting", "quiet", () => SetLibrary("sfp"));
        tabs = Kit.Wrap(4, ethernetTab, sfpTab);
        AutomationProperties.SetName(tabs, "Effect library");
        include = Kit.Check("Include SFP lights", false, _ => { if (!synchronizing) Update(); });
        tabGroup = Toolbar(tabs, "type-filter-surface");
        targetControls = Kit.Wrap(12, Kit.Column(3, target, note), tabGroup, include, choosePorts);
        var heading = Kit.Column(4, Kit.H1("Effects"), Kit.Text("Preview an effect, then apply it in Port controls.", "muted", true));
        var headingRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        headingRow.Children.Add(heading);
        Grid.SetColumn(targetControls, 1);
        headingRow.Children.Add(targetControls);
        search = Kit.Input(saved.Str("search"), "Search effects");
        AutomationProperties.SetName(search, "Search effects");
        search.Width = 270;
        search.TextChanged += (_, _) =>
        {
            var query = search.Text ?? "";
            if ((saved.Str("search") ?? "") == query) return;
            saved["search"] = query;
            Filter(resetScroll: true);
        };
        foreach (var name in GalleryState.Categories)
        {
            var button = Kit.Button(name, "quiet", () =>
            {
                category = name;
                saved["category"] = category;
                Filter(resetScroll: true);
            });
            categoryButtons[name] = button;
            filters.Children.Add(button);
        }
        AutomationProperties.SetName(filters, "Effect category");
        failure.IsVisible = false;
        failure.FontSize = 13;
        failure.LineHeight = 19;
        header = Kit.Column(16, headingRow, Kit.Wrap(14, search, Toolbar(filters, "filter-surface")), failure);
        header.Margin = new Thickness(0, 0, 0, 14);
        empty = Kit.Column(8, message, Kit.Button("Show all effects", "quiet", () =>
        {
            category = "All";
            saved["category"] = category;
            search.Text = "";
            Filter(resetScroll: true);
        }));
        empty.HorizontalAlignment = HorizontalAlignment.Left;
        empty.Margin = new Thickness(4, 16);
        content.Children.Add(grid);
        content.Children.Add(empty);
        scroll.Content = content;
        AutomationProperties.SetName(grid, "Effect previews");
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        body.Children.Add(scroll);
        Content = new Border { Padding = new Thickness(24), Child = body };
        SizeChanged += (_, _) =>
        {
            var width = Bounds.Width;
            var narrow = width < 920;
            var wrapHeading = width < 1140 || targetControls.DesiredSize.Width > width - 430;
            Grid.SetRow(targetControls, wrapHeading ? 1 : 0);
            Grid.SetColumn(targetControls, wrapHeading ? 0 : 1);
            targetControls.Margin = new Thickness(0, wrapHeading ? 10 : 0, 0, 0);
            targetControls.HorizontalAlignment = wrapHeading ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            targetControls.MaxWidth = Math.Max(80, width - 48);
            search.Width = Math.Min(270, Math.Max(80, width - 48));
            bool nextCompact = width >= 900 && Bounds.Height <= 500;
            if (compact != nextCompact)
            {
                compact = nextCompact;
                grid.Compact = compact;
                foreach (var card in cards) card.SetCompact(compact);
            }
            bool nextScrollHeader = narrow && Bounds.Height <= 650;
            if (nextScrollHeader != scrollHeader)
            {
                scrollHeader = nextScrollHeader;
                if (scrollHeader) { body.Children.Remove(header); content.Children.Insert(0, header); }
                else { content.Children.Remove(header); body.Children.Insert(0, header); }
            }
        };
        scroll.ScrollChanged += (_, _) =>
        {
            if (attached && !left) saved["scroll"] = scroll.Offset.Y;
            if (still) Paint();
        };
        timer.Tick += (_, _) => Paint();
        AttachedToVisualTree += (_, _) =>
        {
            attached = true;
            still = ReduceMotion();
            var offset = saved.Num("scroll") ?? 0;
            Update();
            Dispatcher.UIThread.Post(() =>
            {
                if (left) return;
                scroll.Offset = new Vector(0, offset);
                SyncAnimation();
            }, DispatcherPriority.Loaded);
        };
        DetachedFromVisualTree += (_, _) => { attached = false; timer.Stop(); };
        shell.EffectsLoaded += Update;
        Update();
    }

    public override bool FullBleed => true;
    bool Optical => library == "sfp";

    static Border Toolbar(Control content, string background) =>
        new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(3), Child = content }
            .Bind(Border.BackgroundProperty, background);

    void SetLibrary(string value)
    {
        if (library == value) return;
        library = value;
        Studio.GalleryState["type"] = value;
        scroll.Offset = default;
        catalog = null;
        Update();
    }

    /// <summary>
    /// The gallery's configurationKey (effect-gallery.mjs:288): the settings that change card configurations, statuses or
    /// previews, plus the library and the SFP choice that the browser handles with their own redraws.
    /// </summary>
    string ConfigurationKey(JsonObject state, bool clockReady)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            static void Write(System.Text.Json.Utf8JsonWriter writer, JsonNode? node)
            {
                if (node is null) writer.WriteNullValue(); else node.WriteTo(writer);
            }
            writer.WriteStartArray();
            writer.WriteBooleanValue(clockReady);
            writer.WriteStartArray();
            foreach (var id in Studio.Selected) writer.WriteNumberValue(id);
            writer.WriteEndArray();
            Write(writer, Studio.Draft);
            writer.WriteStartObject();
            foreach (var (id, config) in Studio.RememberedEffects) { writer.WritePropertyName(id); Write(writer, config); }
            writer.WriteEndObject();
            foreach (var key in new[] { "zones", "overrides", "brightness", "mode", "sfp_sync" }) Write(writer, state[key]);
            writer.WriteStartArray();
            foreach (var port in state.Objects("ports"))
            {
                writer.WriteStartArray();
                Write(writer, port["port"]); Write(writer, port["source"]); Write(writer, port["override"]);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStartArray();
            foreach (var light in state.Objects("sfp"))
            {
                writer.WriteStartArray();
                foreach (var key in new[] { "port", "mode", "effect", "color", "secondary_color", "brightness", "color_brightness", "secondary_brightness", "period", "rule" })
                    Write(writer, light[key]);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteStringValue(library);
            writer.WriteBooleanValue(include.IsChecked == true);
            writer.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public override void Update()
    {
        if (left || synchronizing) return;
        synchronizing = true;
        try
        {
            var state = Workspace.State;
            ScheduleClock.Receive(state["schedule_clock"]);
            var layout = PortTopology.LightingPorts(state);
            var selected = Studio.Selected.Order().ToArray();
            if (!selection.SequenceEqual(selected))
            {
                selection = selected;
                include.IsChecked = selected.Any(layout.Sfp.Contains);
            }
            bool hasCopper = selection.Any(layout.Copper.Contains), hasOptical = selection.Any(layout.Sfp.Contains);
            if (Optical && !hasOptical && hasCopper)
            {
                library = "ethernet";
                Studio.GalleryState["type"] = library;
            }
            int[] scope = GalleryState.Scope(layout, selected, Optical, include.IsChecked == true);
            var list = Optical ? EffectGallery.SfpEffects : Workspace.Effects;
            bool rebuild = !ReferenceEquals(list, catalog) || !ports.SequenceEqual(scope);
            bool clockReady = ScheduleClock.HostDate(state).HasValue;
            // Nothing that the cards show changed: keep them as they are.
            var key = ConfigurationKey(state, clockReady);
            if (!rebuild && key == configurationKey) return;
            configurationKey = key;
            ports = scope;
            if (rebuild)
            {
                catalog = list;
                Rebuild(list);
            }
            var sfp = PortIndex(state, "sfp");
            var copper = PortIndex(state, "ports");
            var draft = Studio.Draft;
            var opticalCount = ports.Count(layout.Sfp.Contains);
            var noun = ports.Length == 1 ? "port" : "ports";
            var prefix = opticalCount == ports.Length ? $"SFP {noun}" : opticalCount == 0 ? $"Ethernet {noun}" : "Ports";
            target.Text = ports.Length == 0 ? "Select ports to preview effects" : $"{prefix} {PortTopology.PortRanges(ports)}";
            bool rule = false, manual = false;
            foreach (var id in ports)
            {
                sfp.TryGetValue(id, out var opticalPort);
                copper.TryGetValue(id, out var ethernetPort);
                if (Optical) { rule |= opticalPort.Truthy("rule"); continue; }
                var port = opticalPort ?? ethernetPort;
                var pending = EffectDraft.EffectDraftForPort(draft, id);
                var overridden = port.Truthy("override") || state.Truthy($"overrides.{id}") || port.Str("source.kind") == "override";
                if (ethernetPort.Str("source.kind") == "rule") rule = true;
                if (draft.Truthy($"colors.{id}") || (overridden && pending.Bool("clearOverrides") != true
                        && !draft.Arr("returnToEffect").Any(value => value.Int() == id))) manual = true;
                else if (pending is null) rule |= opticalPort.Truthy("rule") || port.Str("source.kind") == "rule";
            }
            note.Text = $"Effect preview · {Math.Round(draft.Num("brightness") ?? state.Num("brightness") ?? 100)}% default brightness"
                + (rule ? " · Automation active" : manual ? " · Fixed colors active" : "")
                + (Optical && SfpSyncModel.SfpSync(state, draft).Str("mode") == "ethernet" ? " · Choosing an effect replaces Follow Ethernet" : "");
            choosePorts.Content = ports.Length == 0 ? "Select ports" : "Change selection";
            include.IsVisible = !Optical && hasCopper && layout.Sfp.Count > 0;
            tabGroup.IsVisible = hasOptical;
            SetPressed(ethernetTab, !Optical);
            SetPressed(sfpTab, Optical);
            foreach (var card in cards)
            {
                card.Config = Optical ? null : EffectGallery.GalleryConfig(card.Definition, state, ports, Studio.RememberedEffects, draft);
                var status = Optical ? EffectGallery.SfpGalleryStatus(card.Definition, state, ports, draft)
                    : card.Id == "daylight" && !clockReady ? new JsonObject { ["label"] = "Waiting for EE clock" }
                    : EffectGallery.GalleryStatus(card.Definition, state, ports, draft, card.Config);
                card.SetStatus(status, ports.Length > 0);
                card.Preview.Configure(state, draft, ports, sfp, card.Config);
            }
            if (rebuild) Filter(false);
            SyncAnimation();
            failure.IsVisible = false;
        }
        catch (Exception error)
        {
            // A malformed catalog or state fails this update only; the next one tries again.
            configurationKey = null;
            catalog = null;
            failure.Text = error.Message;
            failure.IsVisible = true;
        }
        finally { synchronizing = false; }
    }

    static Dictionary<int, JsonObject> PortIndex(JsonObject state, string key)
    {
        var result = new Dictionary<int, JsonObject>();
        foreach (var port in state.Objects(key))
            if (port.Int("port") is { } id) result.TryAdd(id, port);
        return result;
    }

    void Rebuild(JsonArray list)
    {
        grid.Children.Clear();
        cards.Clear();
        if (list.Count == 0) return;
        if (!Optical) EffectRenderer.SetEffectCatalog(list);
        var presentation = (Optical ? list : EffectRenderer.EffectList).OfType<JsonObject>()
            .Where(effect => effect.Str("id") is not null).ToDictionary(effect => effect.Str("id")!);
        foreach (var effect in list.OfType<JsonObject>())
        {
            if (effect.Str("id") is not { } id) continue;
            int count = ports.Length == 0 ? Optical ? 2 : 8 : ports.Length;
            var card = new EffectCard(effect, presentation.GetValueOrDefault(id) ?? effect, count, Optical, ToggleFavorite, Choose);
            card.SetFavorite(favorites.Contains(card.Key));
            card.SetCompact(compact);
            cards.Add(card);
        }
    }

    static void SetPressed(Button button, bool pressed)
    {
        button.Classes.Set("selected", pressed);
        AutomationProperties.SetHelpText(button, pressed ? "Selected" : "");
    }

    void Filter(bool resetScroll)
    {
        var query = search.Text?.Trim() ?? "";
        foreach (var (key, button) in categoryButtons) SetPressed(button, key == category);
        visible = cards.Where(card => card.SearchText.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            && (category == "All" || category == card.Category || category == "Favorites" && favorites.Contains(card.Key)
                || category == "Recent" && recent.Contains(card.Key))).ToList();
        if (category == "Recent") visible.Sort((a, b) => recent.IndexOf(a.Key).CompareTo(recent.IndexOf(b.Key)));
        // Keep only the displayed cards in visual and keyboard order, including Recent.
        if (!grid.Children.SequenceEqual(visible))
        {
            grid.Children.Clear();
            foreach (var card in visible) grid.Children.Add(card);
        }
        empty.IsVisible = visible.Count == 0;
        message.Text = query.Length == 0 && category == "Favorites" ? "Star an effect to keep it here."
            : query.Length == 0 && category == "Recent" ? "Effects you choose appear here." : "No effects match your search.";
        if (resetScroll) { scroll.Offset = default; Studio.GalleryState["scroll"] = 0; }
        for (var i = 0; i < visible.Count; i++)
        {
            var card = visible[i];
            card.Favorite.TabIndex = i * 2 + 1;
            card.Open.TabIndex = i * 2 + 2;
        }
        if (attached) SyncAnimation();
    }

    void ToggleFavorite(EffectCard card)
    {
        try
        {
            favorites = GalleryState.Change(card.Key, true);
            card.SetFavorite(favorites.Contains(card.Key));
            Filter(false);
            if (!visible.Contains(card)) categoryButtons[category].Focus();
        }
        catch (InvalidOperationException error) { Shell.Notify(error.Message, true); }
    }

    async Task Choose(EffectCard card)
    {
        if (ports.Length == 0 || choosing) return;
        choosing = true;
        try
        {
            try { recent = GalleryState.Change(card.Key, false); }
            catch (InvalidOperationException error) { Shell.Notify(error.Message, true); }
            if (Optical) await Run(() => Studio.ChooseSfpEffect(new JsonObject { ["effect"] = card.Id }, ports));
            else if (card.Config?.DeepClone() is JsonObject config)
            {
                config["ports"] = new JsonArray(ports.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
                await Run(() => Studio.ChooseEffect(config));
            }
        }
        finally { choosing = false; }
    }

    async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { Shell.Notify(error.Message, true); }
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    static extern bool ReadAnimationSetting(uint action, uint parameter, out int value, uint flags);

    static bool ReduceMotion() => OperatingSystem.IsWindows()
        && ReadAnimationSetting(0x1042, 0, out var enabled, 0) && enabled == 0;

    void SyncAnimation()
    {
        if (!attached || left || Shell.Hidden || visible.Count == 0) { timer.Stop(); return; }
        if (still) { timer.Stop(); Paint(); }
        else if (!timer.IsEnabled) { previousTime = 0; Paint(); timer.Start(); }
    }

    void Paint()
    {
        if (left || !attached || Shell.Hidden) { timer.Stop(); return; }
        double now = clock.Elapsed.TotalMilliseconds;
        double elapsed = previousTime <= 0 ? 16 : now - previousTime;
        previousTime = now;
        double time = still ? 1.25 : now / 1000;
        var state = Workspace.State;
        var hour = ScheduleClock.HostDate(state)?.TimeOfDay.TotalHours;
        JsonObject?[]? livePorts = null;
        IReadOnlyList<JsonNode?>? traffic = null;
        foreach (var card in visible)
        {
            var origin = card.Preview.TranslatePoint(default, scroll);
            if (origin is null || card.Preview.Bounds.Height <= 0 || origin.Value.Y + card.Preview.Bounds.Height <= 0 || origin.Value.Y >= scroll.Bounds.Height) continue;
            if (card.Category == "Activity" || card.Id == "daylight" && hour is null)
            {
                if (livePorts is null)
                {
                    var copper = PortIndex(state, "ports");
                    var optical = PortIndex(state, "sfp");
                    livePorts = ports.Select(id => copper.GetValueOrDefault(id) ?? optical.GetValueOrDefault(id)).ToArray();
                }
                if (card.Category == "Activity" && traffic is null)
                    traffic = Workspace.Session.Bool("simulated") != false
                        ? EffectRenderer.DataPreviewFacts(Math.Max(1, ports.Length), time).ToArray()
                        : livePorts.Select(port => port?["facts"]).ToArray();
            }
            card.Preview.Paint(time, elapsed, still, hour, card.Category == "Activity" ? traffic : null, livePorts ?? []);
        }
    }

    public override void VisibilityChanged(bool shown)
    {
        if (!shown) { timer.Stop(); previousTime = 0; return; }
        still = ReduceMotion();
        SyncAnimation();
    }

    public override void Leave()
    {
        left = true;
        Shell.EffectsLoaded -= Update;
        timer.Stop();
        Studio.GalleryState["scroll"] = scroll.Offset.Y;
    }
}
