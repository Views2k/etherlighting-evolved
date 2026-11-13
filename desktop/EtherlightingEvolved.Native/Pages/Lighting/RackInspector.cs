using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Ui;
using U = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Lighting;

/// <summary>
/// Port controls in a rack window (short and wide). Nothing scrolls there: each setting is one compact row, and
/// when the rows of an effect are taller than the window they are split into pages with a tab for each page.
/// A normal window keeps the full-size controls in one scrolling column.
/// </summary>
public sealed partial class LightingPage
{
    const double RackTabHeight = 28;
    readonly List<(string Section, Control Row)> rackRows = [];
    readonly List<Control> rackTools = [];
    readonly Grid rackPager = new() { IsVisible = false };
    string rackSection = "", rackKey = "";
    int rackPage;
    bool paginating;

    /// <summary>How many pages Port controls has in a rack window (1 when everything fits at once).</summary>
    internal int RackPageCount { get; private set; } = 1;

    /// <summary>False only when a single row is taller than the rack window, the one case that scrolls.</summary>
    internal bool RackFits { get; private set; } = true;

    internal void ShowRackPage(int page) { rackPage = page; PaginateRack(); }

    /// <summary>Starts a named group of settings: a heading in a normal window, a page name in a rack window.</summary>
    void Heading(string name)
    {
        if (rackLayout) rackSection = name;
        else inspector.Children.Add(U.H3(name));
    }

    void Row(Control control)
    {
        if (rackLayout) rackRows.Add((rackSection, control));
        else inspector.Children.Add(control);
    }

    /// <summary>Explanatory text: a paragraph in a normal window, a tooltip on the row above in a rack window.</summary>
    void Note(string text)
    {
        if (!rackLayout) inspector.Children.Add(U.P(text, "small muted"));
        else if (rackRows.Count > 0) ToolTip.SetTip(rackRows[^1].Row, text);
    }

    /// <summary>A secondary action such as Copy appearance. A rack window gathers these on a Tools page.</summary>
    void Tool(Button button)
    {
        if (rackLayout) rackTools.Add(button);
        else inspector.Children.Add(button);
    }

    Control Labeled(string name, Control control)
    {
        if (!rackLayout) return U.Field(name, control);
        var label = U.Text(name);
        label.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10 };
        row.Children.Add(label);
        row.Children.Add(control);
        Grid.SetColumn(control, 1);
        return row;
    }

    /// <summary>One line: label, slider, value.</summary>
    Control CompactRange(string label, double min, double max, double step, double value, Action<double> changed, Func<double, string> show, bool mixed)
    {
        var name = U.Text(label);
        var display = U.Text(mixed ? "Mixed" : show(value), "small muted");
        var range = U.Range(min, max, step, value, n => { display.Text = show(n); changed(n); });
        AutomationProperties.SetName(range, label);
        name.VerticalAlignment = display.VerticalAlignment = VerticalAlignment.Center;
        name.MaxWidth = 120;
        var row = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8, MinHeight = 28 };
        row.Children.Add(name);
        row.Children.Add(range);
        row.Children.Add(display);
        Grid.SetColumn(range, 1);
        Grid.SetColumn(display, 2);
        return row;
    }

    /// <summary>
    /// One row for a color: a swatch that opens the color picker (which also takes a hex value), the color's name
    /// and level over its level slider, and an optional control such as Randomize at the end.
    /// </summary>
    Control CompactColor(string label, string? color, double level, Action<string> changeColor, Action<double> brightness, bool relative, bool showLevel, bool mixedLevel, Control? trailing)
    {
        var current = ColorMath.NormalizeHex(color) ?? "#808080";
        var sample = new Border { Width = 28, Height = 24, CornerRadius = new CornerRadius(4), Background = U.Brush(current) };
        var swatch = U.Button("");
        swatch.Content = sample;
        swatch.Padding = new Thickness(4);
        swatch.Width = 40;
        swatch.Height = 36;
        swatch.MinHeight = 0;
        swatch.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(swatch, label);
        ToolTip.SetTip(swatch, color is null ? "Mixed colors" : current.ToUpperInvariant());
        swatch.Click += (_, _) => ColorPicker(label, current, value =>
        {
            current = value;
            sample.Background = U.Brush(value);
            ToolTip.SetTip(swatch, value.ToUpperInvariant());
            changeColor(value);
        });
        var name = U.Text(label);
        Control middle = name;
        if (showLevel)
        {
            string Level(double n)
            {
                var text = $"{n:0}%";
                if (relative && (editor.Draft.Num("brightness") ?? State.Num("brightness") ?? 100) is < 100 and { } master)
                    text += $" · peak {Js.Round(n * master / 100):0}%";
                return text;
            }
            var shown = U.Text(mixedLevel ? "Mixed" : Level(level), "small muted");
            var range = U.Range(0, 100, 1, level, n => { shown.Text = Level(n); brightness(n); });
            AutomationProperties.SetName(range, $"{label} {(relative ? "color level" : "brightness")}");
            var top = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 6 };
            top.Children.Add(name);
            top.Children.Add(shown);
            Grid.SetColumn(shown, 1);
            middle = U.Column(0, top, range);
        }
        middle.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(swatch);
        row.Children.Add(middle);
        Grid.SetColumn(middle, 1);
        if (trailing is not null)
        {
            trailing.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(trailing);
            Grid.SetColumn(trailing, 2);
        }
        return row;
    }

    /// <summary>Adds a control to a section, sharing a line between neighboring check boxes in a rack window.</summary>
    void AddSetting(StackPanel section, Control control)
    {
        if (rackLayout && control is CheckBox box)
        {
            box.MinHeight = 28;
            if (section.Children.LastOrDefault() is WrapPanel { Tag: "checks" } line) { line.Children.Add(box); return; }
            var checks = U.Wrap(14, box);
            checks.Tag = "checks";
            section.Children.Add(checks);
            return;
        }
        section.Children.Add(control);
    }

    /// <summary>Moves a section's settings into rack rows under that section's name.</summary>
    void RackSection(string name, StackPanel section)
    {
        var rows = section.Children.ToList();
        section.Children.Clear();
        foreach (var row in rows) rackRows.Add((name, row));
    }

    void ResetRackInspector()
    {
        rackRows.Clear();
        rackTools.Clear();
        rackSection = "";
    }

    /// <summary>Adds the collected rows below the fixed top of Port controls and shows the page that fits.</summary>
    void FinishRackInspector(string key)
    {
        if (!rackLayout) { inspectorScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; return; }
        if (rackTools.Count > 0)
        {
            var tools = U.Wrap(4);
            foreach (var tool in rackTools)
            {
                if (tool is Button button) { button.MinHeight = 28; button.Padding = new Thickness(8, 3); }
                tools.Children.Add(tool);
            }
            rackRows.Add(("Tools", tools));
        }
        inspector.Children.Add(rackPager);
        foreach (var (_, row) in rackRows) inspector.Children.Add(row);
        if (key != rackKey) { rackKey = key; rackPage = 0; }
        PaginateRack();
    }

    /// <summary>
    /// Measures the rows and shows the ones on the current page. Sections stay together where they fit; a section
    /// taller than a page continues on the next one.
    /// </summary>
    void PaginateRack()
    {
        if (paginating || !rackLayout) return;
        var height = inspectorFrame.Bounds.Height - inspector.Margin.Top - inspector.Margin.Bottom;
        var width = inspectorFrame.Bounds.Width - inspector.Margin.Left - inspector.Margin.Right - inspectorFrame.BorderThickness.Left;
        if (height <= 0 || width <= 0) return; // Not laid out yet; the size change calls this again.
        paginating = true;
        try
        {
            foreach (var (_, row) in rackRows) row.IsVisible = true;
            rackPager.IsVisible = false;
            inspector.Measure(new Size(width, double.PositiveInfinity));
            var spacing = inspector.Spacing;
            var rows = rackRows.Select(entry => entry.Row).ToHashSet();
            var top = inspector.Children.Where(child => child != rackPager && !rows.Contains(child)).Sum(child => child.DesiredSize.Height + spacing);
            var heights = rackRows.Select(entry => entry.Row.DesiredSize.Height).ToList();
            var all = heights.Sum() + spacing * Math.Max(0, heights.Count - 1);
            List<List<int>> pages;
            var fits = true;
            if (top + all <= height) pages = [Enumerable.Range(0, heights.Count).ToList()];
            else
            {
                var budget = height - top - RackTabHeight - spacing;
                pages = RackPages(heights, rackRows.Select(entry => entry.Section).ToList(), budget, spacing);
                fits = pages.All(page => page.Sum(index => heights[index]) + spacing * (page.Count - 1) <= budget);
            }
            rackPage = Math.Clamp(rackPage, 0, Math.Max(0, pages.Count - 1));
            RackPageCount = pages.Count;
            RackFits = fits;
            for (var page = 0; page < pages.Count; page++)
                foreach (var index in pages[page]) rackRows[index].Row.IsVisible = page == rackPage;
            rackPager.IsVisible = pages.Count > 1;
            if (pages.Count > 1) BuildRackPager(pages, width);
            // A single row taller than the window is the one case that still scrolls.
            inspectorScroll.VerticalScrollBarVisibility = fits ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            if (fits) inspectorScroll.Offset = default;
        }
        finally { paginating = false; }
    }

    internal static List<List<int>> RackPages(IReadOnlyList<double> heights, IReadOnlyList<string> sections, double budget, double spacing)
    {
        var pages = new List<List<int>>();
        var current = new List<int>();
        double used = 0;
        void Close()
        {
            if (current.Count == 0) return;
            pages.Add(current);
            current = [];
            used = 0;
        }
        for (var start = 0; start < heights.Count;)
        {
            var end = start;
            while (end < heights.Count && sections[end] == sections[start]) end++;
            var section = Enumerable.Range(start, end - start).Sum(index => heights[index]) + spacing * (end - start - 1);
            if (used + section + (current.Count > 0 ? spacing : 0) > budget && current.Count > 0 && section <= budget) Close();
            for (var index = start; index < end; index++)
            {
                var need = heights[index] + (current.Count > 0 ? spacing : 0);
                if (used + need > budget && current.Count > 0) { Close(); need = heights[index]; }
                current.Add(index);
                used += need;
            }
            start = end;
        }
        Close();
        return pages;
    }

    void BuildRackPager(List<List<int>> pages, double width)
    {
        var names = pages.Select(page => string.Join(" · ", page.Select(index => rackRows[index].Section).Where(name => name.Length > 0).Distinct())).ToList();
        var labels = names.Select((name, index) =>
        {
            if (name.Length == 0) name = "Settings";
            return names.Count(other => other == names[index]) > 1 ? $"{name} {names.Take(index + 1).Count(other => other == names[index])}" : name;
        }).ToList();
        void Show(int page) { rackPage = page; PaginateRack(); }
        Button Tab(string text, bool selected, Action click)
        {
            var button = U.Button(text, "quiet", click);
            button.Classes.Set("selected", selected);
            button.Classes.Set("primary", selected);
            button.MinHeight = RackTabHeight;
            button.Height = RackTabHeight;
            button.Padding = new Thickness(9, 2);
            button.FontSize = 12;
            return button;
        }
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        for (var page = 0; page < pages.Count; page++)
        {
            var index = page;
            tabs.Children.Add(Tab(labels[page], page == rackPage, () => Show(index)));
        }
        rackPager.Children.Clear();
        rackPager.ColumnDefinitions = new("*");
        rackPager.Children.Add(tabs);
        rackPager.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (tabs.DesiredSize.Width <= width) return;
        // Too many pages for a row of tabs: step through them instead.
        rackPager.Children.Clear();
        rackPager.ColumnDefinitions = new("Auto,*,Auto");
        var previous = Tab("‹", false, () => Show((rackPage + pages.Count - 1) % pages.Count));
        var next = Tab("›", false, () => Show((rackPage + 1) % pages.Count));
        AutomationProperties.SetName(previous, "Previous settings page");
        AutomationProperties.SetName(next, "Next settings page");
        var caption = U.Text($"{labels[rackPage]}  ({rackPage + 1} of {pages.Count})", "small muted");
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        caption.VerticalAlignment = VerticalAlignment.Center;
        rackPager.Children.Add(previous);
        rackPager.Children.Add(caption);
        rackPager.Children.Add(next);
        Grid.SetColumn(caption, 1);
        Grid.SetColumn(next, 2);
    }
}
