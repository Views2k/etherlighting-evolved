// Port of src/effects.mjs: the client-side effect simulation (createEffectRenderer) and the module's default renderer,
// which works with hex colors. preview/effect-renderer.mjs (EffectRenderer.cs) creates a second renderer that keeps
// fractional RGB channels.
using System.Text;
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

/// <summary>
/// The color functions a renderer is built with (the colorMath argument of createEffectRenderer). A color value is a
/// hex string or an RGB channel array (double[]); null stands for undefined.
/// </summary>
public interface IEffectColorMath
{
    object? Mix(object? a, object? b, double t);

    object? Dim(object? color, double value);

    double[] HexToRgb(object? color);

    object? RgbToHex(double[] rgb);

    object? HsvToHex(double h, double s, double v);
}

/// <summary>The color math of src/color.mjs applied to renderer color values: every result is an uppercase hex string.</summary>
public sealed class HexColorMath : IEffectColorMath
{
    public static readonly HexColorMath Instance = new();

    public object? Mix(object? a, object? b, double t)
    {
        var start = HexToRgb(a);
        var end = HexToRgb(b);
        var weight = ColorMath.Clamp(t);
        return ColorMath.RgbToHex([start[0] + (end[0] - start[0]) * weight, start[1] + (end[1] - start[1]) * weight, start[2] + (end[2] - start[2]) * weight]);
    }

    public object? Dim(object? color, double value)
    {
        var rgb = HexToRgb(color);
        var scale = ColorMath.Clamp(value);
        return ColorMath.RgbToHex([rgb[0] * scale, rgb[1] * scale, rgb[2] * scale]);
    }

    public double[] HexToRgb(object? color) => ColorMath.HexToRgb(Effects.ColorText(color));

    public object? RgbToHex(double[] rgb) => ColorMath.RgbToHex(rgb);

    public object? HsvToHex(double h, double s, double v) => ColorMath.HsvToHex(h, s, v);
}

/// <summary>The motion or color clock of one preview (the clock objects previewPhase and previewColorPhase keep).</summary>
public sealed class EffectClock
{
    public double? Rate { get; set; }

    public double? Offset { get; set; }
}

/// <summary>A fade from the last shown color, kept by the Traffic Trails animation state.</summary>
public sealed record EffectFade(object? Color, double Start);

/// <summary>The per-port animation state data effects keep between frames (facts.animationState).</summary>
public sealed class EffectAnimationState
{
    public double? Last { get; set; }
    public bool? Online { get; set; }
    public object? Shown { get; set; }
    public EffectFade? Fade { get; set; }
    public double? Load { get; set; }
    public double? Level { get; set; }
    public double? Target { get; set; }
    public double? Speed { get; set; }
    public double? Low { get; set; }
    public double? High { get; set; }
    public double? TargetLow { get; set; }
    public double? TargetHigh { get; set; }

    /// <summary>structuredClone(animationState).</summary>
    public EffectAnimationState Clone() => (EffectAnimationState)MemberwiseClone();
}

/// <summary>
/// The facts argument of effectColor: the port's reported facts (link, uplink, traffic, speed, poe, poe_power) and the
/// values the previews add to them.
/// </summary>
public sealed class EffectFacts
{
    public EffectFacts() { }

    public EffectFacts(JsonNode? values) => Values = values;

    /// <summary>The port's reported facts object.</summary>
    public JsonNode? Values { get; init; }
    public JsonObject? ResolvedParams { get; init; }
    public double? Phase { get; init; }
    public double? ColorPhase { get; init; }
    public double? Hour { get; init; }
    public double? PeakTraffic { get; init; }
    public EffectAnimationState? AnimationState { get; init; }

    internal JsonNode? Fact(string key) => Js.Get(Values, key);
}

public static class Effects
{
    /// <summary>createEffectRenderer(colorMath): a renderer with its own catalog state. The default is hex color math.</summary>
    public static Renderer CreateEffectRenderer(IEffectColorMath? colorMath = null) => new(colorMath ?? HexColorMath.Instance);

    /// <summary>The module's renderer (the destructured exports of src/effects.mjs).</summary>
    public static readonly Renderer Default = CreateEffectRenderer();

    public static JsonArray EffectList => Default.EffectList;
    public static JsonArray Library => Default.Library;
    public static void SetEffectCatalog(JsonNode? catalog) => Default.SetEffectCatalog(catalog);
    public static JsonObject EffectById(JsonNode? id) => Default.EffectById(id);
    public static JsonObject EffectById(string? id) => Default.EffectById(id);
    public static bool SynchronizedBreathing(JsonNode? config) => Default.SynchronizedBreathing(config);
    public static double BreathingPeriod(JsonNode? pace) => Default.BreathingPeriod(pace);
    public static JsonObject? SpeedParam(JsonNode? effect, JsonNode? parameters = null) => Default.SpeedParam(effect, parameters);
    public static double SpeedSliderValue(JsonNode? param, double value) => Default.SpeedSliderValue(param, value);
    public static double SpeedParamValue(JsonNode? param, JsonNode? value) => Default.SpeedParamValue(param, value);
    public static string SpeedText(JsonNode? param, JsonNode? value) => Default.SpeedText(param, value);
    public static JsonObject DefaultConfig(JsonNode? id) => Default.DefaultConfig(id);
    public static JsonObject DefaultConfig(string id = "gradient") => Default.DefaultConfig(id);
    public static JsonObject EffectConfigForSelection(string id, JsonNode? state) => Default.EffectConfigForSelection(id, state);
    public static JsonNode? ShareBreathingPace(JsonNode? zones, JsonNode? config) => Default.ShareBreathingPace(zones, config);
    public static JsonObject ParamsAfterChange(JsonNode? config, string key, JsonNode? value, JsonNode? zones = null) => Default.ParamsAfterChange(config, key, value, zones);
    public static object? RandomColor(string key, double phase) => Default.RandomColor(key, phase);
    public static JsonObject ResolvedColorParams(JsonNode? config, double time, double? phase = null) => Default.ResolvedColorParams(config, time, phase);
    public static double PreviewPhase(EffectClock clock, JsonNode? config, double time) => Default.PreviewPhase(clock, config, time);
    public static double PreviewColorPhase(EffectClock clock, JsonNode? config, double time) => Default.PreviewColorPhase(clock, config, time);
    public static double ActivityPeak(IEnumerable<JsonNode?> facts, bool includeUplinks = false) => Default.ActivityPeak(facts, includeUplinks);
    public static double ActivityPeak(JsonArray facts, bool includeUplinks = false) => Default.ActivityPeak(facts, includeUplinks);
    public static JsonArray DataPreviewFacts(int count, double time) => Default.DataPreviewFacts(count, time);
    public static object? EffectColor(JsonNode? config, double index, double count, double time, EffectFacts? facts = null) =>
        Default.EffectColor(config, index, count, time, facts);

    /// <summary>String(color) for a renderer color value, as hexToRgb reads it.</summary>
    internal static string ColorText(object? color) => color switch
    {
        null => "undefined",
        string text => text,
        double[] channels => string.Join(",", channels.Select(Js.NumberToString)),
        JsonNode node => Js.ToStr(node),
        _ => color.ToString() ?? "",
    };

    /// <summary>A renderer color value read from a JSON value.</summary>
    public static object? Paint(JsonNode? node)
    {
        switch (node)
        {
            case null: return null;
            case JsonArray array: return array.Select(item => item is null ? 0 : Js.ToNumber(item)).ToArray();
            case JsonObject: return "[object Object]";
        }
        return Js.Str(node) ?? Js.ToStr(node);
    }

    /// <summary>A renderer color value written as a JSON value.</summary>
    public static JsonNode? PaintNode(object? color) => color switch
    {
        null => null,
        string text => JsonValue.Create(text),
        double[] channels => Js.NumberArray(channels),
        JsonNode node => node.DeepClone(),
        _ => JsonValue.Create(color.ToString()),
    };

    /// <summary>The renderer returned by createEffectRenderer.</summary>
    public sealed class Renderer
    {
        static readonly Dictionary<string, string[]> Presentation = new(StringComparer.Ordinal)
        {
            ["static"] = ["Ambient", "sun", "#E8BC78", "#E8BC78", "Display a single color."],
            ["breathing"] = ["Ambient", "wind", "#C4A2F5", "#7453A6", "Fade a color in and out."],
            ["color_cycle"] = ["Ambient", "blend", "#EDA7B8", "#B8A2F5", "Cycle through the color wheel."],
            ["rainbow"] = ["Dynamic", "rainbow", "#F68C9F", "#8D91ED", "Move a rainbow across the group."],
            ["comet"] = ["Dynamic", "comet", "#B7B7FF", "#6F71C4", "Move a light with a fading trail."],
            ["scanner"] = ["Dynamic", "scan", "#F5787B", "#B24A67", "Sweep a light back and forth."],
            ["marquee"] = ["Dynamic", "arrow", "#F0BE75", "#F0746B", "Move evenly spaced lights."],
            ["starry_night"] = ["Ambient", "sparkles", "#E8DBAB", "#9A874D", "Fade individual lights in and out."],
            ["fire"] = ["Ambient", "flame", "#FFB367", "#E56846", "Simulate flickering flames."],
            ["police"] = ["Dynamic", "zap", "#F5787B", "#6A95EE", "Alternate between two colors."],
            ["strobe"] = ["Dynamic", "zap", "#E4E7EF", "#8998AD", "Flash ports at a set interval. Can affect people who are sensitive to flashing light."],
            ["gradient"] = ["Ambient", "blend", "#79DCC0", "#A38DE0", "Blend two colors across the group."],
            ["ripple"] = ["Dynamic", "water", "#8CDDEB", "#5784BA", "Expand waves across the group."],
            ["lightning"] = ["Dynamic", "zap", "#C9D9FC", "#7381A8", "Flash over a background color."],
            ["daylight"] = ["Ambient", "sun", "#ECC896", "#B18BC0", "Change color with the time of day."],
            ["traffic"] = ["Data", "activity", "#7FA3EE", "#EE8E8A", "Follow device traffic with automatic link-speed scaling."],
            ["aurora"] = ["Ambient", "aurora", "#6ED8B2", "#A38DDF", "Drift overlapping curtains of color."],
            ["lava_lamp"] = ["Ambient", "blend", "#ED988B", "#BA86D7", "Let soft color shapes merge and separate."],
            ["plasma"] = ["Dynamic", "wave", "#AD8FDF", "#E883AA", "Blend intersecting waves of color."],
            ["ocean_swell"] = ["Ambient", "water", "#65CDBE", "#5B90D3", "Roll gentle waves across the ports."],
            ["eclipse"] = ["Ambient", "sun", "#D6B88E", "#71658F", "Move a soft shadow through the base color."],
            ["meteor_shower"] = ["Dynamic", "comet", "#A5CAF0", "#9C90DA", "Send overlapping streaks with fading trails."],
            ["counterflow"] = ["Dynamic", "arrow", "#71CEC4", "#DB91B5", "Pass two color streams in opposite directions."],
            ["color_dissolve"] = ["Ambient", "blend", "#CBA2D8", "#7BCDB9", "Fade into a new color in a shuffled order."],
            ["ink_spread"] = ["Dynamic", "water", "#9A9CDC", "#EDAC95", "Let color spread outward from changing points."],
            ["alternating_fade"] = ["Ambient", "layers", "#8EB4E3", "#DCA0A6", "Crossfade the odd and even ports."],
            ["color_tide"] = ["Dynamic", "water", "#73CECA", "#8597D9", "Fill the row with color, then let it recede."],
            ["palette_morph"] = ["Ambient", "rainbow", "#DDA9A2", "#91BDCC", "Transform a flowing multicolor gradient."],
            ["ember_bed"] = ["Ambient", "flame", "#EFA76E", "#B55D64", "Let warm pockets of light glow and cool."],
            ["fireflies"] = ["Ambient", "sparkles", "#DACB8D", "#8CA985", "Follow small lights as they wander and pulse."],
            ["traffic_trails"] = ["Data", "activity", "#7CAFD9", "#B5D49C", "Animate fading trails with port activity."],
            ["activity_glow"] = ["Data", "activity", "#72E0BD", "#547FAA", "Brighten busy ports and fade as traffic settles."],
            ["poe_usage"] = ["Data", "zap", "#4DA6FF", "#EF78C7", "Follow the power each port supplies."],
        };

        static readonly Dictionary<string, string> Categories = new(StringComparer.Ordinal)
        {
            ["static"] = "Ambient", ["animated"] = "Dynamic", ["time"] = "Ambient", ["data"] = "Data",
        };

        static readonly Dictionary<string, (double, int[])[]> FireStops = new(StringComparer.Ordinal)
        {
            ["fire"] = [(0, [20, 0, 0]), (.35, [200, 10, 0]), (.7, [255, 90, 0]), (1, [255, 200, 60])],
            ["ice"] = [(0, [0, 0, 30]), (.4, [0, 40, 200]), (.75, [0, 160, 255]), (1, [200, 240, 255])],
            ["toxic"] = [(0, [0, 20, 0]), (.4, [0, 140, 10]), (.75, [90, 255, 0]), (1, [220, 255, 120])],
            ["plasma"] = [(0, [30, 0, 40]), (.4, [150, 0, 200]), (.75, [255, 0, 140]), (1, [255, 180, 240])],
        };

        static readonly Dictionary<string, (double, int[])[]> DaylightStops = new(StringComparer.Ordinal)
        {
            ["natural"] = [(0, [40, 18, 70]), (5.5, [40, 18, 70]), (6.5, [255, 120, 40]), (8, [255, 200, 140]), (12, [235, 245, 255]), (17, [255, 210, 160]),
                (19, [255, 110, 60]), (20.5, [160, 50, 120]), (22, [40, 18, 70]), (24, [40, 18, 70])],
            ["warm"] = [(0, [120, 30, 0]), (6, [120, 30, 0]), (8, [255, 170, 90]), (13, [255, 220, 170]), (19, [255, 140, 60]), (22, [120, 30, 0]), (24, [120, 30, 0])],
            ["vivid"] = [(0, [20, 0, 120]), (6, [255, 60, 0]), (9, [255, 220, 0]), (13, [0, 200, 255]), (18, [255, 0, 120]), (21, [120, 0, 255]), (24, [20, 0, 120])],
        };

        static readonly string[] PoeClasses = ["poe", "poe+", "poe++", "poe+++"];

        readonly IEffectColorMath math;
        readonly Dictionary<string, (double At, object? Color)[]> firePalettes, daylightPalettes;
        readonly Dictionary<string, uint> colorSeeds = new(StringComparer.Ordinal);
        JsonNode currentCatalog;

        internal Renderer(IEffectColorMath colorMath)
        {
            math = colorMath;
            currentCatalog = EffectCatalog.Catalog;
            foreach (var effect in Js.Objects(EffectCatalog.Catalog))
            {
                var presented = PresentEffect((JsonObject)effect.DeepClone());
                EffectList.Add(presented);
                Library.Add(presented.DeepClone());
            }
            firePalettes = FireStops.ToDictionary(pair => pair.Key, pair => pair.Value.Select(stop => (stop.Item1, math.RgbToHex(stop.Item2.Select(c => (double)c).ToArray()))).ToArray());
            daylightPalettes = DaylightStops.ToDictionary(pair => pair.Key, pair => pair.Value.Select(stop => (stop.Item1, math.RgbToHex(stop.Item2.Select(c => (double)c).ToArray()))).ToArray());
        }

        /// <summary>EFFECTS: the presented catalog. Entries are shared; copy one before changing it.</summary>
        public JsonArray EffectList { get; } = [];

        /// <summary>LIBRARY: the same effects as EffectList.</summary>
        public JsonArray Library { get; } = [];

        JsonObject PresentEffect(JsonObject effect)
        {
            var id = Js.Get(effect, "id");
            var idText = Js.ToStr(id);
            var custom = id is not null && Presentation.TryGetValue(idText, out var row) ? row : null;
            var defaults = Js.Objects(Js.Get(effect, "params"))
                .Where(param => Js.StrictEquals(param["type"], "color") && Js.ToStr(param["default"]) is var text && text.Length == 7 && text[0] == '#'
                                && text.Skip(1).All(char.IsAsciiHexDigit))
                .Select(param => Js.ToStr(param["default"])).ToList();
            var categoryKey = Js.ToStr(Js.Get(effect, "category"));
            var fallback = new[]
            {
                Categories.TryGetValue(categoryKey, out var category) ? category : "Other", "blend",
                defaults.Count > 0 && defaults[0].Length > 0 ? defaults[0] : "#98A0AC",
                defaults.Count > 1 && defaults[1].Length > 0 ? defaults[1] : defaults.Count > 0 && defaults[0].Length > 0 ? defaults[0] : "#68717D",
                Js.Str(Js.Get(effect, "description")) ?? "",
            };
            var chosen = custom ?? fallback;
            var name = Js.Str(Js.Get(effect, "name")) is { } named ? ReplaceFirst(named, "Colour", "Color") : null;
            effect["name"] = name is not null ? JsonValue.Create(name) : Js.Clone(id);
            effect["category"] = chosen[0];
            effect["icon"] = chosen[1];
            effect["colors"] = new JsonArray(chosen[2], chosen[3]);
            effect["description"] = chosen[4];
            effect["supportsPreview"] = custom is not null;
            return effect;
        }

        static string ReplaceFirst(string text, string find, string replace)
        {
            var at = text.IndexOf(find, StringComparison.Ordinal);
            return at < 0 ? text : text[..at] + replace + text[(at + find.Length)..];
        }

        /// <summary>setEffectCatalog(catalog): replaces the effects with the engine's catalog.</summary>
        public void SetEffectCatalog(JsonNode? catalog)
        {
            if (ReferenceEquals(catalog, currentCatalog)) return;
            if (catalog is not JsonArray list || list.Count == 0
                || list.Any(entry => Js.Str(Js.Get(entry, "id")) is null || Js.Get(entry, "params") is not JsonArray))
                throw new ArgumentException("Invalid effect catalog");
            var effects = list.Select(entry => PresentEffect((JsonObject)entry!.DeepClone())).ToList();
            EffectList.Clear();
            Library.Clear();
            foreach (var effect in effects)
            {
                EffectList.Add(effect);
                Library.Add(effect.DeepClone());
            }
            currentCatalog = catalog;
        }

        /// <summary>effectById(id): the effect with the ID, or the first effect.</summary>
        public JsonObject EffectById(JsonNode? id) =>
            Js.Objects(EffectList).FirstOrDefault(effect => Js.StrictEquals(effect["id"], id)) ?? (JsonObject)EffectList[0]!;

        public JsonObject EffectById(string? id) =>
            Js.Objects(EffectList).FirstOrDefault(effect => Js.StrictEquals(effect["id"], id)) ?? (JsonObject)EffectList[0]!;

        /// <summary>synchronizedBreathing(config): Breathing with synchronized playback.</summary>
        public bool SynchronizedBreathing(JsonNode? config) =>
            Js.StrictEquals(Js.Get(config, "id"), "breathing") && Js.StrictEquals(Js.Get(Js.Get(config, "params"), "playback"), "synchronized");

        /// <summary>breathingPeriod(pace): 3.08, 2 or 1 seconds for paces 1 to 3, otherwise 2.</summary>
        public double BreathingPeriod(JsonNode? pace) => Js.ToStr(pace) switch
        {
            "1" => 3.08,
            "2" => 2,
            "3" => 1,
            _ => 2,
        };

        static JsonObject? FindParam(JsonNode? effect, string key) =>
            Js.Objects(Js.Get(effect, "params")).FirstOrDefault(param => Js.StrictEquals(param["key"], key));

        /// <summary>speedParam(effect, params): the parameter the speed control edits, or null.</summary>
        public JsonObject? SpeedParam(JsonNode? effect, JsonNode? parameters = null)
        {
            if (Js.StrictEquals(Js.Get(effect, "id"), "breathing") && Js.StrictEquals(Js.Get(parameters, "playback"), "synchronized"))
                return FindParam(effect, "pace");
            return new[] { "speed", "period", "rate" }.Select(key => FindParam(effect, key)).FirstOrDefault(param => param is not null);
        }

        /// <summary>speedSliderValue(param, value): Period runs reversed so the right end is faster.</summary>
        public double SpeedSliderValue(JsonNode? param, double value) =>
            Js.StrictEquals(Js.Get(param, "key"), "period") ? Js.Arith(param, "min") + Js.Arith(param, "max") - value : value;

        /// <summary>speedParamValue(param, value): the stored value for a slider position, snapped to the step.</summary>
        public double SpeedParamValue(JsonNode? param, JsonNode? value)
        {
            var raw = SpeedSliderValue(param, Js.ToNumber(value));
            var step = Js.Truthy(Js.Get(param, "step")) ? Js.ToNumber(Js.Get(param, "step")) : .1;
            double min = Js.Arith(param, "min"), max = Js.Arith(param, "max");
            return Js.StringToNumber(Js.ToFixed(Math.Max(min, Math.Min(max, min + Js.Round((raw - min) / step) * step)), 6));
        }

        public double SpeedParamValue(JsonNode? param, double value) => SpeedParamValue(param, JsonValue.Create(value));

        /// <summary>speedText(param, value): the speed control's readout.</summary>
        public string SpeedText(JsonNode? param, JsonNode? value)
        {
            var key = Js.Get(param, "key");
            if (Js.StrictEquals(key, "pace"))
                return Js.ToStr(value) switch { "1" => "Slow", "2" => "Medium", "3" => "Fast", _ => "Medium" };
            var number = Js.NumberToString(Js.StringToNumber(Js.ToFixed(Js.ToNumber(value), 2)));
            if (Js.StrictEquals(key, "period")) return $"{number} s / cycle";
            if (Js.StrictEquals(key, "speed") && Js.StrictEquals(value, 0)) return "Still";
            var unit = Js.Get(param, "unit");
            return number + (Js.Truthy(unit) ? " " + Js.ToStr(unit) : "×");
        }

        public string SpeedText(JsonNode? param, double value) => SpeedText(param, JsonValue.Create(value));

        /// <summary>defaultConfig(id = 'gradient'): {id, params} with every parameter's default.</summary>
        public JsonObject DefaultConfig(JsonNode? id) => id is null ? DefaultConfig() : Defaults(EffectById(id));

        public JsonObject DefaultConfig(string id = "gradient") => Defaults(EffectById(id));

        static JsonObject Defaults(JsonObject effect)
        {
            var parameters = new JsonObject();
            foreach (var param in Js.Objects(effect["params"]))
                if (param.TryGetPropertyValue("default", out var value)) parameters[Js.ToStr(param["key"])] = Js.Clone(value);
            return new JsonObject { ["id"] = Js.Clone(effect["id"]), ["params"] = parameters };
        }

        /// <summary>effectConfigForSelection(id, state): defaults, with Breathing joining the shared synchronized pace when supported.</summary>
        public JsonObject EffectConfigForSelection(string id, JsonNode? state)
        {
            var config = DefaultConfig(id);
            var parameters = (JsonObject)config["params"]!;
            if (id == "breathing" && Js.IsTrue(Js.Get(Js.Get(state, "switch"), "native_breathing")) && parameters.ContainsKey("playback"))
            {
                parameters["playback"] = "synchronized";
                var shared = Js.Objects(Js.Get(state, "zones")).FirstOrDefault(zone => SynchronizedBreathing(zone["effect"]));
                parameters["pace"] = Js.Clone(Js.Get(Js.Get(shared?["effect"], "params"), "pace")) ?? 2;
            }
            return config;
        }

        /// <summary>shareBreathingPace(zones, config): synchronized Breathing groups take the config's pace.</summary>
        public JsonNode? ShareBreathingPace(JsonNode? zones, JsonNode? config)
        {
            if (!SynchronizedBreathing(config)) return zones;
            var pace = Js.Get(Js.Get(config, "params"), "pace");
            var result = new JsonArray();
            foreach (var zone in Js.Items(zones))
            {
                var copy = Js.Clone(zone);
                if (copy is JsonObject item && SynchronizedBreathing(item["effect"]) && item["effect"] is JsonObject effect && effect["params"] is JsonObject parameters)
                    Js.SetOrRemove(parameters, "pace", Js.Clone(pace), Js.Has(Js.Get(config, "params"), "pace"));
                result.Add(copy);
            }
            return result;
        }

        /// <summary>paramsAfterChange(config, key, value, zones): the parameters after one change, keeping exclusive options exclusive.</summary>
        public JsonObject ParamsAfterChange(JsonNode? config, string key, JsonNode? value, JsonNode? zones = null)
        {
            var parameters = Js.Spread(Js.Get(config, "params"));
            parameters[key] = Js.Clone(value);
            if (Js.StrictEquals(Js.Get(config, "id"), "breathing") && key == "playback" && Js.StrictEquals(value, "synchronized"))
            {
                var shared = Js.Objects(zones).FirstOrDefault(zone => SynchronizedBreathing(zone["effect"]));
                parameters["pace"] = Js.Clone(Js.Get(Js.Get(shared?["effect"], "params"), "pace") ?? parameters["pace"]) ?? 2;
            }
            if (Js.IsTrue(value))
                foreach (var pair in Js.Items(Js.Get(EffectById(Js.Get(config, "id")), "exclusive_params")))
                    if (Js.Includes(pair, key))
                        foreach (var other in Js.Items(pair))
                            if (!Js.StrictEquals(other, key)) parameters[Js.ToStr(other)] = false;
            return parameters;
        }

        static double Mod(double x, double n) => (x % n + n) % n;

        static double Ease(double x)
        {
            x = ColorMath.Clamp(x);
            return x * x * (3 - 2 * x);
        }

        static double Soft(double distance, double width) => Math.Abs(distance) >= width ? 0 : .5 + .5 * Math.Cos(Math.PI * distance / width);

        static double Reflected(double position, double count) => count <= 1 ? 0 : count - 1 - Math.Abs(Mod(position, 2 * (count - 1)) - (count - 1));

        internal static double Hash01(double index, double seed)
        {
            var x = unchecked((uint)(Js.ToInt32(index) * 374761393) + (uint)(Js.ToInt32(seed) * 668265263));
            x = unchecked((x ^ (x >> 13)) * 1274126177u);
            return (x ^ (x >> 16)) / 4294967295.0;
        }

        static double Pow(double x, double y)
        {
            if (double.IsNaN(y) || Math.Abs(x) == 1 && double.IsInfinity(y)) return double.NaN;
            return Math.Pow(x, y);
        }

        /// <summary>randomColor(key, phase): a hue that drifts smoothly from one seeded value to the next.</summary>
        public object? RandomColor(string key, double phase)
        {
            if (!colorSeeds.TryGetValue(key, out var seed))
            {
                seed = 2166136261;
                foreach (var b in Encoding.UTF8.GetBytes(key)) seed = unchecked((seed ^ b) * 16777619u);
                colorSeeds[key] = seed;
            }
            var index = Math.Floor(phase);
            var fraction = Ease(phase - index);
            double start = Hash01(index, seed), end = Hash01(index + 1, seed);
            return math.HsvToHex((start + (Mod(end - start + .5, 1) - .5) * fraction) * 360, .85, 1);
        }

        /// <summary>resolvedColorParams(config, time, phase): parameters with random colors and color brightness applied.</summary>
        public JsonObject ResolvedColorParams(JsonNode? config, double time, double? phase = null)
        {
            var effect = EffectById(Js.Get(config, "id"));
            var parameters = Js.Spread(DefaultConfig(Js.Get(config, "id"))["params"], Js.Get(config, "params"));
            var transition = parameters["color_transition"];
            var colorPhase = phase ?? time / (Js.Truthy(transition) ? Js.ToNumber(transition) : 8);
            var specs = Js.Objects(effect["params"]).ToList();
            foreach (var param in specs)
            {
                if (!Js.StrictEquals(param["type"], "color")) continue;
                var key = Js.ToStr(param["key"]);
                var random = param["random_param"];
                if (Js.Truthy(random) && specs.Any(item => Js.StrictEquals(item["key"], random) && Js.StrictEquals(item["randomizes"], param["key"]))
                    && Js.IsTrue(parameters[Js.ToStr(random)]) && !SynchronizedBreathing(config))
                    parameters[key] = PaintNode(RandomColor(key, colorPhase));
                var brightness = param["brightness_param"];
                if (Js.Truthy(brightness) && specs.Any(item => Js.StrictEquals(item["key"], brightness) && Js.StrictEquals(item["dims"], param["key"])))
                {
                    var brightnessKey = Js.ToStr(brightness);
                    var color = parameters[key];
                    var dimmed = ColorBrightness(color, parameters.TryGetPropertyValue(brightnessKey, out var level), level);
                    if (dimmed is not null) parameters[key] = PaintNode(dimmed);
                }
            }
            return parameters;
        }

        /// <summary>colorBrightness(color, value = 100). Returns null when the color is left unchanged.</summary>
        object? ColorBrightness(JsonNode? color, bool present, JsonNode? value)
        {
            var brightness = present ? value is null ? 0 : Js.ToNumber(value) : 100;
            return double.IsFinite(brightness) && brightness < 100 ? math.Dim(Paint(color), ColorMath.Clamp(brightness / 100)) : null;
        }

        object? ColorBrightness(object? color, JsonObject p, string key)
        {
            var brightness = p.TryGetPropertyValue(key, out var value) ? value is null ? 0 : Js.ToNumber(value) : 100;
            return double.IsFinite(brightness) && brightness < 100 ? math.Dim(color, ColorMath.Clamp(brightness / 100)) : color;
        }

        static double Noise(double x, double seed)
        {
            var i = Math.Floor(x);
            return Hash01(i, seed) + (Hash01(i + 1, seed) - Hash01(i, seed)) * Ease(x - i);
        }

        static double Wave(double phase) => .5 + .5 * Math.Sin(2 * Math.PI * phase);

        static double RingDistance(double position, double center, double count)
        {
            var distance = Mod(position - center, count);
            return distance > count / 2 ? distance - count : distance;
        }

        object? SoftPalette(object?[] colors, double value)
        {
            var position = ColorMath.Clamp(value) * (colors.Length - 1);
            var index = Math.Min(colors.Length - 2, Math.Floor(position));
            return math.Mix(At(colors, index), At(colors, index + 1), Ease(position - index));
        }

        static object? At(object?[] colors, double index) => index >= 0 && index < colors.Length && index == Math.Floor(index) ? colors[(int)index] : null;

        object? Weighted(IReadOnlyList<(object? Color, double Weight)> lights)
        {
            var rgb = lights.Select(light => (Channels: math.HexToRgb(light.Color), light.Weight)).ToList();
            var result = new double[3];
            for (var channel = 0; channel < 3; channel++)
            {
                double sum = 0;
                foreach (var (channels, weight) in rgb) sum += (channel < channels.Length ? channels[channel] : double.NaN) * weight;
                result[channel] = sum;
            }
            return math.RgbToHex(result);
        }

        object? Glow(object? background, IReadOnlyList<(object? Color, double Weight)> lights)
        {
            double total = 0;
            foreach (var light in lights) total += light.Weight;
            if (total <= 0) return background;
            var strength = 1 - Math.Exp(-total);
            var weighted = new List<(object?, double)>(lights.Count + 1) { (background, 1 - strength) };
            weighted.AddRange(lights.Select(light => (light.Color, light.Weight * strength / total)));
            return Weighted(weighted);
        }

        object? PaletteAt((double At, object? Color)[]? stops, double value)
        {
            if (stops is null || stops.Length == 0) return math.RgbToHex([0, 0, 0]);
            for (var i = 1; i < stops.Length; i++)
                if (value <= stops[i].At) return math.Mix(stops[i - 1].Color, stops[i].Color, (value - stops[i - 1].At) / (stops[i].At - stops[i - 1].At));
            return stops[^1].Color;
        }

        static double PhaseAt(EffectClock clock, double rate, double time)
        {
            if (clock.Rate is { } previous && !(previous == rate)) clock.Offset = Or0(clock.Offset) + time * (previous - rate);
            clock.Rate = rate;
            return Or0(clock.Offset) + time * rate;
        }

        static double Or0(double? value) => value is { } number && Js.Truthy(number) ? number : 0;

        /// <summary>previewPhase(clock, config, time): the motion phase; a speed change keeps the current position.</summary>
        public double PreviewPhase(EffectClock clock, JsonNode? config, double time)
        {
            var parameters = Js.Get(config, "params");
            if (SynchronizedBreathing(config)) return PhaseAt(clock, 1 / BreathingPeriod(Js.Get(parameters, "pace")), time);
            var period = Js.Get(parameters, "period");
            var rate = Js.Truthy(period) ? 1 / Js.ToNumber(period)
                : Js.Get(parameters, "speed") is { } speed ? Js.ToNumber(speed)
                : Js.Get(parameters, "rate") is { } r ? Js.ToNumber(r) : 1;
            return PhaseAt(clock, rate, time);
        }

        /// <summary>previewColorPhase(clock, config, time): the phase of random color changes.</summary>
        public double PreviewColorPhase(EffectClock clock, JsonNode? config, double time)
        {
            var transition = Js.Get(Js.Get(config, "params"), "color_transition");
            return PhaseAt(clock, 1 / (Js.Truthy(transition) ? Js.ToNumber(transition) : 8), time);
        }

        static double ActivityRate(Func<string, JsonNode?> port, bool includeUplinks) =>
            Js.IsTrue(port("link")) && (includeUplinks || !Js.IsTrue(port("uplink"))) && Js.IsFinite(port("traffic"))
                ? Math.Max(0, Js.Num(port("traffic"))!.Value) : 0;

        /// <summary>activityPeak(facts, includeUplinks): the highest traffic among linked ports, uplinks excluded unless included.</summary>
        public double ActivityPeak(IEnumerable<JsonNode?> facts, bool includeUplinks = false)
        {
            double peak = 0;
            foreach (var port in facts) peak = Math.Max(peak, ActivityRate(key => Js.Get(port, key), includeUplinks));
            return peak;
        }

        public double ActivityPeak(JsonArray facts, bool includeUplinks = false) => ActivityPeak((IEnumerable<JsonNode?>)facts, includeUplinks);

        /// <summary>dataPreviewFacts(count, time): illustrative traffic and PoE readings for gallery previews.</summary>
        public JsonArray DataPreviewFacts(int count, double time)
        {
            var result = new JsonArray();
            for (var index = 0; index < count; index++)
                result.Add(new JsonObject
                {
                    ["link"] = true, ["uplink"] = false,
                    ["traffic"] = 1000 * Pow(.08 + .92 * Wave(time * .075 + Hash01(index, 281)), 3),
                    ["poe"] = index % 4 == 0 ? "off" : "poe++",
                    ["poe_power"] = index % 4 == 0 ? 0 : 60 * Pow(.02 + .98 * Wave(time * .045 + Hash01(index, 529)), 2),
                });
            return result;
        }

        /// <summary>
        /// effectColor(config, index, count, time, facts): the color of one port. The hex renderer returns hex strings;
        /// the RGB renderer returns channel arrays, or a parameter's hex string where the effect passes one through.
        /// </summary>
        public object? EffectColor(JsonNode? config, double index, double count, double time, EffectFacts? facts = null)
        {
            facts ??= new EffectFacts();
            var p = facts.ResolvedParams ?? ResolvedColorParams(config, time, facts.ColorPhase);
            var id = Js.Get(config, "id") is var idNode && Js.Str(idNode) is { } text ? text : null;
            double N(string key) => Js.Arith(p, key);
            bool T(string key) => Js.Truthy(p[key]);
            object? C(string key) => Paint(p[key]);
            JsonNode? F(string key) => facts.Fact(key);
            var primary = T("color") ? C("color") : C("color_a");
            var secondary = C("color_b");
            var background = T("background") ? C("background") : "#000000";
            var x = index / Math.Max(1, count - 1);
            var position = count > 1 ? x : .5;
            var travel = facts.Phase ?? time * (p["speed"] is { } speedNode ? Js.ToNumber(speedNode) : p["rate"] is { } rateNode ? Js.ToNumber(rateNode) : 1);
            switch (id)
            {
                case "static":
                    return primary;
                case "breathing":
                {
                    var synchronized = SynchronizedBreathing(config);
                    var period = synchronized ? BreathingPeriod(p["pace"]) : N("period");
                    var floor = synchronized ? 0 : N("floor");
                    var phase = (facts.Phase ?? time / period) - (!synchronized && T("ripple") ? index / (2 * count) : 0);
                    return math.Dim(primary, floor + (1 - floor) * (.5 - .5 * Math.Cos(2 * Math.PI * phase)));
                }
                case "rainbow":
                    return math.HsvToHex(((T("reverse") ? -travel : travel) + index * N("spread") / count) * 360, N("saturation"), 1);
                case "color_cycle":
                    return math.HsvToHex(travel * 360, N("saturation"), 1);
                case "gradient":
                {
                    var amount = x;
                    if (T("speed") || Js.Truthy(travel))
                    {
                        if (T("mirror")) amount = .5 - .5 * Math.Cos(Math.Acos(1 - 2 * x) + 2 * Math.PI * travel);
                        else
                        {
                            var phase = Mod(.8 * (.5 - Math.Sin(Math.Asin(1 - 2 * x) / 3)) + travel, 1);
                            amount = phase < .8 ? Ease(phase / .8) : 1 - Ease((phase - .8) / .2);
                        }
                    }
                    return math.Mix(primary, secondary, amount);
                }
                case "comet":
                {
                    if (count == 1) return primary;
                    double level = 0;
                    if (T("bounce"))
                    {
                        for (var step = 0; step <= 16; step++)
                            level = Math.Max(level, Soft(index - Reflected(travel - N("tail") * step / 16, count), 1.5) * Math.Exp(-3.0 * step / 16));
                    }
                    else
                    {
                        var behind = Mod(travel - index, count);
                        var distance = behind > count / 2 ? behind - count : behind;
                        level = Math.Max(Soft(distance, 1.5), Math.Exp(-3 * behind / N("tail")) * Ease((count - behind) / 1.5));
                    }
                    return math.Mix(background, primary, level);
                }
                case "scanner":
                {
                    if (count == 1) return primary;
                    var head = (count - 1) * (.5 - .5 * Math.Cos(Math.PI * travel / (count - 1)));
                    return math.Mix(background, primary, Soft(index - head, N("width")));
                }
                case "marquee":
                {
                    var spacing = N("spacing");
                    var offset = Mod(index - travel, spacing);
                    return math.Mix(background, primary, Soft(Math.Min(offset, spacing - offset), 1.25));
                }
                case "starry_night":
                {
                    var fade = N("fade");
                    var life = 1.5 + fade;
                    var cycle = life + 1 / N("rate");
                    var offsetTime = time + Hash01(index, 31) * cycle;
                    var age = Mod(offsetTime, cycle);
                    var level = age < 1.5 ? Ease(age / 1.5) : age < life ? 1 - Ease((age - 1.5) / fade) : 0;
                    var color = T("rainbow")
                        ? ColorBrightness(math.HsvToHex(Hash01(index + Math.Floor(offsetTime / cycle), 81) * 360, 1, 1), p, "brightness_color")
                        : primary;
                    return math.Mix(background, color, level);
                }
                case "fire":
                    return PaletteAt(firePalettes.GetValueOrDefault(Js.ToStr(p["palette"])),
                        N("intensity") * (.25 + .55 * Noise(travel * 1.2 + index * 7.31, index + 1) + .2 * Noise(travel * 3 + index * 3.7, 100 + index)));
                case "police":
                {
                    var phase = Mod(travel, 1);
                    var first = phase < .5;
                    var flash = Pow(Math.Sin(Math.PI * (phase % .5) / .5), 2);
                    var pattern = p["pattern"];
                    if (Js.StrictEquals(pattern, "together")) return math.Dim(first ? primary : secondary, flash);
                    var left = Js.StrictEquals(pattern, "alternate") ? index % 2 == 0 : index < Math.Floor(count / 2);
                    return first == left ? math.Dim(left ? primary : secondary, flash) : "#000000";
                }
                case "strobe":
                {
                    var phase = Mod(travel, 1);
                    var duty = N("duty");
                    var edge = Math.Min(.1 * N("rate"), duty / 2);
                    if (phase >= duty) return "#000000";
                    var color = T("random_colors")
                        ? ColorBrightness(math.HsvToHex(Hash01(Math.Floor(travel), 99) * 360, 1, 1), p, "brightness_color")
                        : primary;
                    return math.Dim(color, Ease(phase / edge) * Ease((duty - phase) / edge));
                }
                case "ripple":
                {
                    var center = Js.StrictEquals(p["center"], 0) ? (count - 1) / 2 : Math.Min(N("center"), count) - 1;
                    var spacing = N("spacing");
                    var behind = Mod(travel - Math.Abs(index - center), spacing);
                    return math.Mix(background, primary, Soft(Math.Min(behind, spacing - behind), 1.75));
                }
                case "lightning":
                {
                    var interval = 3 + 1 / N("rate");
                    var age = Mod(time, interval);
                    var strike = Math.Floor(time / interval);
                    var center = Math.Floor(Hash01(strike, 49) * count);
                    var distance = Math.Abs(index - center);
                    var flash = Math.Max(Soft(age - .5, .5), Math.Max(.8 * Soft(age - 1.5, .65), .2 * Soft(age - 1.8, 1.2)));
                    var reach = N("reach");
                    return math.Mix(C("sky"), primary, distance <= reach ? flash * (1 - distance / (reach + 1)) : 0);
                }
                case "daylight":
                {
                    var now = DateTime.Now;
                    var hour = facts.Hour ?? now.Hour + now.Minute / 60.0 + now.Second / 3600.0;
                    var night = N("night_brightness");
                    return math.Dim(PaletteAt(daylightPalettes.GetValueOrDefault(Js.ToStr(p["palette"])), hour),
                        night + (1 - night) * Ease((hour - 6) / 2) * (1 - Ease((hour - 20) / 2)));
                }
                case "aurora":
                {
                    var a = Wave(position * .8 - travel + .12 * Math.Sin(2 * Math.PI * (travel * .31 + position)));
                    var b = Wave(position * 1.2 + travel * .67 + .35);
                    return Glow(background, [(C("color_a"), 1.8 * a * a), (C("color_b"), 1.8 * N("layers") * b * b)]);
                }
                case "lava_lamp":
                {
                    double[] centers = [(count - 1) * Wave(travel), (count - 1) * Wave(travel * .73 + .39), (count - 1) * Wave(travel * .41 + .7)];
                    var lights = centers.Select((center, j) =>
                    {
                        var width = N("width") * (.8 + .3 * Wave(travel * .53 + j * .3));
                        var weight = Math.Exp(-Pow((index - center) / width, 2)) * (.65 + .65 * Wave(travel * .61 + j * .4));
                        return (math.Mix(C("color_a"), C("color_b"), j / 2.0), weight);
                    }).ToList();
                    return Glow(background, lights);
                }
                case "plasma":
                {
                    var scale = N("scale");
                    var value = (Wave(position * scale + travel) + Wave(position * scale * .6 - travel * .73) + Wave(position * scale * .35 + travel * .41)) / 3;
                    return SoftPalette([C("color_a"), C("color_b"), C("color_c")], value);
                }
                case "ocean_swell":
                {
                    var height = .65 * Wave(position * .8 - travel) + .35 * Wave(position * 1.4 + travel * .63 + .2);
                    var foam = .65 * Ease((height - .6) / .4);
                    var level = 1 - N("depth") * (1 - height);
                    return Weighted([(C("deep_color"), (1 - height) * (1 - foam) * level), (C("crest_color"), height * (1 - foam) * level), (C("foam_color"), foam * level)]);
                }
                case "eclipse":
                    return math.Mix(C("color"), C("shadow"), N("depth") * (count == 1 ? Wave(travel) : Soft(RingDistance(index, travel * count, count), N("width"))));
                case "meteor_shower":
                {
                    var tail = N("tail");
                    var lights = Enumerable.Range(0, Js.ToLength(N("count"))).Select(j =>
                    {
                        var life = travel * (1 - j * .09) + j * .37;
                        var cycle = Math.Floor(life);
                        var age = Mod(life, 1);
                        var head = -tail + age * (count - 1 + 2 * tail);
                        var distance = head - index;
                        var width = distance >= 0 ? tail : 1.25;
                        var weight = Math.Exp(-Pow(distance / width, 2)) * Pow(Math.Sin(Math.PI * age), 2) * 2;
                        return (math.Mix(C("color_a"), C("color_b"), Hash01(cycle, 83 + j)), weight);
                    }).ToList();
                    return Glow(background, lights);
                }
                case "counterflow":
                    return count == 1 ? math.Mix(C("color_a"), C("color_b"), Wave(travel)) : Glow(background, [
                        (C("color_a"), 2 * Soft(RingDistance(index, travel * count, count), N("width"))),
                        (C("color_b"), 2 * Soft(RingDistance(index, (.5 - travel * .83) * count, count), N("width")))]);
                case "color_dissolve":
                {
                    object?[] colors = [C("color_a"), C("color_b"), C("color_c")];
                    var local = travel + Hash01(index, 31) * N("stagger");
                    var cycle = Math.Floor(local);
                    return math.Mix(At(colors, Mod(cycle, 3)), At(colors, Mod(cycle + 1, 3)), Ease(local - cycle));
                }
                case "ink_spread":
                {
                    object?[] colors = [C("color_a"), C("color_b"), C("color_c")];
                    var cycle = Math.Floor(travel);
                    var age = Mod(travel, 1);
                    var origin = Hash01(cycle, 211) * (count - 1);
                    var spread = N("spread");
                    return math.Mix(At(colors, Mod(cycle, 3)), At(colors, Mod(cycle + 1, 3)),
                        Ease((age - spread * Math.Abs(index - origin) / Math.Max(1, count - 1)) / (1 - spread)));
                }
                case "alternating_fade":
                    return math.Mix(C("color_a"), C("color_b"), Wave(travel + index % 2 * .5));
                case "color_tide":
                {
                    var width = N("width");
                    var front = -width / 2 + (count - 1 + width) * Wave(travel);
                    var port = T("reverse") ? count - 1 - index : index;
                    return math.Mix(background, C("color"), Ease((front - port) / width + .5));
                }
                case "palette_morph":
                {
                    var value = .5 - .5 * Math.Cos(Math.PI * position + Math.PI * travel * N("drift"));
                    var morph = Wave(travel);
                    return Weighted([(C("first_start"), (1 - value) * (1 - morph)), (C("first_end"), value * (1 - morph)),
                        (C("second_start"), (1 - value) * morph), (C("second_end"), value * morph)]);
                }
                case "ember_bed":
                {
                    var heat = .15 + .55 * Noise(index * .7 + travel, 71) + .3 * Wave(travel * .37 - index * .23);
                    var level = 1 - N("depth") * (1 - heat);
                    return Weighted([(C("cool_color"), (1 - heat) * level), (C("hot_color"), heat * level)]);
                }
                case "fireflies":
                {
                    var lights = Enumerable.Range(0, Js.ToLength(N("count"))).Select(j =>
                    {
                        var center = (count - 1) * Noise(travel * .23 + j * 11, 131 + j);
                        var weight = Soft(index - center, N("width")) * Pow(Wave(travel * .7 + j * .31), 2) * 2.5;
                        return (math.Mix(C("color_a"), C("color_b"), Wave(travel * .19 + j * .43)), weight);
                    }).ToList();
                    return Glow(background, lights);
                }
                case "traffic_trails":
                {
                    var state = facts.AnimationState;
                    var traffic = F("traffic");
                    var target = Js.IsTrue(F("uplink")) && !T("include_uplinks") ? 0
                        : ColorMath.Clamp((Js.Truthy(traffic) ? Js.ToNumber(traffic) : 0) / N("max_mbps"));
                    var dt = state?.Last is not { } last ? 0 : Math.Max(0, time - last);
                    var online = !Js.IsFalse(F("link"));
                    var load = state?.Load is { } saved && Js.Truthy(saved) ? saved : 0;
                    object? color;
                    if (state is not null)
                    {
                        state.Last = time;
                        if (state.Online is { } wasOnline && online != wasOnline && Shown(state.Shown)) state.Fade = new EffectFade(state.Shown, time);
                        state.Online = online;
                    }
                    if (!online)
                    {
                        load = 0;
                        color = C("offline_color");
                    }
                    else
                    {
                        load += (target - load) * (1 - Math.Exp(-dt / (target > load ? .35 : 1.4)));
                        var pulse = count == 1 ? Wave(travel) : Soft(RingDistance(index, travel * count, count), N("width"));
                        var level = .3 + .2 * pulse + .5 * pulse * load;
                        color = Weighted([(C("idle_color"), (1 - load) * level), (C("busy_color"), load * level)]);
                    }
                    if (state is not null)
                    {
                        state.Load = load;
                        if (state.Fade is { } fadeState)
                        {
                            var progress = (time - fadeState.Start) / .6;
                            if (progress >= 1) state.Fade = null;
                            else color = math.Mix(fadeState.Color, color, Ease(progress));
                        }
                        state.Shown = color;
                    }
                    return color;
                }
                case "traffic":
                {
                    var traffic = F("traffic");
                    var mbps = Js.IsTrue(F("uplink")) && !T("include_uplinks") ? 0 : Js.Truthy(traffic) ? Js.ToNumber(traffic) : 0;
                    var bps = mbps * 1e6;
                    var speed = F("speed");
                    var capacity = Js.IsFinite(speed) && Js.Num(speed) > 0 ? Js.Num(speed)!.Value : 1000;
                    var load = bps <= 1e3 ? 0 : Math.Log10(bps / 1e3) / Math.Log10(Math.Max(capacity * 1e3, 10));
                    var state = facts.AnimationState ?? new EffectAnimationState();
                    var dt = state.Last is not { } last ? .25 : Math.Max(0, time - last);
                    state.Last = time;
                    if (!Js.IsTrue(F("link")))
                    {
                        state.Level = 0;
                        return C("offline_color");
                    }
                    if (Js.IsTrue(F("uplink")) && T("custom_uplink"))
                    {
                        state.Level = 0;
                        return C("uplink_color");
                    }
                    var smoothing = Pow(N("smoothing"), dt * 4);
                    state.Level = smoothing * Or0(state.Level) + (1 - smoothing) * ColorMath.Clamp(load);
                    return math.Mix(C("idle_color"), C("busy_color"), state.Level.Value);
                }
                case "activity_glow":
                {
                    var state = facts.AnimationState ?? new EffectAnimationState();
                    var dt = state.Last is not { } last ? 0 : Math.Max(0, time - last);
                    var previous = Or0(state.Target);
                    var speed = state.Speed ?? N("speed");
                    var level = Or0(state.Level);
                    level += (previous - level) * (1 - Math.Exp(-dt / ((previous > level ? .25 : 1.2) / speed)));
                    var rate = ActivityRate(F, T("include_uplinks"));
                    state.Target = ColorMath.Clamp(rate / Math.Max(N("minimum_mbps"), facts.PeakTraffic ?? rate));
                    state.Last = time;
                    state.Speed = p.ContainsKey("speed") ? N("speed") : null;
                    state.Level = level;
                    return math.Mix(C("idle_color"), C("active_color"), level);
                }
                case "poe_usage":
                {
                    var state = facts.AnimationState ?? new EffectAnimationState();
                    var dt = state.Last is not { } last ? 0 : Math.Max(0, time - last);
                    var alpha = -Js.Expm1(-dt * (state.Speed ?? N("speed")) / .6);
                    state.Low = Or0(state.Low) + (Or0(state.TargetLow) - Or0(state.Low)) * alpha;
                    state.High = Or0(state.High) + (Or0(state.TargetHigh) - Or0(state.High)) * alpha;
                    var poePower = F("poe_power");
                    var powered = Js.Str(F("poe")) is { } poe && PoeClasses.Contains(poe) && Js.IsFinite(poePower) && Js.Num(poePower) > 0;
                    var level = powered ? ColorMath.Clamp(Js.Log1p(Js.Num(poePower)!.Value) / Js.Log1p(60)) : 0;
                    state.TargetLow = powered ? 1 - level : 0;
                    state.TargetHigh = powered ? level : 0;
                    state.Last = time;
                    state.Speed = p.ContainsKey("speed") ? N("speed") : null;
                    var presence = state.Low.Value + state.High.Value;
                    return math.Mix(C("no_power_color"), math.Mix(C("low_color"), C("high_color"), presence > 0 ? state.High.Value / presence : 0), presence);
                }
                default:
                    return primary;
            }
        }

        static bool Shown(object? color) => color switch
        {
            null => false,
            string text => text.Length > 0,
            _ => true,
        };
    }
}
