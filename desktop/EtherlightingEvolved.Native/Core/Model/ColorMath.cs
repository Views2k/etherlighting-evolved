// Port of src/color.mjs: hex color parsing and formatting, HSV conversion, mixing and dimming.
using System.Globalization;
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

public static class ColorMath
{
    /// <summary>clamp(n, low = 0, high = 1): Math.min(high, Math.max(low, n)); NaN stays NaN.</summary>
    public static double Clamp(double n, double low = 0, double high = 1) => Math.Min(high, Math.Max(low, n));

    /// <summary>normalizeHex(value): "#RGB" or "#RRGGBB" (with or without "#") as uppercase "#RRGGBB", otherwise null.</summary>
    public static string? NormalizeHex(string? value)
    {
        var clean = Js.Trim(value ?? "null");
        if (clean.StartsWith('#')) clean = clean[1..];
        if (clean.Length == 3 && IsHex(clean))
            return "#" + string.Concat(clean.Select(c => new string(c, 2))).ToUpperInvariant();
        if (clean.Length == 6 && IsHex(clean)) return "#" + clean.ToUpperInvariant();
        return null;
    }

    /// <summary>normalizeHex(String(value)) for a JSON value.</summary>
    public static string? NormalizeHex(JsonNode? value) => NormalizeHex(Js.ToStr(value));

    static bool IsHex(string text)
    {
        foreach (var c in text) if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    /// <summary>hexToRgb(hex): integer channels; an invalid color reads as white.</summary>
    public static double[] HexToRgb(string? hex)
    {
        var normalized = NormalizeHex(hex);
        var n = int.Parse(normalized is null ? "FFFFFF" : normalized[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return [n >> 16, (n >> 8) & 255, n & 255];
    }

    public static double[] HexToRgb(JsonNode? hex) => HexToRgb(Js.ToStr(hex));

    /// <summary>rgbToHex(rgb): each channel clamped to 0–255, rounded and written as uppercase hex.</summary>
    public static string RgbToHex(IReadOnlyList<double> rgb)
    {
        var text = new System.Text.StringBuilder("#");
        foreach (var value in rgb)
        {
            var rounded = Js.Round(Clamp(value, 0, 255));
            text.Append(double.IsNaN(rounded) ? "NaN" : ((int)rounded).ToString("x", CultureInfo.InvariantCulture).PadLeft(2, '0'));
        }
        return text.ToString().ToUpperInvariant();
    }

    /// <summary>hsvToHex(h, s, v) with hue in degrees and saturation and value from 0 to 1.</summary>
    public static string HsvToHex(double h, double s, double v) => RgbToHex(HsvChannels(h, s, v));

    internal static double[] HsvChannels(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        s = Clamp(s);
        v = Clamp(v);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        double[] rgb = h < 60 ? [c, x, 0] : h < 120 ? [x, c, 0] : h < 180 ? [0, c, x] : h < 240 ? [0, x, c] : h < 300 ? [x, 0, c] : [c, 0, x];
        return [(rgb[0] + m) * 255, (rgb[1] + m) * 255, (rgb[2] + m) * 255];
    }

    /// <summary>hexToHsv(hex): [hue in degrees, saturation, value].</summary>
    public static double[] HexToHsv(string? hex)
    {
        var rgb = HexToRgb(hex);
        double r = rgb[0] / 255, g = rgb[1] / 255, b = rgb[2] / 255;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        var h = d == 0 ? 0 : max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return [(h * 60 + 360) % 360, max != 0 ? d / max : 0, max];
    }

    /// <summary>mix(a, b, t): a blend from a to b, rounded to a hex color.</summary>
    public static string Mix(string? a, string? b, double t)
    {
        var start = HexToRgb(a);
        var end = HexToRgb(b);
        var weight = Clamp(t);
        return RgbToHex([start[0] + (end[0] - start[0]) * weight, start[1] + (end[1] - start[1]) * weight, start[2] + (end[2] - start[2]) * weight]);
    }

    /// <summary>dim(hex, value): every channel scaled by the clamped value.</summary>
    public static string Dim(string? hex, double value)
    {
        var rgb = HexToRgb(hex);
        var scale = Clamp(value);
        return RgbToHex([rgb[0] * scale, rgb[1] * scale, rgb[2] * scale]);
    }
}
