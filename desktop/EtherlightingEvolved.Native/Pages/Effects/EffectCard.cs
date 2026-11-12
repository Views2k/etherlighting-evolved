using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Theme;
using Kit = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Effects;

internal sealed class EffectCard : Border
{
    readonly TextBlock badge = Kit.Text("", "small", true).Bind(TextBlock.ForegroundProperty, "pink");
    readonly TextBlock star = Kit.Text("☆");
    readonly Border copy, previewSlot;
    bool selected;
    string surface = "";

    internal EffectCard(JsonObject effect, JsonObject presentation, int count, bool optical,
        Action<EffectCard> toggle, Func<EffectCard, Task> choose)
    {
        Definition = effect;
        Id = effect.Str("id")!;
        Key = optical ? $"sfp:{Id}" : Id;
        Title = effect.Str("name") ?? Id;
        var description = presentation.Str("description") ?? effect.Str("description") ?? "";
        Category = presentation.Str("category") switch { "Dynamic" => "Motion", "Data" => "Activity", { } value => value, _ => "Ambient" };
        SearchText = $"{Title} {description} {Category}";
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        ClipToBounds = true;
        Favorite = Kit.Button("", "icon-button", () => toggle(this));
        star.FontSize = 20;
        Favorite.Content = star;
        Favorite.HorizontalAlignment = HorizontalAlignment.Right;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(16, 4, 9, 0), MinHeight = 38 };
        badge.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(badge);
        Grid.SetColumn(Favorite, 1);
        header.Children.Add(Favorite);
        Preview = new EffectPreview(Id, count, optical);
        previewSlot = new Border { Child = Preview };
        var name = Kit.Text(Title, wrap: true).Bind(TextBlock.ForegroundProperty, "text");
        name.FontWeight = FontWeight.SemiBold;
        name.FontSize = 15;
        var details = Kit.Text(description, "muted", true);
        details.FontSize = 13;
        details.LineHeight = 19;
        copy = new Border { Child = Kit.Column(6, name, details) };
        Open = Kit.Button("", "quiet", () => choose(this));
        Open.Padding = new Thickness(0);
        Open.BorderThickness = new Thickness(0);
        Open.HorizontalAlignment = HorizontalAlignment.Stretch;
        Open.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Open.VerticalAlignment = VerticalAlignment.Stretch;
        Open.VerticalContentAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(Open, $"{Title}. {description}");
        Open.Content = Kit.Column(0, previewSlot, copy);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Children.Add(header);
        Grid.SetRow(Open, 1);
        body.Children.Add(Open);
        Child = body;
        PointerEntered += (_, _) => Tone();
        PointerExited += (_, _) => Tone();
        SetCompact(false);
        Tone();
    }

    internal JsonObject Definition { get; }
    internal string Id { get; }
    internal string Key { get; }
    internal string Title { get; }
    internal string Category { get; }
    internal string SearchText { get; }
    internal JsonObject? Config { get; set; }
    internal EffectPreview Preview { get; }
    internal Button Open { get; }
    internal Button Favorite { get; }

    internal void SetCompact(bool compact)
    {
        MinHeight = compact ? 160 : 206;
        copy.Padding = compact ? new Thickness(12, 5, 12, 12) : new Thickness(17, 8, 17, 18);
        previewSlot.Height = Preview.ReservedHeight(compact);
    }

    internal void SetStatus(JsonObject status, bool canChoose)
    {
        selected = status.Bool("selected") == true;
        badge.Text = status.Str("label") ?? "";
        AutomationProperties.SetHelpText(Open, badge.Text);
        Open.IsEnabled = canChoose;
        Tone();
    }

    internal void SetFavorite(bool value)
    {
        star.Text = value ? "★" : "☆";
        star.Bind(TextBlock.ForegroundProperty, value ? "pink" : "favorite");
        var action = value ? $"Remove {Title} from favorites" : $"Add {Title} to favorites";
        AutomationProperties.SetName(Favorite, action);
        ToolTip.SetTip(Favorite, action);
    }

    void Tone()
    {
        var tone = selected ? "card-selected" : IsPointerOver ? "card-hover" : "card-surface";
        if (tone == surface) return;
        surface = tone;
        this.Bind(BackgroundProperty, tone);
        this.Bind(BorderBrushProperty, selected ? "card-selected-border" : IsPointerOver ? "card-hover-border" : "card-border");
    }
}
