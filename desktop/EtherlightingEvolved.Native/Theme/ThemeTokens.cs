using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace EtherlightingEvolved.Native.Theme;

public sealed record ThemeInfo(string Id, string Name, bool Light, string Description, IReadOnlyList<Color> Swatches);

/// <summary>
/// Reads the workspace palettes from the same themes.css and theme catalog the browser workspace uses, so both
/// show identical colors. Each CSS custom property becomes a resource named after it without the leading dashes:
/// colors become brushes (and "name.color" holds the Color), gradients become gradient brushes, layered radial
/// gradients become a brush list, and shadows become BoxShadows.
/// </summary>
public static partial class ThemeTokens
{
    public const string DefaultTheme = "evolved";
    static readonly Lazy<(List<(string Selector, Dictionary<string, string> Values)> Blocks, List<ThemeInfo> Themes)> Source = new(Load);

    public static IReadOnlyList<ThemeInfo> Themes => Source.Value.Themes;

    public static ThemeInfo Find(string? id) => Themes.FirstOrDefault(theme => theme.Id == id) ?? Themes.First(theme => theme.Id == DefaultTheme);

    /// <summary>The raw declarations for one theme, after the CSS cascade, with var() references still in place.</summary>
    public static Dictionary<string, string> Declarations(string theme)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (selector, values) in Source.Value.Blocks)
        {
            var applies = selector switch
            {
                ":root" => true,
                ":root[data-theme]:not([data-theme=\"evolved\"])" => theme != DefaultTheme,
                _ => selector == $":root[data-theme=\"{theme}\"]",
            };
            if (!applies) continue;
            foreach (var (name, value) in values) result[name] = value;
        }
        return result;
    }

    /// <summary>Every token of a theme converted to Avalonia values.</summary>
    public static Dictionary<string, object> Resolve(string theme)
    {
        var declarations = Declarations(theme);
        var resolver = new Resolver(declarations);
        var resources = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var name in declarations.Keys)
        {
            var value = resolver.Expand(name);
            if (value is null) continue;
            switch (Convert(value, resolver))
            {
                case Color color:
                    resources[name] = new ImmutableSolidColorBrush(color);
                    resources[name + ".color"] = color;
                    break;
                case { } converted:
                    resources[name] = converted;
                    break;
            }
        }
        return resources;
    }

    static object? Convert(string value, Resolver resolver)
    {
        value = value.Trim();
        if (TryColor(value, resolver, out var color)) return color;
        if (value.StartsWith("linear-gradient(", StringComparison.Ordinal)) return Linear(Inner(value), resolver);
        if (value.StartsWith("radial-gradient(", StringComparison.Ordinal))
            return SplitTop(value, ',').Select(part => Radial(Inner(part.Trim()), resolver)).Where(brush => brush is not null).Cast<IBrush>().ToArray();
        if (Regex.IsMatch(value, @"^-?\d") && value.Contains("px", StringComparison.Ordinal) && value.Contains('#')) return Shadows(value, resolver);
        return null;
    }

    static string Inner(string call)
    {
        var start = call.IndexOf('(');
        return call[(start + 1)..call.LastIndexOf(')')];
    }

    /// <summary>Splits on a separator outside parentheses and quotes.</summary>
    static List<string> SplitTop(string value, char separator)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        char quote = '\0';
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == separator && depth == 0)
            {
                parts.Add(value[start..i]);
                start = i + 1;
            }
        }
        parts.Add(value[start..]);
        return parts;
    }

    public static bool TryColor(string value, out Color color) => TryColor(value, null, out color);

    static bool TryColor(string value, Resolver? resolver, out Color color)
    {
        value = value.Trim();
        color = default;
        if (value == "transparent") { color = Colors.Transparent; return true; }
        if (value == "currentColor" && resolver?.Expand("text") is { } text) return TryColor(text, resolver, out color);
        if (value.StartsWith('#'))
        {
            var hex = value[1..];
            if (hex.Length is 3 or 4) hex = string.Concat(hex.Select(c => new string(c, 2)));
            if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits)) return false;
            color = hex.Length == 6
                ? Color.FromRgb((byte)(bits >> 16), (byte)(bits >> 8), (byte)bits)
                : Color.FromArgb((byte)bits, (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8));
            return true;
        }
        if (value.StartsWith("color-mix(", StringComparison.Ordinal) && resolver is not null)
        {
            var parts = SplitTop(Inner(value), ',').Select(part => part.Trim()).ToList();
            if (parts.Count != 3) return false;
            var (first, firstShare) = Share(parts[1]);
            var (second, secondShare) = Share(parts[2]);
            if (!TryColor(first, resolver, out var a) || !TryColor(second, resolver, out var b)) return false;
            var p = firstShare ?? (secondShare is { } s ? 1 - s : 0.5);
            color = Mix(a, b, p);
            return true;
        }
        if ((value.StartsWith("rgb(", StringComparison.Ordinal) || value.StartsWith("rgba(", StringComparison.Ordinal)))
        {
            var numbers = Regex.Matches(Inner(value), @"[\d.]+%?").Select(match => match.Value).ToList();
            if (numbers.Count < 3) return false;
            double Channel(string text) => text.EndsWith('%') ? double.Parse(text[..^1], CultureInfo.InvariantCulture) * 2.55 : double.Parse(text, CultureInfo.InvariantCulture);
            var alpha = numbers.Count > 3 ? (numbers[3].EndsWith('%') ? double.Parse(numbers[3][..^1], CultureInfo.InvariantCulture) / 100 : double.Parse(numbers[3], CultureInfo.InvariantCulture)) : 1;
            color = Color.FromArgb((byte)Math.Round(alpha * 255), (byte)Channel(numbers[0]), (byte)Channel(numbers[1]), (byte)Channel(numbers[2]));
            return true;
        }
        return false;
    }

    static (string Color, double? Share) Share(string part)
    {
        var match = Regex.Match(part, @"^(.*?)\s+([\d.]+)%$");
        return match.Success ? (match.Groups[1].Value, double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) / 100) : (part, null);
    }

    /// <summary>CSS color-mix in sRGB: premultiplied interpolation, so mixing with transparent only lowers alpha.</summary>
    public static Color Mix(Color a, Color b, double p)
    {
        double aa = a.A / 255.0 * p, ba = b.A / 255.0 * (1 - p), alpha = aa + ba;
        if (alpha <= 0) return Colors.Transparent;
        byte Channel(byte x, byte y) => (byte)Math.Round((x * aa + y * ba) / alpha);
        return Color.FromArgb((byte)Math.Round(alpha * 255), Channel(a.R, b.R), Channel(a.G, b.G), Channel(a.B, b.B));
    }

    static IBrush? Linear(string inner, Resolver resolver)
    {
        var parts = SplitTop(inner, ',').Select(part => part.Trim()).ToList();
        var angle = 180.0;
        if (parts.Count > 0 && parts[0].EndsWith("deg", StringComparison.Ordinal))
        {
            angle = double.Parse(parts[0][..^3], CultureInfo.InvariantCulture);
            parts.RemoveAt(0);
        }
        var stops = Stops(parts, resolver);
        if (stops.Count == 0) return null;
        // CSS angles point up at 0deg and turn clockwise. The line spans the box corners, as in CSS for a square box.
        var radians = angle * Math.PI / 180;
        double dx = Math.Sin(radians), dy = -Math.Cos(radians), half = 0.5 * (Math.Abs(dx) + Math.Abs(dy));
        return new ImmutableLinearGradientBrush(stops,
            startPoint: new RelativePoint(0.5 - dx * half, 0.5 - dy * half, RelativeUnit.Relative),
            endPoint: new RelativePoint(0.5 + dx * half, 0.5 + dy * half, RelativeUnit.Relative));
    }

    static IBrush? Radial(string inner, Resolver resolver)
    {
        var parts = SplitTop(inner, ',').Select(part => part.Trim()).ToList();
        var center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        if (parts.Count > 0 && parts[0].Contains(" at ", StringComparison.Ordinal))
        {
            var position = Regex.Matches(parts[0], @"(-?[\d.]+)%").Select(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) / 100).ToList();
            if (position.Count == 2) center = new RelativePoint(position[0], position[1], RelativeUnit.Relative);
            parts.RemoveAt(0);
        }
        var stops = Stops(parts, resolver);
        if (stops.Count == 0) return null;
        // An ellipse from a corner reaches the farthest corner in CSS; a radius of about 1.1 of the box matches it closely.
        return new ImmutableRadialGradientBrush(stops, center: center, gradientOrigin: center,
            radiusX: new RelativeScalar(1.1, RelativeUnit.Relative), radiusY: new RelativeScalar(1.1, RelativeUnit.Relative));
    }

    static List<ImmutableGradientStop> Stops(List<string> parts, Resolver resolver)
    {
        var stops = new List<(Color Color, double? Offset)>();
        foreach (var part in parts)
        {
            var match = Regex.Match(part, @"^(.*?)(?:\s+(-?[\d.]+)%)?$");
            if (!TryColor(match.Groups[1].Value, resolver, out var color)) continue;
            stops.Add((color, match.Groups[2].Success ? double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) / 100 : null));
        }
        var result = new List<ImmutableGradientStop>();
        for (var i = 0; i < stops.Count; i++)
        {
            var offset = stops[i].Offset ?? (i == 0 ? 0 : i == stops.Count - 1 ? 1 : Interpolate(stops, i));
            result.Add(new ImmutableGradientStop(offset, stops[i].Color));
        }
        return result;
    }

    static double Interpolate(List<(Color Color, double? Offset)> stops, int index)
    {
        int before = index - 1, after = index + 1;
        while (before > 0 && stops[before].Offset is null) before--;
        while (after < stops.Count - 1 && stops[after].Offset is null) after++;
        var start = stops[before].Offset ?? 0;
        var end = stops[after].Offset ?? 1;
        return start + (end - start) * (index - before) / (after - before);
    }

    static BoxShadows Shadows(string value, Resolver resolver)
    {
        var shadows = new List<BoxShadow>();
        foreach (var part in SplitTop(value, ','))
        {
            var tokens = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            var inset = tokens.Remove("inset");
            var colorText = tokens.LastOrDefault(token => !token.EndsWith("px", StringComparison.Ordinal) && token != "0");
            if (colorText is null || !TryColor(colorText, resolver, out var color)) continue;
            var numbers = tokens.Where(token => token != colorText).Select(token => double.Parse(token.Replace("px", ""), CultureInfo.InvariantCulture)).ToList();
            shadows.Add(new BoxShadow
            {
                IsInset = inset, OffsetX = numbers.ElementAtOrDefault(0), OffsetY = numbers.ElementAtOrDefault(1),
                Blur = numbers.ElementAtOrDefault(2), Spread = numbers.ElementAtOrDefault(3), Color = color,
            });
        }
        return shadows.Count == 0 ? default : new BoxShadows(shadows[0], shadows.Skip(1).ToArray());
    }

    sealed class Resolver(Dictionary<string, string> declarations)
    {
        readonly Dictionary<string, string?> done = new(StringComparer.Ordinal);
        readonly HashSet<string> active = new(StringComparer.Ordinal);

        public string? Expand(string name)
        {
            if (done.TryGetValue(name, out var cached)) return cached;
            if (!declarations.TryGetValue(name, out var raw) || !active.Add(name)) return null;
            var expanded = Substitute(raw);
            active.Remove(name);
            done[name] = expanded;
            return expanded;
        }

        string? Substitute(string value)
        {
            var builder = new StringBuilder();
            var index = 0;
            while (true)
            {
                var start = value.IndexOf("var(", index, StringComparison.Ordinal);
                if (start < 0) { builder.Append(value, index, value.Length - index); break; }
                builder.Append(value, index, start - index);
                int depth = 0, end = start + 3;
                for (; end < value.Length; end++)
                {
                    if (value[end] == '(') depth++;
                    else if (value[end] == ')' && --depth == 0) break;
                }
                var inner = value[(start + 4)..end];
                var comma = SplitTop(inner, ',');
                var name = comma[0].Trim().TrimStart('-');
                var replacement = Expand(name) ?? (comma.Count > 1 ? Substitute(string.Join(",", comma.Skip(1)).Trim()) : null);
                if (replacement is null) return null;
                builder.Append(replacement);
                index = end + 1;
            }
            return builder.ToString();
        }
    }

    static (List<(string, Dictionary<string, string>)>, List<ThemeInfo>) Load()
    {
        var css = Regex.Replace(Read("themes.css"), @"/\*.*?\*/", "", RegexOptions.Singleline);
        var blocks = new List<(string, Dictionary<string, string>)>();
        foreach (Match block in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var declaration in SplitTop(block.Groups[2].Value, ';'))
            {
                var colon = declaration.IndexOf(':');
                if (colon < 0) continue;
                var name = declaration[..colon].Trim();
                if (name.StartsWith("--", StringComparison.Ordinal)) values[name[2..]] = declaration[(colon + 1)..].Trim();
            }
            blocks.Add((block.Groups[1].Value.Trim(), values));
        }
        var themes = new List<ThemeInfo>();
        foreach (Match entry in Regex.Matches(Read("theme-catalog.mjs"), @"Object\.freeze\((\{""id"".*?\})\)"))
        {
            using var document = JsonDocument.Parse(entry.Groups[1].Value);
            var root = document.RootElement;
            var swatches = root.GetProperty("swatches").EnumerateArray().Select(item => TryColor(item.GetString() ?? "", out var c) ? c : Colors.Gray).ToList();
            themes.Add(new ThemeInfo(root.GetProperty("id").GetString()!, root.GetProperty("name").GetString()!,
                root.GetProperty("mode").GetString() == "light", root.GetProperty("description").GetString() ?? "", swatches));
        }
        return (blocks, themes);
    }

    static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"The {name} resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
