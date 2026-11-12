// Port of preview/port-display.mjs: the display color of a port from its reported output, and previews of manual and
// master brightness changes. Colors are RGB channel arrays (0–255, fractional).
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

public static class PortDisplay
{
    static bool ValidRgb(JsonNode? value) => value is JsonArray array && array.Count == 3 && array.All(Js.IsFinite);

    static double Percent(double value) => Math.Max(0, Math.Min(100, value)) / 100;

    static double Percent(JsonNode? value) => Percent(Js.ToNumber(value));

    static double Curve(double? gamma) => gamma is { } value && double.IsFinite(value) && value > 0 ? value : 2.2;

    /// <summary>portDisplayRgb(port, gamma = 2.2): the reported display color, or the output color decoded once.</summary>
    public static double[] PortDisplayRgb(JsonNode? port, double? gamma = 2.2) => PortDisplayRgb(port, Js.Get(port, "mode"), gamma);

    /// <summary>portDisplayRgb for {...port, mode}.</summary>
    internal static double[] PortDisplayRgb(JsonNode? port, JsonNode? mode, double? gamma)
    {
        if (Js.Has(port, "display_rgb")) return ValidRgb(Js.Get(port, "display_rgb")) ? EffectRenderer.Rgb(Js.Get(port, "display_rgb")) : [0, 0, 0];
        if (Js.StrictEquals(mode, "off") || Js.StrictEquals(mode, "unmanaged") && !Js.Truthy(Js.Get(port, "rule"))) return [0, 0, 0];
        var preview = Js.Get(port, "preview");
        var encoded = preview ?? Js.Get(Js.Get(port, "output"), "color");
        var text = encoded is null ? "" : Js.ToStr(encoded);
        if (!(text.Length == 7 && text[0] == '#' && text.Skip(1).All(char.IsAsciiHexDigit))) return [0, 0, 0];
        var curve = Curve(gamma);
        var level = preview is null ? Percent(Js.Get(Js.Get(port, "output"), "level") is { } outputLevel ? Js.ToNumber(outputLevel) : 100) : 1;
        return EffectRenderer.Rgb(text).Select(channel => 255 * Math.Pow(channel / 255 * level, 1 / curve)).ToArray();
    }

    /// <summary>manualDisplayRgb(color, brightness, inheritedBrightness = 100): a fixed color at its own or the inherited level.</summary>
    public static double[] ManualDisplayRgb(object? color, JsonNode? brightness, double inheritedBrightness = 100) =>
        EffectRenderer.Dim(color is JsonNode node ? Effects.Paint(node) : color, brightness is null ? Percent(inheritedBrightness) : Percent(brightness));

    /// <summary>
    /// masterDisplayRgb(port, brightness, previousBrightness, rules = [], gamma = 2.2, date = new Date()): the port's
    /// color after a master brightness change, keeping explicit fixed-color and rule levels.
    /// </summary>
    public static double[] MasterDisplayRgb(JsonNode? port, double brightness, double previousBrightness, JsonNode? rules = null, double? gamma = 2.2) =>
        MasterDisplayRgb(port, brightness, previousBrightness, rules, gamma, DateTime.Now);

    public static double[] MasterDisplayRgb(JsonNode? port, double brightness, double previousBrightness, JsonNode? rules, double? gamma, DateTime? date) =>
        MasterDisplayRgb(port, Js.Get(port, "mode"), brightness, previousBrightness, rules, gamma, date);

    /// <summary>masterDisplayRgb for {...port, mode}.</summary>
    internal static double[] MasterDisplayRgb(JsonNode? port, JsonNode? mode, double brightness, double previousBrightness, JsonNode? rules, double? gamma, DateTime? date)
    {
        var shown = PortDisplayRgb(port, mode, gamma);
        var sfp = Js.StrictEquals(mode, "color") || Js.StrictEquals(mode, "off") || Js.StrictEquals(mode, "unmanaged");
        var rule = sfp ? Rules.MatchingSfpRule(rules, port, mode, date)
            : Rules.MatchingRule(rules, Js.Get(port, "port"), key => Js.Get(Js.Get(port, "facts"), key), date);
        if (Js.Get(Js.Get(port, "override"), "brightness") is not null || Js.Get(Js.Get(rule, "look"), "brightness") is not null) return shown;
        if (sfp && (Js.StrictEquals(mode, "off") || Js.StrictEquals(mode, "unmanaged") && rule is null
                    || Js.Has(port, "display_rgb") && Js.Get(port, "display_rgb") is null)) return shown;
        if (previousBrightness > 0) return EffectRenderer.Rgb(shown.Select(channel => channel * brightness / previousBrightness).ToArray());
        var scale = sfp ? Percent(Js.Get(port, "brightness") is { } level ? Js.ToNumber(level) : 100) * Percent(Js.Get(port, "color_brightness") is { } colorLevel ? Js.ToNumber(colorLevel) : 100) : 1;
        var look = Js.Get(rule, "look");
        var color = !Js.StrictEquals(Js.Get(look, "style"), "effect") && Js.Truthy(Js.Get(look, "color")) ? Js.Get(look, "color") : Js.Get(port, "color");
        return EffectRenderer.Dim(Js.Truthy(color) ? Effects.Paint(color) : "#000000", Percent(brightness) * scale);
    }
}
