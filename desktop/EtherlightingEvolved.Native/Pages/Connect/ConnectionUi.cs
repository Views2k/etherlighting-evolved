using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EtherlightingEvolved.Native.Theme;

namespace EtherlightingEvolved.Native.Pages.Connect;

/// <summary>
/// The connection views' own markup: the accent heading, the logo and external links. Fields, help text, buttons and
/// the other form controls are the shared ones in Ui/Ui.cs and Ui/FormControls.cs.
/// </summary>
public static class ConnectionUi
{
    /// <summary>The h2 used in connection headings: 20px semibold in the accent color.</summary>
    public static TextBlock Heading(string text) =>
        new TextBlock { Text = text, Classes = { "h2" }, LineHeight = 29, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "accent");

    public static Image Logo(double size)
    {
        var logo = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://EtherlightingEvolved/Assets/logo-256.png"));
            logo.Source = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, (int)(size * 2), Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        }
        catch (Exception) { }
        return logo;
    }

    /// <summary>Opens an address in the default browser, like a link with target _blank.</summary>
    public static async Task OpenLink(Visual owner, string url)
    {
        try
        {
            if (TopLevel.GetTopLevel(owner)?.Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception) { }
    }
}
