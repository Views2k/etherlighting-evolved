// Offscreen checks for the native window. Nothing here opens a window on screen.
// Usage: EtherlightingEvolved.Native.Check [--output dir] [--service [--page-check Lighting|Effects|Automation|Profiles|Fans]]
// --service starts the simulator from this source checkout and changes its lighting; it never contacts a switch.
using Avalonia;
using Avalonia.Headless;
using EtherlightingEvolved.Native;
using EtherlightingEvolved.Native.Check.Checks;

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

var output = Path.GetFullPath(Arg("--output") ?? "native-check");
Directory.CreateDirectory(output);
Locations.TestDataDirectory = Path.Combine(output, "client");
var failures = new List<string>();
void Check(bool ok, string name)
{
    Console.WriteLine($"{(ok ? "pass" : "FAIL")}  {name}");
    if (!ok) failures.Add(name);
}

PreferencesChecks.Run(Check);
DesktopLogChecks.Run(Check);
if (args.Contains("--service") || args.Contains("--quit-check"))
{
    ShutdownChecks.Run(output, Check).GetAwaiter().GetResult();
    if (args.Contains("--quit-check")) return failures.Count == 0 ? 0 : 1;
}

await WorkspaceTransportChecks.Run(Check);
if (args.Contains("--transport-check")) return failures.Count == 0 ? 0 : 1;

ModelChecks.Run(Check);
AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

ShellChecks.Contracts(Check);
ConnectionsChecks.Run(Check);
ControllerConnectionChecks.Run(Check);
HostKeyReviewChecks.Run(Check);

// Palettes read from the browser workspace's themes.css
Check(EtherlightingEvolved.Native.Theme.ThemeTokens.Themes.Count == 12, "all 12 themes are listed");
foreach (var theme in EtherlightingEvolved.Native.Theme.ThemeTokens.Themes)
{
    var tokens = EtherlightingEvolved.Native.Theme.ThemeTokens.Resolve(theme.Id);
    var missing = new[] { "surface", "panel", "raised", "text", "muted", "faint", "line", "accent", "button", "button-text", "chrome", "dialog", "selection-soft" }
        .Where(name => tokens.GetValueOrDefault(name) is not Avalonia.Media.ISolidColorBrush).ToList();
    Check(missing.Count == 0 && tokens.GetValueOrDefault("primary-fill") is Avalonia.Media.ILinearGradientBrush && tokens.GetValueOrDefault("canvas-tint") is Avalonia.Media.IBrush[] { Length: >= 2 }
          && tokens.GetValueOrDefault("shadow") is Avalonia.Media.BoxShadows { Count: 2 },
        $"theme {theme.Id} resolves every token{(missing.Count > 0 ? " (missing " + string.Join(", ", missing) + ")" : "")}");
}
var evolved = EtherlightingEvolved.Native.Theme.ThemeTokens.Resolve("evolved");
var midnight = EtherlightingEvolved.Native.Theme.ThemeTokens.Resolve("midnight");
Check(evolved["surface.color"] is Avalonia.Media.Color { R: 0x19, G: 0x19, B: 0x1f, A: 255 } && evolved["line.color"] is Avalonia.Media.Color { R: 0xba, G: 0xac, B: 0xda, A: 0x1c },
    "plain and translucent colors are read");
Check(midnight["line.color"] is Avalonia.Media.Color { R: 0xe7, G: 0xef, B: 0xfc, A: 31 } && midnight["button.color"] is Avalonia.Media.Color { R: 0x14, G: 0x24, B: 0x3b },
    "var() and color-mix() resolve as in the browser");

// Port tiles: repaint only on a visible change, and the drawn light matches the reported color
PortTileChecks.Run(Check, output);

// Optional: start the simulator from this checkout and exercise the native pages and editing workflows against it
if (args.Contains("--service"))
{
    var page = Arg("--page-check");
    if (page is not (null or "Lighting" or "Effects" or "Automation" or "Profiles" or "Fans"))
        throw new ArgumentException("Unknown editing workflow.");
    ShellChecks.ExerciseDesktop(output, Check, page);
}

Console.WriteLine(failures.Count == 0 ? $"All checks passed. Images: {output}" : $"{failures.Count} check(s) failed.");
return failures.Count == 0 ? 0 : 1;
