using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using EtherlightingEvolved.Controls;
using EtherlightingEvolved.Services;
using EtherlightingEvolved.Views;

namespace EtherlightingEvolved;

public sealed class MainWindow : Window
{
    private readonly EngineClient client;
    private readonly bool demo;
    private readonly Grid root = new();
    private readonly ContentControl content = new();
    private readonly TextBlock status = new();
    private readonly TextBlock connection = new();
    private readonly TextBlock selection = new();
    private readonly StackPanel navigation = new() { Orientation = Orientation.Horizontal };
    private readonly PortCanvas canvas = new();
    private readonly ListBox library = new();
    private readonly ComboBox groups = new() { MinWidth = 180 };
    private readonly TextBox search = new();
    private readonly StackPanel parameters = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock effectName = new();
    private readonly TextBlock effectDescription = new();
    private readonly Slider master = new() { Minimum = 0, Maximum = 100, Width = 136 };
    private readonly TextBlock masterLabel = new();
    private readonly Button run = new();
    private readonly DispatcherTimer commitTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly ProgressBar busy = new() { IsIndeterminate = true, Width = 52, Height = 3, Margin = new Thickness(0, 0, 12, 0), Visibility = Visibility.Collapsed };
    private int busyCount;
    private readonly Dictionary<string, (string Path, object Body)> pending = new();
    private bool updating, connected;
    private readonly SemaphoreSlim commitLock = new(1, 1);
    private string page = "Lighting", zoneId = "", effectId = "", groupSignature = "";
    private bool manual;
    private bool settingSelection;
    private JsonObject? selectedEffect;
    private FrameworkElement? studio;
    private AutomationView? automation;
    private ScenesView? scenes;
    private SettingsView? settings;
    private TextBox? loginHost, loginUser;
    private PasswordBox? loginPassword;

    public MainWindow(EngineClient client, bool demo)
    {
        this.client = client; this.demo = demo;
        Title = "Etherlighting Evolved"; Width = 1280; Height = 820;
        MinWidth = 1040; MinHeight = 720; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush("SurfaceBrush"); Foreground = Brush("TextBrush");
        FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
        Icon = Bitmap("Assets/ee.ico");
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 44, ResizeBorderThickness = new Thickness(6), CornerRadius = new CornerRadius(10), GlassFrameThickness = new Thickness(1) });
        WindowSurface.Attach(this);
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        root.Children.Add(Chrome());
        Grid.SetRow(content, 1); root.Children.Add(content);
        var footer = new DockPanel { Margin = new Thickness(18, 0, 18, 0) };
        DockPanel.SetDock(busy, Dock.Left); footer.Children.Add(busy);
        connection.Foreground = Brush("MutedBrush"); connection.FontSize = 11;
        DockPanel.SetDock(connection, Dock.Right); footer.Children.Add(connection);
        status.Text = "Starting lighting service…"; status.FontSize = 11; status.Foreground = Brush("MutedBrush");
        footer.Children.Add(status); Grid.SetRow(footer, 2); root.Children.Add(footer);
        Content = new Border { Background = Background, BorderBrush = Brush("BorderBrush"), BorderThickness = new Thickness(1), Child = root };
        client.Changed += Update;
        commitTimer.Tick += async (_, _) => await FlushAsync();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && manual) { manual = false; BuildParameters(); e.Handled = true; }
        };
        ShowLogin();
    }

    private FrameworkElement Chrome()
    {
        var bar = new Grid { Background = Brush("PanelBrush") };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        left.Children.Add(new WindowButtons(this));
        left.Children.Add(new TextBlock { Text = "Etherlighting Evolved", FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) });
        bar.Children.Add(left);
        navigation.HorizontalAlignment = HorizontalAlignment.Center; navigation.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(navigation, 1); WindowChrome.SetIsHitTestVisibleInChrome(navigation, true); bar.Children.Add(navigation);
        foreach (var name in new[] { "Lighting", "Automation", "Scenes" }) navigation.Children.Add(Action(name, async () => await NavigateAsync(name)));
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        right.Children.Add(Action("Settings", async () => await NavigateAsync("Settings")));
        right.Children.Add(new Image { Source = Bitmap("Assets/logo.png"), Width = 32, Height = 32, Margin = new Thickness(12, 0, 0, 0) });
        WindowChrome.SetIsHitTestVisibleInChrome(right, true); Grid.SetColumn(right, 2); bar.Children.Add(right);
        return bar;
    }

    public async Task ReadyAsync()
    {
        status.Text = "Ready"; Update();
        if (client.Session["logged_in"]?.GetValue<bool>() == true) await ShowStudioAsync();
        else ShowLogin();
    }

    public void StartupFailed(string message) { status.Text = message; connection.Text = "Service unavailable"; }

    private void ShowLogin()
    {
        connected = false; navigation.IsEnabled = false;
        var panel = new StackPanel { Width = 360, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new Image { Source = Bitmap("Assets/logo.png"), Width = 110, Height = 110, Margin = new Thickness(0, 0, 0, 22) });
        panel.Children.Add(Text("Connect your switch", 25, false));
        panel.Children.Add(Text("Your lighting workspace starts here.", 13, true));
        loginHost = new TextBox { Text = client.Session["remembered"]?["host"]?.ToString() ?? "", Margin = new Thickness(0, 6, 0, 12) };
        loginUser = new TextBox { Text = client.Session["remembered"]?["username"]?.ToString() ?? "", Margin = new Thickness(0, 6, 0, 12) };
        loginPassword = new PasswordBox { Margin = new Thickness(0, 6, 0, 14) };
        panel.Children.Add(Text("Switch IP address", 12, true, 24)); panel.Children.Add(loginHost);
        panel.Children.Add(Text("SSH username", 12, true)); panel.Children.Add(loginUser);
        panel.Children.Add(Text("SSH password", 12, true)); panel.Children.Add(loginPassword);
        var remember = new CheckBox { Content = "Save login on this PC", Margin = new Thickness(0, 0, 0, 18) };
        panel.Children.Add(remember);
        var error = Text("", 12, true); error.TextWrapping = TextWrapping.Wrap;
        Button? connect = null;
        connect = Action("Connect", async () =>
        {
            if (string.IsNullOrWhiteSpace(loginHost.Text) || string.IsNullOrWhiteSpace(loginUser.Text)) { error.Text = "Enter the switch address and SSH username."; return; }
            connect!.IsEnabled = false; error.Text = "Connecting…";
            try
            {
                await client.SendAsync(HttpMethod.Post, "/api/session/login", new { host = loginHost.Text.Trim(), username = loginUser.Text.Trim(), password = loginPassword.Password, remember = remember.IsChecked == true });
                loginPassword.Clear(); await ShowStudioAsync();
            }
            catch (Exception) { error.Text = "Could not connect. Check the address, SSH credentials, and connection, then try again."; }
            finally { connect.IsEnabled = true; }
        });
        connect.Style = (Style)FindResource("PrimaryButton"); connect.Height = 38; connect.IsDefault = true; panel.Children.Add(connect);
        panel.Children.Add(Action("Explore demo", async () => { await client.SendAsync(HttpMethod.Post, "/api/session/login", new { host = "demo" }); await ShowStudioAsync(); }));
        ((FrameworkElement)panel.Children[panel.Children.Count - 1]).Margin = new Thickness(3, 8, 3, 0);
        panel.Children.Add(error);
        panel.Children.Add(Text("Unofficial community project. Not affiliated with Ubiquiti.", 11, true, 22));
        content.Content = panel; Motion.Enter(panel);
    }

    private async Task ShowStudioAsync()
    {
        if (!connected)
        {
            connected = true; navigation.IsEnabled = true;
            if (studio == null) studio = BuildStudio();
            content.Content = studio; page = "Lighting"; Update(); BuildLibrary(); BuildParameters(); Motion.Enter(studio);
            foreach (Button item in navigation.Children) item.Background = item.Content?.ToString() == "Lighting" ? Brush("BrandSelectionBrush") : Brushes.Transparent;
        }
        await Task.CompletedTask;
    }

    private FrameworkElement BuildStudio()
    {
        var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(226) }); layout.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new DockPanel { Margin = new Thickness(12, 16, 10, 12) };
        var head = new StackPanel(); head.Children.Add(Text("Effects", 17, false));
        head.Children.Add(Text("Search effects", 11, true, 12));
        search.Margin = new Thickness(0, 5, 0, 12); search.ToolTip = "Search effects";
        search.TextChanged += (_, _) => BuildLibrary(); head.Children.Add(search); DockPanel.SetDock(head, Dock.Top); sidebar.Children.Add(head);
        library.BorderThickness = new Thickness(0); library.Background = Brushes.Transparent;
        library.SelectionChanged += async (_, _) =>
        {
            if (updating || library.SelectedItem is not ListBoxItem item || item.Tag is not string id) return;
            await ApplyEffectAsync(id);
        };
        sidebar.Children.Add(library); layout.Children.Add(sidebar);
        var work = new Grid(); Grid.SetColumn(work, 1); layout.Children.Add(work);
        work.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
        work.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        work.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        var editorRow = new RowDefinition { Height = new GridLength(250) };
        work.RowDefinitions.Add(editorRow);
        SizeChanged += (_, _) => editorRow.Height = new GridLength(ActualHeight < 760 ? 210 : 250);
        var toolbar = new DockPanel { Margin = new Thickness(20, 12, 20, 8) };
        var output = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(output, Dock.Right);
        output.Children.Add(Text("Output", 12, true)); output.Children.Add(master); master.Margin = new Thickness(10, 0, 10, 0);
        masterLabel.Width = 40; masterLabel.VerticalAlignment = VerticalAlignment.Center; output.Children.Add(masterLabel);
        master.ValueChanged += (_, _) => { masterLabel.Text = $"{master.Value:0}%"; if (!updating) Queue("master", "/api/brightness", new { value = Math.Round(master.Value) }); };
        run.Content = "Pause"; run.Click += async (_, _) => await ToggleOutputAsync(); Motion.Attach(run); output.Children.Add(run); toolbar.Children.Add(output);
        var groupTools = new StackPanel { Orientation = Orientation.Horizontal }; groupTools.Children.Add(groups);
        groupTools.Children.Add(Action("Edit groups", async () => { var dialog = new GroupsDialog(client) { Owner = this }; dialog.ShowDialog(); await ReloadStateAsync(); }));
        groups.SelectionChanged += (_, _) =>
        {
            if (updating || groups.SelectedItem is not ComboBoxItem item) return;
            zoneId = item.Tag?.ToString() ?? ""; manual = false; effectId = "";
            var zone = CurrentZone(); SelectCanvas((zone?["ports"]?.AsArray() ?? new()).Select(x => x!.GetValue<int>()));
            BuildParameters(); BuildLibrary();
        };
        toolbar.Children.Add(groupTools); work.Children.Add(toolbar);
        var stage = new Grid { Margin = new Thickness(20, 12, 20, 0) };
        stage.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); stage.RowDefinitions.Add(new RowDefinition());
        var caption = new DockPanel(); caption.Children.Add(Text("PORT LIGHTING", 11, true));
        selection.HorizontalAlignment = HorizontalAlignment.Right; selection.Foreground = Brush("MutedBrush"); caption.Children.Add(selection); stage.Children.Add(caption);
        Grid.SetRow(canvas, 1); stage.Children.Add(canvas); Grid.SetRow(stage, 1); work.Children.Add(stage);
        canvas.SelectionChanged += () => { selection.Text = $"{canvas.SelectedPorts.Count} selected"; library.IsEnabled = canvas.SelectedPorts.Count > 0 && !canvas.SelectedPorts.Any(p => p > 8); library.Opacity = library.IsEnabled ? 1 : .4; if (!settingSelection) manual = true; BuildParameters(); };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 0, 20, 8) };
        controls.Children.Add(Action("Select all", () => { canvas.SetSelection(Enumerable.Range(1, 8)); return Task.CompletedTask; }));
        controls.Children.Add(Action("Clear selection", () => { canvas.SetSelection(Array.Empty<int>()); return Task.CompletedTask; }));
        controls.Children.Add(Action("Set color…", () => { manual = true; BuildParameters(); return Task.CompletedTask; }));
        controls.Children.Add(Action("Use effect", async () =>
        {
            foreach (var port in canvas.SelectedPorts.Where(p => p <= 8)) await client.SendAsync(HttpMethod.Delete, $"/api/ports/{port}");
            manual = false; await ReloadStateAsync(); BuildParameters();
        }));
        controls.Children.Add(Action("Save scene…", async () => await NavigateAsync("Scenes")));
        Grid.SetRow(controls, 2); work.Children.Add(controls);
        var editor = new DockPanel { Background = Brush("PanelBrush"), Margin = new Thickness(0, 0, 0, 0) };
        var heading = new StackPanel { Margin = new Thickness(24, 12, 24, 8) };
        effectName.FontSize = 19; effectName.FontWeight = FontWeights.SemiBold; heading.Children.Add(effectName);
        effectDescription.Foreground = Brush("MutedBrush"); effectDescription.Margin = new Thickness(0, 4, 0, 0); heading.Children.Add(effectDescription);
        DockPanel.SetDock(heading, Dock.Top); editor.Children.Add(heading);
        parameters.Margin = new Thickness(24, 0, 24, 8);
        editor.Children.Add(new ScrollViewer { Content = parameters, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetRow(editor, 3); work.Children.Add(editor);
        return layout;
    }

    private void Update()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Update); return; }
        var loggedIn = client.Session["logged_in"]?.GetValue<bool>() == true;
        if (loggedIn && !connected) { _ = ShowStudioAsync(); return; }
        if (!loggedIn && connected) { ShowLogin(); return; }
        if (!connected) return;
        updating = true;
        try
        {
            connection.Text = client.Session["simulated"]?.GetValue<bool>() == true || demo ? "Demo session" : client.Session["connected"]?.GetValue<bool>() == true && client.ConnectionError == null ? "Switch connected" : "Connection interrupted";
            var zones = client.State["zones"] as JsonArray ?? new();
            var signature = string.Join("|", zones.Select(z => $"{z?["id"]}:{z?["name"]}:{z?["ports"]}"));
            if (signature != groupSignature)
            {
                groupSignature = signature; groups.Items.Clear();
                foreach (var zone in zones) groups.Items.Add(new ComboBoxItem { Content = zone?["name"]?.ToString(), Tag = zone?["id"]?.ToString() });
                groups.SelectedItem = groups.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == zoneId) ?? groups.Items.Cast<ComboBoxItem>().FirstOrDefault();
                zoneId = (groups.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
                if (canvas.SelectedPorts.Count == 0) SelectCanvas(Enumerable.Range(1, 8));
            }
            groups.SelectedItem = groups.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == zoneId);
            if (!master.IsMouseCaptureWithin && !master.IsKeyboardFocusWithin) master.Value = Number(client.State["brightness"], 100);
            run.Content = client.State["mode"]?.ToString() == "running" ? "Pause" : "Resume";
            canvas.SetFrame(client.State["ports"] as JsonArray ?? new(), client.State["sfp"] as JsonArray);
            var nextId = CurrentZone()?["effect"]?["id"]?.ToString() ?? "";
            if (effectId != nextId) { effectId = nextId; BuildParameters(); BuildLibrary(); }
            if (client.ConnectionError != null) status.Text = client.ConnectionError;
        }
        finally { updating = false; }
    }

    private void BuildLibrary()
    {
        var offset = ViewUi.Descendants<ScrollViewer>(library).FirstOrDefault()?.VerticalOffset ?? 0;
        var before = updating; updating = true;
        library.Items.Clear();
        foreach (var effect in client.Effects.OfType<JsonObject>().Where(e => (e["name"]?.ToString() ?? "").Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
        {
            var row = new StackPanel { Margin = new Thickness(3, 2, 3, 2) };
            row.Children.Add(Text(effect["name"]!.ToString(), 13, false));
            var category = effect["category"]?.ToString() ?? "";
            row.Children.Add(Text(category switch { "animated" => "Motion", "data" => "Activity", "time" => "Time of day", _ => "Color" }, 10, true));
            var item = new ListBoxItem { Content = row, Padding = new Thickness(10, 5, 10, 5), Tag = effect["id"]!.ToString(), ToolTip = effect["description"]?.ToString() };
            library.Items.Add(item); if (item.Tag.ToString() == effectId) item.IsSelected = true;
        }
        library.UpdateLayout();
        ViewUi.Descendants<ScrollViewer>(library).FirstOrDefault()?.ScrollToVerticalOffset(offset);
        if (library.SelectedItem != null) library.ScrollIntoView(library.SelectedItem);
        updating = before;
    }

    private void BuildParameters()
    {
        parameters.Children.Clear();
        if (canvas.SelectedPorts.Any(p => p > 8)) { BuildSfp(); return; }
        if (manual) { BuildManual(); return; }
        var zone = CurrentZone();
        var id = zone?["effect"]?["id"]?.ToString() ?? "static";
        selectedEffect = client.Effects.OfType<JsonObject>().FirstOrDefault(e => e["id"]?.ToString() == id);
        effectName.Text = selectedEffect?["name"]?.ToString() ?? "Choose an effect";
        effectDescription.Text = selectedEffect?["description"]?.ToString() ?? "Select an effect from the library to begin.";
        var values = zone?["effect"]?["params"] as JsonObject ?? new();
        foreach (var param in selectedEffect?["params"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            if (param["randomizes"] != null || param["dims"] != null) continue;
            var key = param["key"]!.ToString(); var value = values[key] ?? param["default"];
            var column = new StackPanel { Width = param["type"]?.ToString() == "color" ? 180 : 160, Margin = new Thickness(0, 0, 24, 0) };
            column.Children.Add(Text(param["label"]!.ToString(), 12, true));
            var target = $"/api/zones/{Uri.EscapeDataString(zoneId)}/params";
            void Change(object? v) => Queue("param:" + key, target, new { @params = new Dictionary<string, object?> { [key] = v } });
            if (param["type"]?.ToString() == "color")
            {
                var brightnessKey = param["brightness_param"]?.ToString();
                var well = new ColorWell { Hex = value?.ToString() ?? "#FFFFFF", Brightness = Number(brightnessKey == null ? null : values[brightnessKey], 100), Margin = new Thickness(0, 8, 0, 0) };
                well.Changed += () =>
                {
                    var change = new Dictionary<string, object?> { [key] = well.Hex };
                    if (brightnessKey != null) change[brightnessKey] = well.Brightness;
                    Queue("param:" + key, target, new { @params = change });
                };
                column.Children.Add(well);
                if (param["random_param"]?.ToString() is string randomKey)
                {
                    var random = new CheckBox { Content = "Randomize", MinHeight = 20, IsChecked = values[randomKey]?.GetValue<bool>() ?? false, Margin = new Thickness(0, 6, 0, 0) };
                    random.Click += (_, _) => Queue("param:" + randomKey, target, new { @params = new Dictionary<string, object?> { [randomKey] = random.IsChecked == true } }); column.Children.Add(random);
                }
            }
            else if (param["type"]?.ToString() == "bool")
            {
                var toggle = new CheckBox { Content = "Enabled", IsChecked = value?.GetValue<bool>() ?? false, Margin = new Thickness(0, 14, 0, 0) }; toggle.Click += (_, _) => Change(toggle.IsChecked == true); column.Children.Add(toggle);
            }
            else if (param["type"]?.ToString() == "choice")
            {
                var combo = new ComboBox { Margin = new Thickness(0, 12, 0, 0), ItemsSource = param["choices"]?.AsArray().Select(x => x!.ToString()).ToList(), SelectedItem = value?.ToString() };
                combo.SelectionChanged += (_, _) => { if (combo.SelectedItem != null) Change(combo.SelectedItem.ToString()); }; column.Children.Add(combo);
            }
            else
            {
                var label = Text("", 18, false, 10); column.Children.Add(label);
                var slider = new Slider { Minimum = Number(param["min"], 0), Maximum = Number(param["max"], 100), TickFrequency = Number(param["step"], 1), IsSnapToTickEnabled = true, Margin = new Thickness(0, 12, 0, 0) };
                slider.Value = Number(value, 0); label.Text = $"{slider.Value:0.##} {param["unit"]}";
                slider.ValueChanged += (_, _) => { label.Text = $"{slider.Value:0.##} {param["unit"]}"; Change(slider.Value); }; column.Children.Add(slider);
            }
            if (param["description"] != null) column.ToolTip = param["description"]!.ToString();
            parameters.Children.Add(column);
        }
        Motion.Enter(parameters);
    }

    private void BuildManual()
    {
        var selected = canvas.SelectedPorts.Where(p => p <= 8).OrderBy(p => p).ToArray();
        effectName.Text = selected.Length == 1 ? $"Port {selected[0]}" : $"{selected.Length} selected ports";
        effectDescription.Text = "Set a color here, or choose an effect from the library.";
        var first = canvas.SelectedPorts.OrderBy(p => p).FirstOrDefault();
        var saved = client.State["overrides"]?[first.ToString()];
        var currentColor = client.State["ports"]?.AsArray().FirstOrDefault(p => p?["port"]?.GetValue<int>() == first)?["color"]?.ToString();
        var well = new ColorWell { Hex = saved?["color"]?.ToString() ?? currentColor ?? "#74A9FF", Brightness = Number(saved?["brightness"], 100), Width = 260 };
        var apply = Action("Pin color", async () =>
        {
            var ports = canvas.SelectedPorts.Where(p => p <= 8).ToArray();
            if (ports.Length == 0) { status.Text = "Select one or more ports first."; return; }
            await client.SendAsync(HttpMethod.Put, "/api/ports", new { ports = ports.ToDictionary(p => p.ToString(), p => new { color = well.Hex, brightness = (int)well.Brightness }) }); status.Text = "Color pinned";
        });
        well.Changed += () =>
        {
            var ports = canvas.SelectedPorts.Where(p => p <= 8).ToArray(); if (ports.Length == 0) return;
            Queue("manual", "/api/ports", new { ports = ports.ToDictionary(p => p.ToString(), p => new { color = well.Hex, brightness = (int)well.Brightness }) });
        };
        parameters.Children.Add(well); parameters.Children.Add(apply);
        parameters.Children.Add(Action("Customize current effect", async () =>
        {
            var current = client.State["zones"]?.AsArray().FirstOrDefault(z => z?["ports"]?.AsArray().Any(p => p!.GetValue<int>() == selected.FirstOrDefault()) == true)?["effect"];
            if (current == null) return;
            var id = current["id"]!.ToString(); var values = current["params"]?.DeepClone() ?? new JsonObject();
            await FlushAsync(); await EnsureSelectedZoneAsync(id, values);
            await client.SendAsync(HttpMethod.Put, $"/api/zones/{Uri.EscapeDataString(zoneId)}/effect", new { id, @params = values, clear_overrides = true });
            manual = false; await ReloadStateAsync(); BuildParameters();
        }));
    }

    private void BuildSfp()
    {
        var ports = canvas.SelectedPorts.Where(p => p > 8).ToArray();
        var state = client.State["sfp"]?.AsArray().FirstOrDefault(p => p?["port"]?.GetValue<int>() == ports[0]);
        effectName.Text = ports.Length == 2 ? "SFP lights" : $"SFP {ports[0]}";
        effectDescription.Text = "Independent color and motion for the selected SFP lights.";
        void Set(string key, object? value) { foreach (var p in ports) Queue($"sfp:{p}:{key}", $"/api/sfp/{p}", new Dictionary<string, object?> { [key] = value }); }
        var modeColumn = new StackPanel { Width = 160, Margin = new Thickness(0, 0, 24, 0) };
        modeColumn.Children.Add(Text("Lighting", 12, true));
        var mode = new ComboBox { Margin = new Thickness(0, 8, 0, 12) };
        foreach (var pair in new[] { ("unmanaged", "Link status"), ("color", "Custom color"), ("off", "Off") }) mode.Items.Add(new ComboBoxItem { Content = pair.Item2, Tag = pair.Item1, IsSelected = state?["mode"]?.ToString() == pair.Item1 });
        mode.SelectionChanged += (_, _) => Set("mode", (mode.SelectedItem as ComboBoxItem)?.Tag?.ToString()); modeColumn.Children.Add(mode);
        var effects = new ComboBox();
        foreach (var pair in new[] { ("steady", "Steady"), ("pulse", "Gentle pulse"), ("two_color", "Two-color fade"), ("color_cycle", "Color cycle") }) effects.Items.Add(new ComboBoxItem { Content = pair.Item2, Tag = pair.Item1, IsSelected = (state?["effect"]?.ToString() ?? "steady") == pair.Item1 });
        effects.IsEnabled = state?["mode"]?.ToString() == "color";
        effects.SelectionChanged += (_, _) => Set("effect", (effects.SelectedItem as ComboBoxItem)?.Tag?.ToString()); modeColumn.Children.Add(effects); parameters.Children.Add(modeColumn);
        foreach (var key in new[] { "color", "secondary_color" })
        {
            var brightnessKey = key == "color" ? "color_brightness" : "secondary_brightness";
            var column = new StackPanel { Width = 180, Margin = new Thickness(0, 0, 24, 0) }; column.Children.Add(Text(key == "color" ? "Primary color" : "Secondary color", 12, true));
            var well = new ColorWell { Hex = state?[key]?.ToString() ?? "#FFFFFF", Brightness = Number(state?[brightnessKey], 100), Margin = new Thickness(0, 8, 0, 0) };
            well.IsEnabled = state?["mode"]?.ToString() == "color";
            well.Changed += () => { foreach (var p in ports) Queue($"sfp:{p}:{key}", $"/api/sfp/{p}", new Dictionary<string, object?> { [key] = well.Hex, [brightnessKey] = well.Brightness }); }; column.Children.Add(well); parameters.Children.Add(column);
        }
        var timing = new StackPanel { Width = 160 }; timing.Children.Add(Text("Cycle duration", 12, true));
        var time = Text($"{Number(state?["period"], 6):0.0} s", 18, false, 10); timing.Children.Add(time);
        var speed = new Slider { Minimum = 2, Maximum = 30, Value = Number(state?["period"], 6), TickFrequency = .5, IsSnapToTickEnabled = true, Margin = new Thickness(0, 12, 0, 0) };
        speed.ValueChanged += (_, _) => { time.Text = $"{speed.Value:0.0} s"; Set("period", speed.Value); }; timing.Children.Add(speed); parameters.Children.Add(timing);
        timing.IsEnabled = state?["mode"]?.ToString() == "color";
    }

    private async Task ApplyEffectAsync(string id)
    {
        if (!canvas.SelectedPorts.Any(p => p <= 8) || canvas.SelectedPorts.Any(p => p > 8)) return;
        await FlushAsync();
        await ExecuteAsync(async () =>
        {
            await EnsureSelectedZoneAsync(id, new JsonObject());
            await client.SendAsync(HttpMethod.Put, $"/api/zones/{Uri.EscapeDataString(zoneId)}/effect", new { id, @params = new { }, clear_overrides = true });
            manual = false; SelectCanvas((CurrentZone()?["ports"]?.AsArray() ?? new()).Select(p => p!.GetValue<int>()));
            await ReloadStateAsync(); BuildParameters(); status.Text = "Effect applied";
        });
    }

    private void SelectCanvas(IEnumerable<int> ports)
    {
        settingSelection = true;
        try { canvas.SetSelection(ports); } finally { settingSelection = false; }
    }

    private async Task EnsureSelectedZoneAsync(string id, JsonNode values)
    {
        var selected = canvas.SelectedPorts.Where(p => p <= 8).ToHashSet();
        if (selected.Count == 0) throw new InvalidOperationException("Select a port first.");
        var zones = client.State["zones"]?.AsArray() ?? new();
        var exact = zones.OfType<JsonObject>().FirstOrDefault(z => selected.SetEquals(z["ports"]!.AsArray().Select(p => p!.GetValue<int>())));
        if (exact != null) { zoneId = exact["id"]!.ToString(); return; }
        var next = new JsonArray();
        foreach (var zone in zones.OfType<JsonObject>())
        {
            var remaining = zone["ports"]!.AsArray().Select(p => p!.GetValue<int>()).Where(p => !selected.Contains(p)).ToArray();
            if (remaining.Length == 0) continue;
            var copy = (JsonObject)zone.DeepClone(); copy["ports"] = new JsonArray(remaining.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()); next.Add(copy);
        }
        var newId = Guid.NewGuid().ToString("N");
        next.Add(new JsonObject { ["id"] = newId, ["name"] = selected.Count == 1 ? $"Port {selected.Single()}" : $"Ports {string.Join(", ", selected.OrderBy(p => p))}", ["ports"] = new JsonArray(selected.OrderBy(p => p).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()), ["effect"] = new JsonObject { ["id"] = id, ["params"] = values.DeepClone() } });
        await client.SendAsync(HttpMethod.Put, "/api/zones", new { zones = next });
        zoneId = newId; effectId = "";
        Update();
    }

    private async Task ToggleOutputAsync() => await ExecuteAsync(async () =>
    { await client.SendAsync(HttpMethod.Post, client.State["mode"]?.ToString() == "running" ? "/api/release" : "/api/resume"); await ReloadStateAsync(); });

    private async Task NavigateAsync(string target)
    {
        if (!connected) return;
        if (page == "Automation" && target != page && automation?.ConfirmNavigation() == false) return;
        await FlushAsync(); page = target;
        switch (target)
        {
            case "Lighting": content.Content = studio; BuildParameters(); break;
            case "Automation": automation ??= new AutomationView(client); await automation.ReloadAsync(); content.Content = automation; break;
            case "Scenes": scenes ??= new ScenesView(client); await scenes.ReloadAsync(); content.Content = scenes; break;
            case "Settings": settings ??= new SettingsView(client); await settings.ReloadAsync(); content.Content = settings; break;
        }
        foreach (Button item in navigation.Children) item.Background = item.Content?.ToString() == target ? Brush("BrandSelectionBrush") : Brushes.Transparent;
        if (content.Content is FrameworkElement view) Motion.Enter(view);
    }

    private void Queue(string key, string path, object body) { pending[path + ":" + key] = (path, body); if (!commitTimer.IsEnabled) commitTimer.Start(); }
    private async Task FlushAsync()
    {
        commitTimer.Stop(); await commitLock.WaitAsync();
        try
        {
            while (pending.Count > 0)
            {
                var batch = pending.Values.ToArray(); pending.Clear();
                foreach (var item in batch) await ExecuteAsync(async () => { await client.SendAsync(item.Path.EndsWith("/params") ? HttpMethod.Patch : HttpMethod.Put, item.Path, item.Body); status.Text = "Saved"; if (item.Path.Contains("/sfp/") && JsonSerializerNode(item.Body)?["mode"] != null) BuildParameters(); });
            }
        }
        finally { commitLock.Release(); }
    }

    private async Task ReloadStateAsync() { await client.RefreshAsync(); Update(); }
    private async Task ExecuteAsync(Func<Task> action)
    { busyCount++; busy.Visibility = Visibility.Visible; try { await action(); } catch (Exception error) { status.Text = error is EngineApiException ? error.Message : "Could not confirm the change. Check the connection and try again."; try { await client.RefreshAsync(); if (connected) BuildParameters(); } catch (Exception) { } } finally { busyCount--; if (busyCount == 0) busy.Visibility = Visibility.Collapsed; } }
    private static JsonNode? JsonSerializerNode(object value) => System.Text.Json.JsonSerializer.SerializeToNode(value);
    private JsonObject? CurrentZone() => client.State["zones"]?.AsArray().OfType<JsonObject>().FirstOrDefault(z => z["id"]?.ToString() == zoneId);
    private static double Number(JsonNode? node, double fallback) => double.TryParse(node?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private Brush Brush(string key) => (Brush)FindResource(key);
    private static BitmapImage Bitmap(string path) => new(new Uri("pack://application:,,,/" + path));
    private TextBlock Text(string value, double size, bool muted, double top = 0) => new() { Text = value, FontSize = size, Foreground = Brush(muted ? "MutedBrush" : "TextBrush"), Margin = new Thickness(0, top, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private Button Action(string label, Func<Task> action)
    {
        var button = new Button { Content = label, Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await ExecuteAsync(action); } finally { button.IsEnabled = true; } };
        Motion.Attach(button); return button;
    }

    public async Task VerifyAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        if (!demo || !connected) throw new InvalidOperationException("Verification requires an isolated demo session.");
        await Task.Delay(350);
        await ApplyEffectAsync("aurora");
        var framesBefore = client.FramesReceived;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && (client.FramesReceived < framesBefore + 3 || !(client.State["ports"]?.AsArray().Any(p => p?["preview"]?.ToString() is string c && c != "#ffffff") ?? false))) await Task.Delay(100);
        if (client.FramesReceived < framesBefore + 3) throw new InvalidOperationException("Live preview frames did not arrive.");
        await Task.Delay(200);
        Capture(Path.Combine(folder, "lighting.png"));
        var lights = ViewUi.Descendants<WindowButtons>(this).First();
        if (lights.Children.OfType<Button>().Any(b => Math.Abs(b.ActualWidth - 12) > .1 || Math.Abs(b.ActualHeight - 12) > .1)) throw new InvalidOperationException("Window control geometry is incorrect.");
        lights.SetHoverPreview(true); await Task.Delay(120); Capture(Path.Combine(folder, "window-hover.png")); lights.SetHoverPreview(false);
        var picker = ViewUi.Descendants<Button>(parameters).First(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Choose color");
        picker.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(160);
        foreach (var popup in PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>())
        {
            if (popup.RootVisual is FrameworkElement visual && visual != this && visual.ActualWidth > 100 && visual.ActualWidth < 700)
            { CaptureElement(visual, Path.Combine(folder, "color-popup.png")); }
        }
        foreach (var well in ViewUi.Descendants<ColorWell>(parameters)) well.ClosePicker();
        foreach (var target in new[] { "Automation", "Scenes", "Settings" })
        {
            await NavigateAsync(target); await Task.Delay(250); Capture(Path.Combine(folder, target.ToLowerInvariant() + ".png"));
            if (target == "Automation") await automation!.VerifyAsync();
            if (target == "Scenes") await scenes!.VerifyAsync();
            if (target == "Settings") await settings!.VerifyAsync();
        }
        await NavigateAsync("Lighting");
        var checkedEffects = 0;
        foreach (var catalogEffect in client.Effects.OfType<JsonObject>().ToArray())
        {
            var id = catalogEffect["id"]!.ToString();
            await ApplyEffectAsync(id);
            if (CurrentZone()?["effect"]?["id"]?.ToString() != id) throw new InvalidOperationException("Effect application failed: " + id);
            var expected = catalogEffect["params"]!.AsArray().Count(p => p?["randomizes"] == null && p?["dims"] == null);
            if (parameters.Children.Count != expected) throw new InvalidOperationException("Effect editor is incomplete: " + id);
            checkedEffects++;
        }
        await ApplyEffectAsync("aurora");
        Width = 1040; Height = 680; await Task.Delay(200); Capture(Path.Combine(folder, "lighting-small.png"));
        Width = 1600; Height = 980; await Task.Delay(200); Capture(Path.Combine(folder, "lighting-large.png"));
        Width = 1280; Height = 820;
        canvas.SetSelection(new[] { 1 });
        UpdateLayout();
        var portBrightness = ViewUi.Descendants<Slider>(parameters).First();
        portBrightness.Value = 35;
        await FlushAsync();
        if (Number(client.State["overrides"]?["1"]?["brightness"], -1) != 35 || client.State["overrides"]?["2"] != null)
            throw new InvalidOperationException("Individual port brightness changed the wrong scope.");
        await ApplyEffectAsync("breathing");
        if (CurrentZone()?["effect"]?["id"]?.ToString() != "breathing") throw new InvalidOperationException("Effect selection did not persist.");
        var neighbor = client.State["zones"]!.AsArray().First(z => z!["ports"]!.AsArray().Any(p => p!.GetValue<int>() == 2));
        if (neighbor?["effect"]?["id"]?.ToString() != "aurora" || client.State["overrides"]?["1"] != null)
            throw new InvalidOperationException("Individual effect selection changed neighboring ports or retained a pin.");
        await Task.Delay(200); Capture(Path.Combine(folder, "individual-port.png"));
        canvas.SetSelection(new[] { 9, 10 }); BuildParameters(); await Task.Delay(250); Capture(Path.Combine(folder, "sfp.png"));
        canvas.SetSelection(Enumerable.Range(1, 8)); manual = true; BuildParameters(); await Task.Delay(250); Capture(Path.Combine(folder, "manual.png"));
        Task? groupCheck = null;
        var groupDialog = new GroupsDialog(client) { Owner = this, ShowActivated = false };
        groupDialog.Loaded += (_, _) => { groupCheck = groupDialog.VerifyAsync(); };
        groupDialog.ShowDialog(); if (groupCheck != null) await groupCheck;
        await client.SendAsync(HttpMethod.Post, "/api/session/logout");
        ShowLogin(); await Task.Delay(250); Capture(Path.Combine(folder, "login.png"));
        await File.WriteAllTextAsync(Path.Combine(folder, "result.json"), System.Text.Json.JsonSerializer.Serialize(new { native_render = true, effects_applied = checkedEffects, live_preview_frames = client.FramesReceived, rules_sfp_save_edit_reorder_delete = true, scenes_save_apply_delete = true, groups_assignment = true, real_switch_contacted = false }));
    }

    private void Capture(string path)
    {
        CaptureElement(this, path);
    }

    private static void CaptureElement(FrameworkElement element, string path)
    {
        element.UpdateLayout(); var image = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
