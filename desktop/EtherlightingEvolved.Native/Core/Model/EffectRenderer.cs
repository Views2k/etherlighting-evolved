// Port of preview/effect-renderer.mjs: the effect renderer the workspace previews use. It is the renderer of
// src/effects.mjs (Effects.cs) built with color math that keeps fractional RGB channels instead of hex strings.
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

/// <summary>The RGB color math of preview/effect-renderer.mjs: results are unrounded channel arrays.</summary>
public sealed class RgbColorMath : IEffectColorMath
{
    public static readonly RgbColorMath Instance = new();

    public object? Mix(object? a, object? b, double t) => EffectRenderer.Mix(a, b, t);

    public object? Dim(object? color, double value) => EffectRenderer.Dim(color, value);

    public double[] HexToRgb(object? color) => EffectRenderer.Rgb(color);

    public object? RgbToHex(double[] rgb) => EffectRenderer.Rgb(rgb);

    public object? HsvToHex(double h, double s, double v) => EffectRenderer.Hsv(h, s, v);
}

public static class EffectRenderer
{
    /// <summary>rgb(color): channel arrays clamped to 0–255; hex strings parsed (invalid ones read as white).</summary>
    public static double[] Rgb(object? color) => color switch
    {
        double[] channels => channels.Select(channel => ColorMath.Clamp(channel, 0, 255)).ToArray(),
        IReadOnlyList<double> channels => channels.Select(channel => ColorMath.Clamp(channel, 0, 255)).ToArray(),
        JsonArray array => array.Select(item => ColorMath.Clamp(item is null ? 0 : Js.ToNumber(item), 0, 255)).ToArray(),
        JsonNode node => Rgb(Effects.Paint(node)),
        _ => ColorMath.HexToRgb(Effects.ColorText(color)),
    };

    /// <summary>dim(color, amount): each channel scaled by the clamped amount.</summary>
    public static double[] Dim(object? color, double amount)
    {
        var scale = ColorMath.Clamp(amount);
        return Rgb(color).Select(channel => channel * scale).ToArray();
    }

    /// <summary>mix(first, second, amount): a blend that keeps fractional channels.</summary>
    public static double[] Mix(object? first, object? second, double amount)
    {
        var start = Rgb(first);
        var end = Rgb(second);
        var weight = ColorMath.Clamp(amount);
        var result = new double[start.Length];
        for (var index = 0; index < start.Length; index++)
            result[index] = start[index] + ((index < end.Length ? end[index] : double.NaN) - start[index]) * weight;
        return result;
    }

    /// <summary>hsv(hue, saturation, value) (also exported as hsvToRgb): unrounded channels.</summary>
    public static double[] Hsv(double hue, double saturation, double value) => ColorMath.HsvChannels(hue, saturation, value);

    public static double[] HsvToRgb(double hue, double saturation, double value) => Hsv(hue, saturation, value);

    /// <summary>The renderer behind this module's exports.</summary>
    public static readonly Effects.Renderer Instance = Effects.CreateEffectRenderer(RgbColorMath.Instance);

    public static JsonArray EffectList => Instance.EffectList;
    public static JsonArray Library => Instance.Library;
    public static void SetEffectCatalog(JsonNode? catalog) => Instance.SetEffectCatalog(catalog);
    public static JsonObject EffectById(JsonNode? id) => Instance.EffectById(id);
    public static JsonObject EffectById(string? id) => Instance.EffectById(id);
    public static bool SynchronizedBreathing(JsonNode? config) => Instance.SynchronizedBreathing(config);
    public static double BreathingPeriod(JsonNode? pace) => Instance.BreathingPeriod(pace);
    public static JsonObject? SpeedParam(JsonNode? effect, JsonNode? parameters = null) => Instance.SpeedParam(effect, parameters);
    public static double SpeedSliderValue(JsonNode? param, double value) => Instance.SpeedSliderValue(param, value);
    public static double SpeedParamValue(JsonNode? param, JsonNode? value) => Instance.SpeedParamValue(param, value);
    public static string SpeedText(JsonNode? param, JsonNode? value) => Instance.SpeedText(param, value);
    public static JsonObject DefaultConfig(JsonNode? id) => Instance.DefaultConfig(id);
    public static JsonObject DefaultConfig(string id = "gradient") => Instance.DefaultConfig(id);
    public static JsonObject EffectConfigForSelection(string id, JsonNode? state) => Instance.EffectConfigForSelection(id, state);
    public static JsonNode? ShareBreathingPace(JsonNode? zones, JsonNode? config) => Instance.ShareBreathingPace(zones, config);
    public static JsonObject ParamsAfterChange(JsonNode? config, string key, JsonNode? value, JsonNode? zones = null) => Instance.ParamsAfterChange(config, key, value, zones);
    public static object? RandomColor(string key, double phase) => Instance.RandomColor(key, phase);
    public static JsonObject ResolvedColorParams(JsonNode? config, double time, double? phase = null) => Instance.ResolvedColorParams(config, time, phase);
    public static double PreviewPhase(EffectClock clock, JsonNode? config, double time) => Instance.PreviewPhase(clock, config, time);
    public static double PreviewColorPhase(EffectClock clock, JsonNode? config, double time) => Instance.PreviewColorPhase(clock, config, time);
    public static double ActivityPeak(IEnumerable<JsonNode?> facts, bool includeUplinks = false) => Instance.ActivityPeak(facts, includeUplinks);
    public static double ActivityPeak(JsonArray facts, bool includeUplinks = false) => Instance.ActivityPeak(facts, includeUplinks);
    public static JsonArray DataPreviewFacts(int count, double time) => Instance.DataPreviewFacts(count, time);

    /// <summary>effectColor: a channel array, or a parameter's hex string where the effect passes one through.</summary>
    public static object? EffectColor(JsonNode? config, double index, double count, double time, EffectFacts? facts = null) =>
        Instance.EffectColor(config, index, count, time, facts);
}
