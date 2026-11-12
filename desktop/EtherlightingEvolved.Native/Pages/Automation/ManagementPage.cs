using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using EtherlightingEvolved.Native.Theme;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Shell;
using EtherlightingEvolved.Native.Ui;
using U = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages;

internal abstract class ManagementPage : Page
{
    protected readonly Grid Body = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto") };
    public override bool FullBleed => true;
    protected readonly TextBlock Notice = U.P("", "muted");
    protected readonly Func<string, string, JsonNode?, Task<JsonNode?>> Send;
    protected bool Busy { get; private set; }
    protected bool Active { get; private set; } = true;
    protected ManagementPage(AppShell shell) : base(shell)
    {
        Send = Workspace.WorkspaceRequest();
        Body.Margin = new Thickness(24);
        Notice.IsVisible = false;
        Body.Children.CollectionChanged += (_, _) =>
        {
            for (int i = 0; i < Body.RowDefinitions.Count; i++)
            {
                var child = i < Body.Children.Count ? Body.Children[i] : null;
                Body.RowDefinitions[i].Height = child?.Classes.Contains("workspace-split") == true ? GridLength.Star : GridLength.Auto;
                if (child != null) Grid.SetRow(child, i);
            }
        };
        SizeChanged += (_, e) => Body.Margin = e.NewSize.Height < 450 && e.NewSize.Width >= 850 ? new Thickness(16, 8) : new Thickness(24);
        Content = Body;
    }

    protected void Start(Func<Task> load)
    {
        Body.Children.Add(U.P("Loading…", "muted"));
        Loaded += async (_, _) => { if (Active && Body.Children.Count == 1) await Run(load); };
    }

    protected async Task Run(Func<Task> action)
    {
        if (Busy || !Active) return;
        Busy = true;
        Body.IsEnabled = false;
        try { await action(); }
        catch (Exception error) { if (Active) Tell(error.Message, true); }
        finally { Busy = false; Body.IsEnabled = true; }
    }

    protected void Tell(string message, bool error = false)
    {
        if (!Active) return;
        Notice.Text = message;
        Notice.IsVisible = message.Length > 0;
        Notice.Classes.Set("danger", error);
        Shell.Status(message, error);
    }

    protected Task<JsonNode?> Get(string path) => Send(path, "GET", null);

    protected Button ActionButton(string label, Func<Task> action, string kind = "quiet") =>
        U.Button(label, kind, () => Run(action));

    protected static Control Header(string title, string subtitle, params Control[] actions)
    {
        var header = U.ViewHeader(title, subtitle);
        var row = U.Wrap(8, actions);
        header.Children.RemoveAt(1);
        header.RowDefinitions = new RowDefinitions("Auto,Auto");
        header.Children.Add(row);
        void Layout(double width)
        {
            bool compact = width > 0 && width < 770;
            header.ColumnDefinitions[1].Width = compact ? new GridLength(0) : GridLength.Auto;
            Grid.SetRow(row, compact ? 1 : 0);
            Grid.SetColumn(row, compact ? 0 : 1);
            row.Margin = compact ? new Thickness(0, 10, 0, 0) : default;
        }
        header.SizeChanged += (_, e) => Layout(e.NewSize.Width);
        Layout(900);
        return header;
    }

    protected static Grid Split(Control list, Control editor)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("250,*"), RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Classes.Add("workspace-split");
        if (editor is StackPanel)
            editor = new ScrollViewer { Content = editor, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        editor.MinHeight = 0;
        var listView = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var rail = new Border { Child = listView }.Bind(Border.BorderBrushProperty, "line");
        editor.MaxWidth = 940;
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        editor.VerticalAlignment = VerticalAlignment.Stretch;
        Grid.SetColumn(editor, 1);
        grid.Children.Add(rail);
        grid.Children.Add(editor);
        void Layout(double width)
        {
            bool compact = width > 0 && width < 710;
            grid.ColumnDefinitions[0].Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(width < 1040 ? 210 : 250);
            grid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            rail.BorderThickness = compact ? new Thickness(0, 0, 0, 1) : new Thickness(0, 0, 1, 0);
            rail.Padding = compact ? new Thickness(0, 0, 0, 14) : new Thickness(0, 0, 16, 0);
            grid.RowDefinitions[0].Height = compact ? GridLength.Auto : GridLength.Star;
            grid.RowDefinitions[1].Height = compact ? GridLength.Star : GridLength.Auto;
            listView.MaxHeight = compact ? Math.Clamp(grid.Bounds.Height * .3, 80, 220) : double.PositiveInfinity;
            Grid.SetColumn(editor, compact ? 0 : 1);
            Grid.SetRow(editor, compact ? 1 : 0);
            editor.Margin = compact ? new Thickness(0, 16, 0, 0) : new Thickness(20, 0, 0, 0);
        }
        grid.SizeChanged += (_, e) => Layout(e.NewSize.Width);
        Layout(900);
        return grid;
    }

    protected static Grid Editor(Control content, Control actions, Control? header = null)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        if (header != null) { header.Margin = new Thickness(0, 0, 0, 8); grid.Children.Add(header); }
        var scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var footer = new Border { Child = actions, Padding = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0) }.Bind(Border.BorderBrushProperty, "line");
        Grid.SetRow(scroll, 1); Grid.SetRow(footer, 2);
        grid.Children.Add(scroll); grid.Children.Add(footer);
        return grid;
    }

    protected static Button ListItem(string heading, string detail, bool selected, Func<Task> click)
    {
        var button = U.Button("", selected ? "selected" : "quiet", click);
        button.Content = U.Column(5, U.Text(heading, "h3", true), U.P(detail, "muted"));
        button.Padding = new Thickness(11, 12);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        return button;
    }

    protected static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    protected static string Title(string value) => value.Length == 0 ? "" : char.ToUpperInvariant(value[0]) + value[1..];
    protected static IEnumerable<string> Values(JsonNode? array) => array.Arr().Select(n => n?.ToString() ?? "");
    public override Task<bool> CanLeave() => Task.FromResult(!Busy);
    public override void Leave() => Active = false;
}
