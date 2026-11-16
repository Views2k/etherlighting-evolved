// Checks for the shared logic layer (Core/Model), ported from the browser workspace's node tests. Each JS test is one
// group; each assertion group in it reports one check. Fixtures are read from tests/fixtures in this checkout.
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;

namespace EtherlightingEvolved.Native.Check.Checks;

public static class ModelChecks
{
    delegate void Expect(bool ok, string? what = null);

    static Action<bool, string> report = (_, _) => { };

    public static void Run(Action<bool, string> check)
    {
        report = check;
        ColorChecks();
        EffectChecks();
        RuleChecks();
        PortDisplayChecks();
        TopologyChecks();
        PortMapChecks();
        EffectDraftChecks();
        SfpSyncChecks();
        LightingStateChecks();
        SecondaryModelChecks();
        SceneTransactionChecks();
        GalleryChecks();
        DraftPreviewChecks();
        ProfilePreviewChecks();
        SettingsChecks();
        SceneReviewChecks();
        ScheduleClockChecks();
        PortAnimationChecks();
    }

    // ----- Infrastructure -----

    /// <summary>One JS test: every Expect call reports a check; an exception fails the test.</summary>
    static void T(string name, Action<Expect> body)
    {
        var count = 0;
        try
        {
            body((ok, what) =>
            {
                count++;
                report(ok, $"model: {name}{(what is null ? count > 1 ? $" ({count})" : "" : $" ({what})")}");
            });
            if (count == 0) report(false, $"model: {name} ran without assertions");
        }
        catch (Exception error)
        {
            report(false, $"model: {name} (threw {error.GetType().Name}: {error.Message})");
        }
    }

    static readonly JsonValue UndefinedMarker = JsonValue.Create("undefined marker");

    /// <summary>Parses a JavaScript object literal: unquoted keys, single quotes, NaN, Infinity and undefined.</summary>
    static JsonNode? J(string text) => new Literal(text).Parse();

    static JsonObject O(string text) => (JsonObject)J(text)!;

    static JsonArray A(string text) => (JsonArray)J(text)!;

    sealed class Literal(string text)
    {
        int i;

        public JsonNode? Parse()
        {
            var value = Value();
            Ws();
            if (i != text.Length) throw new FormatException($"Unexpected text at {i}: {text[i..]}");
            return ReferenceEquals(value, UndefinedMarker) ? null : value;
        }

        void Ws()
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        }

        JsonNode? Value()
        {
            Ws();
            var c = text[i];
            if (c == '{')
            {
                i++;
                var obj = new JsonObject();
                while (true)
                {
                    Ws();
                    if (text[i] == '}') { i++; return obj; }
                    var key = text[i] is '"' or '\'' ? Text() : Word();
                    Ws();
                    if (text[i] != ':') throw new FormatException($"Expected ':' at {i}: {text}");
                    i++;
                    var value = Value();
                    if (!ReferenceEquals(value, UndefinedMarker)) obj[key] = value;
                    Ws();
                    if (text[i] == ',') i++;
                }
            }
            if (c == '[')
            {
                i++;
                var array = new JsonArray();
                while (true)
                {
                    Ws();
                    if (text[i] == ']') { i++; return array; }
                    var value = Value();
                    array.Add(ReferenceEquals(value, UndefinedMarker) ? null : value);
                    Ws();
                    if (text[i] == ',') i++;
                }
            }
            if (c is '"' or '\'') return JsonValue.Create(Text());
            if (c is '-' or '+' or '.' || char.IsAsciiDigit(c))
            {
                var start = i;
                if (c is '-' or '+') i++;
                if (string.CompareOrdinal(text, i, "Infinity", 0, 8) == 0)
                {
                    i += 8;
                    return JsonValue.Create(c == '-' ? double.NegativeInfinity : double.PositiveInfinity);
                }
                while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] is '.' or 'e' or 'E' || text[i] is '+' or '-' && text[i - 1] is 'e' or 'E')) i++;
                return JsonValue.Create(double.Parse(text[start..i], NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            var word = Word();
            return word switch
            {
                "true" => JsonValue.Create(true),
                "false" => JsonValue.Create(false),
                "null" => null,
                "NaN" => JsonValue.Create(double.NaN),
                "Infinity" => JsonValue.Create(double.PositiveInfinity),
                "undefined" => UndefinedMarker,
                _ => throw new FormatException($"Unknown word {word} in {text}"),
            };
        }

        string Word()
        {
            var start = i;
            while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '$')) i++;
            if (start == i) throw new FormatException($"Expected a word at {i}: {text}");
            return text[start..i];
        }

        string Text()
        {
            var quote = text[i++];
            var result = new System.Text.StringBuilder();
            while (text[i] != quote)
            {
                var c = text[i++];
                if (c != '\\') { result.Append(c); continue; }
                var escaped = text[i++];
                result.Append(escaped switch
                {
                    'n' => "\n",
                    't' => "\t",
                    'r' => "\r",
                    'b' => "\b",
                    'f' => "\f",
                    'u' => ((char)int.Parse(text.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString(),
                    _ => escaped.ToString(),
                });
                if (escaped == 'u') i += 4;
            }
            i++;
            return result.ToString();
        }
    }

    static bool Eq(JsonNode? actual, string expected) => Js.DeepEqual(actual, J(expected));

    static bool Eq(JsonNode? actual, JsonNode? expected) => Js.DeepEqual(actual, expected);

    static JsonObject With(JsonNode? source, string changes) => Js.Spread(source, J(changes));

    static JsonArray Arr(params JsonNode?[] items) => Js.ArrayOf(items);

    static JsonNode? Get(JsonNode? node, string path) => node.At(path);

    static string? S(JsonNode? node, string path) => Js.Str(node.At(path));

    static double? N(JsonNode? node, string path) => Js.Num(node.At(path));

    static bool Throws(Action action, string? pattern = null)
    {
        try { action(); }
        catch (Exception error) { return pattern is null || Regex.IsMatch(error.Message, pattern); }
        return false;
    }

    static void Await(Func<Task> action) => Task.Run(action).GetAwaiter().GetResult();

    static T Await<T>(Func<Task<T>> action) => Task.Run(action).GetAwaiter().GetResult();

    static Exception? Rejection(Func<Task> action)
    {
        try { Await(action); }
        catch (Exception error) { return error; }
        return null;
    }

    static bool Near(IReadOnlyList<double>? actual, IReadOnlyList<double> expected, double tolerance = 1e-8) =>
        actual is not null && actual.Count == expected.Count && actual.Select((value, index) => Math.Abs(value - expected[index]) <= tolerance).All(ok => ok);

    static bool Close(IReadOnlyList<double>? actual, IReadOnlyList<double> expected) => Near(actual, expected, 1);

    /// <summary>assert.deepEqual on renderer colors (hex strings or channel arrays).</summary>
    static bool Same(object? a, object? b) => (a, b) switch
    {
        (string x, string y) => x == y,
        (double[] x, double[] y) => x.SequenceEqual(y),
        (null, null) => true,
        _ => false,
    };

    static double[] Rgb(object? color) => EffectRenderer.Rgb(color);

    static double[] HexRgb(object? color) => ColorMath.HexToRgb(color as string);

    static JsonObject Param(JsonNode? effect, string key) => Js.Objects(Get(effect, "params")).First(param => Js.Str(param["key"]) == key);

    static JsonObject CatalogEffect(string id) => Js.Objects(EffectCatalog.Catalog).First(effect => Js.Str(effect["id"]) == id);

    static JsonObject P(JsonObject config) => (JsonObject)config["params"]!;

    static JsonObject Assign(JsonObject target, string changes) => Js.Assign(target, J(changes));

    static string? RepoFile(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, relative);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    static JsonArray Fixture(string name) =>
        RepoFile(Path.Combine("tests", "fixtures", name)) is { } path ? (JsonArray)JsonNode.Parse(File.ReadAllText(path))!
            : throw new FileNotFoundException($"tests/fixtures/{name} was not found above {AppContext.BaseDirectory}");

    static JsonObject ColorsJson(Dictionary<int, double[]> colors)
    {
        var result = new JsonObject();
        foreach (var (port, color) in colors.OrderBy(pair => pair.Key)) result[port.ToString(CultureInfo.InvariantCulture)] = Js.NumberArray(color);
        return result;
    }

    static string Text(object? color) => color switch
    {
        string text => text,
        double[] channels => string.Join(",", channels.Select(Js.NumberToString)),
        _ => "undefined",
    };

    // ----- src/color.mjs (tests/core.test.mjs) -----

    static void ColorChecks()
    {
        T("color: color entry accepts shorthand, rejects malformed values, and round-trips RGB", e =>
        {
            e(ColorMath.NormalizeHex(" #f0a ") == "#FF00AA" && ColorMath.NormalizeHex("abcdef") == "#ABCDEF", "shorthand");
            e(new[] { "#ffffff00", "red", "<script>", null }.All(value => ColorMath.NormalizeHex(value) is null), "malformed");
            foreach (var color in new[] { "#000000", "#FFFFFF", "#FF0000", "#00FF00", "#0000FF", "#E4BD82", "#79DCC0" })
            {
                var hsv = ColorMath.HexToHsv(color);
                e(ColorMath.HsvToHex(hsv[0], hsv[1], hsv[2]) == color, color);
            }
            e(ColorMath.RgbToHex([-20, 256, 127.7]) == "#00FF80", "rgbToHex clamps and rounds");
        });
    }

    // ----- src/effects.mjs and preview/effect-renderer.mjs -----

    static readonly Regex HexPattern = new("^#[0-9a-f]{6}$", RegexOptions.IgnoreCase);

    static readonly string[] AddedEffects = ["aurora", "lava_lamp", "plasma", "ocean_swell", "eclipse", "meteor_shower", "counterflow",
        "color_dissolve", "ink_spread", "alternating_fade", "color_tide", "palette_morph", "ember_bed", "fireflies", "traffic_trails"];

    static JsonArray CatalogWith(JsonNode definition)
    {
        var list = (JsonArray)EffectCatalog.Catalog.DeepClone();
        list.Add(definition);
        return list;
    }

    static void EffectChecks()
    {
        // tests/core.test.mjs
        T("effects: the library includes all registered effects and every effect produces valid RGB frames", e =>
        {
            e(Effects.Library.Count == EffectCatalog.Catalog.Count, "library size");
            e(Js.Objects(Effects.EffectList).Select(effect => Js.Str(effect["id"])).Distinct().Count() == Effects.EffectList.Count, "unique ids");
            foreach (var effect in Js.Objects(Effects.EffectList))
            {
                var id = Js.Str(effect["id"])!;
                var ok = true;
                foreach (var t in new[] { 0, .1, 1.5, 10, 600 })
                    for (var i = 0; i < 8; i++)
                        ok &= Effects.EffectColor(Effects.DefaultConfig(id), i, 8, t, new EffectFacts(O("{link:true,traffic:100}")) { Hour = 13 }) is string text && HexPattern.IsMatch(text);
                e(ok, id);
            }
        });
        T("effects: live effect defaults and control limits come from the connected backend", e =>
        {
            var remote = (JsonArray)EffectCatalog.Catalog.DeepClone();
            var speed = Param(Js.Objects(remote).First(effect => Js.Str(effect["id"]) == "comet"), "speed");
            speed["default"] = 0.75;
            speed["max"] = 3;
            try
            {
                Effects.SetEffectCatalog(remote);
                e(N(Effects.DefaultConfig("comet"), "params.speed") == .75, "default");
                e(Js.Num(Param(Effects.EffectById("comet"), "speed")["max"]) == 3, "limit");
            }
            finally { Effects.SetEffectCatalog(EffectCatalog.Catalog); }
        });
        T("effects: every animated effect exposes its speed and the speed control preserves its limits", e =>
        {
            var animated = Js.Objects(EffectCatalog.Catalog).Where(effect => Js.Str(effect["id"]) is not ("static" or "daylight" or "traffic")).ToList();
            e(animated.Count == EffectCatalog.Catalog.Count - 3, "count");
            foreach (var effect in animated)
            {
                var id = Js.Str(effect["id"])!;
                var param = Effects.SpeedParam(Effects.EffectById(id));
                e(param is not null, $"{id} speed setting");
                if (param is null) continue;
                foreach (var key in new[] { "min", "default", "max" })
                {
                    var value = Js.ToNumber(param[key]);
                    e(Effects.SpeedParamValue(param, Effects.SpeedSliderValue(param, value)) == value, $"{id} preserves {key}");
                }
            }
            var breathing = Effects.SpeedParam(Effects.EffectById("breathing"))!;
            e(Effects.SpeedParamValue(breathing, breathing["max"]) == 1, "right end is fastest supported breathing");
            e(Effects.SpeedParamValue(breathing, breathing["min"]) == 30, "left end is slowest breathing");
        });
        T("effects: Marquee can randomize its background while its chosen primary stays unchanged", e =>
        {
            var config = Effects.DefaultConfig("marquee");
            Assign(P(config), "{color:'#93B7E8',background:'#172E45'}");
            config["params"] = Effects.ParamsAfterChange(config, "random_background", true);
            var backgrounds = new HashSet<string>();
            foreach (var time in new[] { 0.0, 4, 8, 16 })
            {
                var resolved = Effects.ResolvedColorParams(config, time);
                e(S(resolved, "color") == "#93B7E8", $"primary at {time}");
                backgrounds.Add(S(resolved, "background") ?? "");
            }
            e(backgrounds.Count > 1, "background changes");
            config["params"] = Effects.ParamsAfterChange(config, "random_background", false);
            e(S(Effects.ResolvedColorParams(config, 8), "background") == "#172E45", "fixed background");
            e(S(config, "params.color") == "#93B7E8", "primary kept");
        });
        T("effects: generic primary randomization and legacy sparkle or flash colors remain mutually exclusive", e =>
        {
            foreach (var id in new[] { "starry_night", "strobe" })
            {
                var config = Effects.DefaultConfig(id);
                var pair = Js.Items(Js.Get(Effects.EffectById(id)["exclusive_params"], 0)).Select(Js.ToStr).ToArray();
                var (generic, legacy) = (pair[0], pair[1]);
                P(config)["color"] = "#93B7E8";
                config["params"] = Effects.ParamsAfterChange(config, legacy, true);
                config["params"] = Effects.ParamsAfterChange(config, generic, true);
                e(Js.IsFalse(P(config)[legacy]) && Js.IsTrue(P(config)[generic]), $"{id} generic wins");
                config["params"] = Effects.ParamsAfterChange(config, legacy, true);
                e(Js.IsTrue(P(config)[legacy]) && Js.IsFalse(P(config)[generic]) && S(config, "params.color") == "#93B7E8", $"{id} legacy wins");
                if (P(config).ContainsKey("random_background"))
                {
                    config["params"] = Effects.ParamsAfterChange(config, "random_background", true);
                    e(Js.IsTrue(P(config)[legacy]), $"{id} background randomization does not disable the primary source");
                }
            }
        });
        T("effects: color-transition edits preserve color phase independently of movement speed", e =>
        {
            var config = Effects.DefaultConfig("marquee");
            EffectClock motion = new(), colors = new();
            P(config)["random_background"] = true;
            var before = Effects.PreviewColorPhase(colors, config, 12);
            var position = Effects.PreviewPhase(motion, config, 12);
            P(config)["color_transition"] = 2;
            e(Effects.PreviewColorPhase(colors, config, 12) == before, "color phase kept");
            e(Effects.PreviewPhase(motion, config, 12) == position, "motion kept");
            e(Effects.PreviewColorPhase(colors, config, 13) == before + .5, "new transition rate");
            P(config)["speed"] = 1.25;
            e(Effects.PreviewColorPhase(colors, config, 13) == before + .5, "speed does not change color phase");
            var a = HexRgb(S(Effects.ResolvedColorParams(config, 0, .99999), "background"));
            var b = HexRgb(S(Effects.ResolvedColorParams(config, 0, 1.00001), "background"));
            e(a.Select((channel, i) => Math.Abs(channel - b[i]) <= 1).All(ok => ok), "continuous at a phase boundary");
        });
        T("effects: demo speed edits preserve the current animation position and can pause at zero", e =>
        {
            var clock = new EffectClock();
            var config = Effects.DefaultConfig("rainbow");
            var before = Effects.PreviewPhase(clock, config, 12);
            P(config)["speed"] = .2;
            e(Effects.PreviewPhase(clock, config, 12) == before, "position kept");
            e(Math.Abs(Effects.PreviewPhase(clock, config, 13) - before - .2) < 1e-10, "new speed");
            P(config)["speed"] = 0;
            var stopped = Effects.PreviewPhase(clock, config, 14);
            e(Effects.PreviewPhase(clock, config, 50) == stopped, "paused");
        });
        T("effects: rainbow stars keep their color through the global preview cycle boundary", e =>
        {
            var config = Effects.DefaultConfig("starry_night");
            P(config)["rainbow"] = true;
            var cycle = 1.5 + Js.ToNumber(P(config)["fade"]) + 1 / Js.ToNumber(P(config)["rate"]);
            for (var port = 0; port < 8; port++)
            {
                var a = HexRgb(Effects.EffectColor(config, port, 8, cycle - .0001));
                var b = HexRgb(Effects.EffectColor(config, port, 8, cycle + .0001));
                e(a.Select((channel, i) => Math.Abs(channel - b[i]) <= 1).All(ok => ok), $"port {port + 1}");
            }
        });

        // tests/activity-glow.test.mjs
        T("effects: uplink traffic does not set the activity scale", e =>
            e(Effects.ActivityPeak(A("[{link:true,traffic:20},{link:true,uplink:true,traffic:10000},{link:false,traffic:500},{link:null,traffic:1000},{link:true,traffic:NaN}]")) == 20));
        T("effects: uplinks keep the same appearance as an idle device in each data effect", e =>
        {
            foreach (var id in new[] { "traffic", "traffic_trails", "activity_glow" })
            {
                var config = Effects.DefaultConfig(id);
                EffectAnimationState uplink = new(), idle = new();
                foreach (var t in new[] { 0, .25, 1, 3, 10 })
                    e(Same(Effects.EffectColor(config, 0, 8, t, new EffectFacts(O("{link:true,uplink:true,traffic:10000}")) { PeakTraffic = 100, AnimationState = uplink }),
                        Effects.EffectColor(config, 0, 8, t, new EffectFacts(O("{link:true,uplink:false,traffic:0}")) { PeakTraffic = 100, AnimationState = idle })), $"{id} at {t}");
            }
        });
        T("effects: a device retains its full activity level with a busy uplink beside it", e =>
        {
            var config = Effects.DefaultConfig("activity_glow");
            var state = new EffectAnimationState();
            var facts = A("[{link:true,traffic:100},{link:true,uplink:true,traffic:10000}]");
            foreach (var t in new[] { 0, .1, .5, 1, 2, 5 })
                Effects.EffectColor(config, 0, 2, t, new EffectFacts(facts[0]) { PeakTraffic = Effects.ActivityPeak(facts), AnimationState = state });
            e(state.Level > .99);
        });
        T("effects: opted-in uplinks use their measured traffic in all three previews", e =>
        {
            var facts = A("[{link:true,uplink:true,traffic:1000},{link:true,traffic:20},{link:false,traffic:9000}]");
            e(Effects.ActivityPeak(facts, true) == 1000, "peak");
            foreach (var id in new[] { "traffic", "traffic_trails", "activity_glow" })
            {
                var config = Effects.DefaultConfig(id);
                P(config)["include_uplinks"] = true;
                object? Render(JsonNode? port, EffectAnimationState state, double t) =>
                    Effects.EffectColor(config, 0, 2, t, new EffectFacts(port) { PeakTraffic = 1000, AnimationState = state });
                EffectAnimationState optical = new(), device = new(), quiet = new();
                object? active = null, idle = null;
                foreach (var t in new[] { 0, .25, 1, 3 })
                {
                    active = Render(facts[0], optical, t);
                    e(Same(active, Render(With(facts[0], "{uplink:false}"), device, t)), $"{id} uplink equals device at {t}");
                    idle = Render(With(facts[0], "{traffic:0}"), quiet, t);
                }
                e(!Same(active, idle), $"{id} traffic shows");
            }
        });
        T("effects: illustrative activity previews rank ports and change over time", e =>
        {
            var config = Effects.DefaultConfig("activity_glow");
            var states = Enumerable.Range(0, 8).Select(_ => new EffectAnimationState()).ToArray();
            var frames = new[] { 0, .25, 1, 4, 8 }.Select(time =>
            {
                var facts = Effects.DataPreviewFacts(8, time);
                var peak = Effects.ActivityPeak(facts);
                return Enumerable.Range(0, 8).Select(index => Text(Effects.EffectColor(config, index, 8, time, new EffectFacts(facts[index]) { PeakTraffic = peak, AnimationState = states[index] }))).ToList();
            }).ToList();
            e(frames[2].Distinct().Count() > 3, "ranked");
            e(!frames[2].SequenceEqual(frames[4]), "changes");
        });
        T("effects: a speed edit preserves the current level before changing the fade rate", e =>
        {
            var config = Effects.DefaultConfig("activity_glow");
            var state = new EffectAnimationState();
            EffectFacts Facts() => new(O("{link:true,traffic:1}")) { PeakTraffic = 1, AnimationState = state };
            Effects.EffectColor(config, 0, 1, 0, Facts());
            var before = Effects.EffectColor(config, 0, 1, .2, Facts());
            P(config)["speed"] = 4;
            e(Same(Effects.EffectColor(config, 0, 1, .2, Facts()), before), "level kept");
            var after = HexRgb(Effects.EffectColor(config, 0, 1, .3, Facts()));
            e(after.Where((value, index) => value > HexRgb(before)[index]).Any(), "faster fade");
        });

        // tests/breathing-playback.test.mjs (the parts that use the effect renderer)
        T("effects: old Breathing scenes remain Custom while a new supported selection uses shared synchronized pace", e =>
        {
            var state = O("{mode:'live',zones:[{id:'all',ports:[1,2,3,4,5,6,7,8],effect:{id:'gradient',params:{}}}]}");
            e(S(Effects.EffectConfigForSelection("breathing", state), "params.playback") == "custom", "custom without native breathing");
            state["switch"] = O("{native_breathing:true}");
            e(S(Effects.EffectConfigForSelection("breathing", state), "params.playback") == "synchronized", "synchronized");
            e(N(Effects.EffectConfigForSelection("breathing", state), "params.pace") == 2, "default pace");
            var other = Effects.DefaultConfig("breathing");
            Assign(P(other), "{playback:'synchronized',pace:3}");
            state["zones"]!.AsArray().Add(new JsonObject { ["id"] = "other", ["ports"] = A("[8]"), ["effect"] = other });
            e(N(Effects.EffectConfigForSelection("breathing", state), "params.pace") == 3, "shared pace");
            e(!P(Effects.EffectConfigForSelection("comet", state)).ContainsKey("playback"), "other effects");
            var oldCatalog = (JsonArray)EffectCatalog.Catalog.DeepClone();
            var breathing = Js.Objects(oldCatalog).First(effect => Js.Str(effect["id"]) == "breathing");
            breathing["params"] = Js.ArrayOf(Js.Objects(breathing["params"]).Where(param => Js.Str(param["key"]) is not ("playback" or "pace")));
            try
            {
                Effects.SetEffectCatalog(oldCatalog);
                e(!P(Effects.EffectConfigForSelection("breathing", state)).ContainsKey("playback"), "older catalog");
            }
            finally { Effects.SetEffectCatalog(EffectCatalog.Catalog); }
        });
        T("effects: playback controls preserve chosen colors and inactive Custom settings", e =>
        {
            var config = Effects.DefaultConfig("breathing");
            Assign(P(config), "{color:'#2468AC',brightness_color:35,period:7.5,floor:.25,ripple:true,random_color:true,color_transition:11}");
            var before = Js.Clone(P(config));
            var zones = A("[{id:'all',ports:[1,2,3,4,5,6,7,8],effect:{id:'gradient',params:{}}}]");
            config["params"] = Effects.ParamsAfterChange(config, "playback", "synchronized", zones);
            config["params"] = Effects.ParamsAfterChange(config, "pace", 3, zones);
            config["params"] = Effects.ParamsAfterChange(config, "playback", "custom");
            e(Eq(config["params"], With(before, "{pace:3}")));
        });
        T("effects: all three synchronized speeds increase left to right and use their nominal preview periods", e =>
        {
            var config = Effects.DefaultConfig("breathing");
            Assign(P(config), "{playback:'synchronized',color:'#2468AC',brightness_color:50,random_color:true,ripple:true,floor:.9}");
            var param = Effects.SpeedParam(Effects.EffectById("breathing"), config["params"])!;
            foreach (var (pace, period, label) in new[] { (1, 3.08, "Slow"), (2, 2.0, "Medium"), (3, 1.0, "Fast") })
            {
                P(config)["pace"] = pace;
                e(Effects.BreathingPeriod(pace) == period && Effects.SpeedText(param, pace) == label, $"pace {pace} period and label");
                e(Effects.SpeedParamValue(param, Effects.SpeedSliderValue(param, pace)) == pace, $"pace {pace} slider");
                foreach (var time in new[] { 0, period / 4, period / 2, period * .75, period })
                {
                    var colors = Enumerable.Range(0, 8).Select(index => Text(Effects.EffectColor(config, index, 8, time))).ToList();
                    e(colors.Distinct().Count() == 1 && S(Effects.ResolvedColorParams(config, time), "color") == "#123456", $"pace {pace} at {time} in step");
                    if (time == 0 || time == period) e(colors[0] == "#000000", $"pace {pace} dark at {time}");
                    if (time == period / 2) e(colors[0] == "#123456", $"pace {pace} full at {time}");
                }
            }
        });
        T("effects: changing shared pace affects synchronized groups and preserves other group settings", e =>
        {
            JsonObject a = Effects.DefaultConfig("breathing"), b = Effects.DefaultConfig("breathing"), custom = Effects.DefaultConfig("breathing");
            Assign(P(a), "{playback:'synchronized',pace:1,color:'#2468AC'}");
            Assign(P(b), "{playback:'synchronized',pace:1,color:'#ABCDEF',brightness_color:35}");
            var zones = Arr(new JsonObject { ["id"] = "a", ["effect"] = a.DeepClone() }, new JsonObject { ["id"] = "b", ["effect"] = b.DeepClone() },
                new JsonObject { ["id"] = "custom", ["effect"] = custom.DeepClone() }, new JsonObject { ["id"] = "gradient", ["effect"] = Effects.DefaultConfig("gradient") });
            var updated = Js.Spread(a);
            updated["params"] = Effects.ParamsAfterChange(a, "pace", 3, zones);
            var shared = Effects.ShareBreathingPace(zones, updated)!;
            e(Eq(Js.ArrayOf(Js.Items(shared).Select(zone => Get(zone, "effect.params.pace"))), "[3,3,2,null]"), "paces");
            e(S(shared, "1.effect.params.color") == "#ABCDEF" && N(shared, "1.effect.params.brightness_color") == 35, "other settings kept");
            e(N(zones, "0.effect.params.pace") == 1, "the previous state remains available for recovery");
            e(N(Effects.ParamsAfterChange(custom, "playback", "synchronized", shared), "pace") == 3, "joining takes the shared pace");
            var clock = new EffectClock();
            e(Effects.PreviewPhase(clock, Get(shared, "0.effect"), 3.2) == Effects.PreviewPhase(clock, Get(shared, "1.effect"), 3.2), "same phase");
        });
        T("effects: legacy Custom previews retain their timing and random color behavior", e =>
        {
            var config = Effects.DefaultConfig("breathing");
            Assign(P(config), "{period:7.5,floor:.25,ripple:true,random_color:true}");
            var legacy = Js.Clone(config);
            P(legacy).Remove("playback");
            P(legacy).Remove("pace");
            var ok = true;
            foreach (var time in new[] { 0, .125, 1.5, 7.5, 30 })
                for (var index = 0; index < 8; index++) ok &= Same(Effects.EffectColor(legacy, index, 8, time), Effects.EffectColor(config, index, 8, time));
            e(ok);
        });

        // tests/catalog-presentation.test.mjs (the parts that use the effect renderer)
        T("effects: new effect cards have distinct descriptions and animated preview frames", e =>
        {
            var descriptions = new HashSet<string>();
            foreach (var id in AddedEffects)
            {
                var effect = Effects.EffectById(id);
                var config = Effects.DefaultConfig(id);
                descriptions.Add(S(effect, "description")!);
                e(S(effect, "description") != S(Effects.EffectById("static"), "description") && Js.IsTrue(effect["supportsPreview"]), $"{id} presentation");
                var frames = new[] { 0, 1, 3, 7 }.Select(t => string.Join(",", Enumerable.Range(0, 8).Select(index => Text(Effects.EffectColor(config, index, 8, t))))).Distinct().Count();
                e(frames > 1, $"{id} preview changes over time");
            }
            e(descriptions.Count == AddedEffects.Length, "distinct descriptions");
        });
        T("effects: future backend effects keep their metadata without a fabricated static preview", e =>
        {
            var definition = O("{id:'future_effect',name:'New current',description:'A color moves along each port.',category:'data',params:[{key:'color',type:'color',default:'#123456'},{key:'background',type:'color',default:'#abcdef'}]}");
            try
            {
                Effects.SetEffectCatalog(CatalogWith(definition));
                var effect = Effects.EffectById("future_effect");
                e(S(effect, "name") == "New current" && S(effect, "description") == "A color moves along each port." && S(effect, "category") == "Data", "metadata");
                e(Eq(effect["colors"], "['#123456','#abcdef']") && Js.IsFalse(effect["supportsPreview"]), "colors and no preview");
            }
            finally { Effects.SetEffectCatalog(EffectCatalog.Catalog); }
        });
        T("effects: backend presentation values with invalid colors do not reach styles", e =>
        {
            var definition = O("{id:'future\" data-extra=\"bad',name:'<img src=x>',description:'<script>bad()</script>',category:'animated',params:[{key:'color',type:'color',default:'red;display:none'}]}");
            try
            {
                Effects.SetEffectCatalog(CatalogWith(definition));
                e(Eq(Effects.EffectById("future\" data-extra=\"bad")["colors"], "['#98A0AC','#68717D']"));
            }
            finally { Effects.SetEffectCatalog(EffectCatalog.Catalog); }
        });
        T("effects: unknown identifiers never resolve to inherited presentation properties", e =>
        {
            try
            {
                Effects.SetEffectCatalog(CatalogWith(O("{id:'constructor',name:'Future effect',description:'Its own description.',category:'__proto__',params:[]}")));
                var effect = Effects.EffectById("constructor");
                e(Js.IsFalse(effect["supportsPreview"]) && S(effect, "category") == "Other" && S(effect, "description") == "Its own description.");
            }
            finally { Effects.SetEffectCatalog(EffectCatalog.Catalog); }
        });

        // tests/color-brightness.test.mjs (the parts that use the effect renderer)
        T("effects: every explicit effect color has an independent brightness companion", e =>
        {
            foreach (var effect in Js.Objects(EffectCatalog.Catalog))
                foreach (var color in Js.Objects(effect["params"]).Where(param => Js.Str(param["type"]) == "color"))
                {
                    var brightness = Js.Objects(effect["params"]).FirstOrDefault(param => Js.StrictEquals(param["key"], color["brightness_param"]));
                    e(brightness is not null && Js.StrictEquals(brightness["dims"], color["key"]) && S(brightness, "type") == "float" && N(brightness, "default") == 100
                      && N(brightness, "min") == 0 && N(brightness, "max") == 100 && S(brightness, "unit") == "%", $"{S(effect, "id")} {S(color, "key")}");
                }
        });
        T("effects: primary and background brightness are applied independently before blending", e =>
        {
            var config = Effects.DefaultConfig("marquee");
            Assign(P(config), "{color:'#80C040',background:'#204080',spacing:4,brightness_color:50,brightness_background:25}");
            var before = Js.Clone(config);
            var resolved = Effects.ResolvedColorParams(config, 0);
            e(S(resolved, "color") == "#406020" && S(resolved, "background") == "#081020", "resolved");
            e(Same(Effects.EffectColor(config, 0, 8, 0), "#406020") && Same(Effects.EffectColor(config, 2, 8, 0), "#081020"), "rendered");
            e(Eq(config, before), "config unchanged");
        });
        T("effects: random colors retain their own brightness without replacing chosen colors", e =>
        {
            foreach (var effect in Js.Objects(EffectCatalog.Catalog))
                foreach (var color in Js.Objects(effect["params"]).Where(param => Js.Str(param["type"]) == "color"))
                {
                    var id = S(effect, "id")!;
                    var key = S(color, "key")!;
                    var config = Effects.DefaultConfig(id);
                    P(config)[key] = "#2468AC";
                    P(config)[S(color, "random_param")!] = true;
                    var full = S(Effects.ResolvedColorParams(config, 3.5), key);
                    P(config)[S(color, "brightness_param")!] = 37;
                    var ok = S(Effects.ResolvedColorParams(config, 3.5), key) == ColorMath.Dim(full, .37) && S(config, "params." + key) == "#2468AC"
                             && Js.IsTrue(P(config)[S(color, "random_param")!]);
                    P(config)[S(color, "brightness_param")!] = 0;
                    e(ok && S(Effects.ResolvedColorParams(config, 3.5), key) == "#000000", $"{id} {key}");
                }
        });
        T("effects: missing brightness values preserve every legacy effect preview exactly", e =>
        {
            foreach (var effect in Js.Objects(EffectCatalog.Catalog))
            {
                var id = S(effect, "id")!;
                var full = Effects.DefaultConfig(id);
                var legacy = Js.Clone(full);
                foreach (var param in Js.Objects(effect["params"]).Where(param => Js.Truthy(param["dims"]))) P(legacy).Remove(S(param, "key")!);
                var ok = true;
                foreach (var time in new[] { 0, .125, 1.5, 4.7 })
                    for (var port = 0; port < 8; port++) ok &= Same(Effects.EffectColor(legacy, port, 8, time), Effects.EffectColor(full, port, 8, time));
                e(ok, id);
            }
        });
        T("effects: legacy random Starry Night and Strobe colors honor primary brightness", e =>
        {
            foreach (var (id, random) in new[] { ("starry_night", "rainbow"), ("strobe", "random_colors") })
            {
                var config = Effects.DefaultConfig(id);
                P(config)[random] = true;
                P(config)["brightness_color"] = 0;
                if (P(config).ContainsKey("background")) P(config)["background"] = "#000000";
                var ok = true;
                foreach (var time in new[] { .125, .5, 1.5, 4.7 })
                    for (var port = 0; port < 8; port++) ok &= Same(Effects.EffectColor(config, port, 8, time), "#000000");
                e(ok, id);
            }
            var strobe = Effects.DefaultConfig("strobe");
            Assign(P(strobe), "{random_colors:true,rate:1}");
            var full = Effects.EffectColor(strobe, 0, 8, .25);
            P(strobe)["brightness_color"] = 25;
            e(HexRgb(Effects.EffectColor(strobe, 0, 8, .25)).SequenceEqual(ColorMath.HexToRgb(ColorMath.Dim(full as string, .25))), "strobe brightness");
        });
        T("effects: changing color brightness retains other colors and independent controls", e =>
        {
            var config = Effects.DefaultConfig("marquee");
            Assign(P(config), "{color:'#2468AC',random_background:true,brightness_background:70}");
            var parameters = Effects.ParamsAfterChange(config, "brightness_color", 25);
            e(S(parameters, "color") == "#2468AC" && N(parameters, "brightness_background") == 70 && Js.IsTrue(parameters["random_background"]) && N(parameters, "brightness_color") == 25);
        });

        // tests/continuous-preview.test.mjs
        T("effects: slow dim breathing retains distinct brightness samples at 120 Hz", e =>
        {
            var config = EffectRenderer.DefaultConfig("breathing");
            Assign(P(config), "{playback:'custom',period:20,color:'#102040',brightness_color:20,floor:0,ripple:false}");
            var frames = Enumerable.Range(0, 120).Select(index => Rgb(EffectRenderer.EffectColor(config, 0, 8, .4 + index / 120.0))).ToList();
            e(frames.All(color => color.All(double.IsFinite)), "finite");
            e(frames.Any(color => color.Any(channel => channel % 1 != 0)), "fractional");
            e(Enumerable.Range(1, frames.Count - 1).All(index => frames[index][2] > frames[index - 1][2] && frames[index][2] - frames[index - 1][2] < .05), "smooth");
            var quantized = Enumerable.Range(0, 120).Select(index => Text(Effects.EffectColor(config, 0, 8, .4 + index / 120.0))).Distinct().Count();
            e(quantized < frames.Count / 4, "the hex renderer quantizes");
        });
        T("effects: all effect previews preserve finite channel precision and leave configuration untouched", e =>
        {
            foreach (var effect in Js.Objects(EffectCatalog.Catalog))
            {
                var id = S(effect, "id")!;
                var config = EffectRenderer.DefaultConfig(id);
                var before = Js.Clone(config);
                var animation = Enumerable.Range(0, 8).Select(_ => new EffectAnimationState()).ToArray();
                var ok = true;
                foreach (var time in new[] { 0, .1, .5, 1, 2, 5 })
                    for (var port = 0; port < 8; port++)
                    {
                        var color = Rgb(EffectRenderer.EffectColor(config, port, 8, time, new EffectFacts(O("{link:true,traffic:150,speed:2500}")) { Hour = 13, AnimationState = animation[port] }));
                        ok &= color.Length == 3 && color.All(channel => double.IsFinite(channel) && channel >= 0 && channel <= 255);
                    }
                e(ok && Eq(config, before), id);
            }
        });
        T("effects: gallery selection retains Marquee colors, independent brightness and random background", e =>
        {
            var effect = CatalogEffect("marquee");
            var parameters = Js.Spread(EffectRenderer.DefaultConfig("marquee")["params"],
                J("{color:'#6C7BFF',background:'#AF5095',brightness_color:42,brightness_background:63,random_background:true,color_transition:3}"));
            var state = new JsonObject
            {
                ["zones"] = Arr(new JsonObject { ["ports"] = A("[1,2]"), ["effect"] = new JsonObject { ["id"] = "marquee", ["params"] = parameters.DeepClone() } },
                    O("{ports:[3],effect:{id:'marquee',params:{color:'#00FF00'}}}")),
            };
            var chosen = (JsonObject)EffectGallery.GalleryConfig(effect, state, [1]).DeepClone();
            e(Eq(chosen["params"], parameters), "parameters");
            var dimmed = EffectRenderer.Dim(parameters["color"], .42);
            e(Same(Rgb(EffectRenderer.EffectColor(chosen, 0, 8, 1, new EffectFacts { Phase = 0 })), dimmed), "primary at 1");
            e(Same(Rgb(EffectRenderer.EffectColor(chosen, 0, 8, 7, new EffectFacts { Phase = 0 })), dimmed), "primary at 7");
            e(!Same(EffectRenderer.EffectColor(chosen, 1, 8, 1, new EffectFacts { Phase = 0 }), EffectRenderer.EffectColor(chosen, 1, 8, 7, new EffectFacts { Phase = 0 })), "random background");
            var draft = new JsonObject { ["id"] = "marquee", ["params"] = With(parameters, "{color:'#123456',brightness_color:28}") };
            e(Eq(EffectGallery.GalleryConfig(effect, state, [1], new Dictionary<string, JsonObject> { ["marquee"] = draft })["params"], parameters), "applied before remembered");
            var staged = new JsonObject { ["effect"] = With(draft, "{ports:[1]}") };
            e(Eq(EffectGallery.GalleryConfig(effect, state, [1], new Dictionary<string, JsonObject>(), staged)["params"], draft["params"]), "draft first");
            e(S(EffectGallery.GalleryConfig(effect, state, [3]), "params.color") == "#00FF00", "other group");
        });
        T("effects: preview catalog state is isolated from legacy hex rendering", e =>
        {
            var count = Effects.EffectList.Count;
            try
            {
                EffectRenderer.SetEffectCatalog(Arr(CatalogEffect("static")));
                e(EffectRenderer.EffectList.Count == 1 && Effects.EffectList.Count == count, "separate catalogs");
                e(Effects.EffectColor(Effects.DefaultConfig("breathing"), 0, 8, .5) is string text && HexPattern.IsMatch(text), "hex renderer");
            }
            finally { EffectRenderer.SetEffectCatalog(EffectCatalog.Catalog); }
        });
        T("effects: one resolved color set per frame preserves every effect sample", e =>
        {
            foreach (var effect in Js.Objects(EffectCatalog.Catalog))
            {
                var id = S(effect, "id")!;
                var config = EffectRenderer.DefaultConfig(id);
                var ok = true;
                foreach (var time in new[] { .1, .5, 1, 2 })
                {
                    var resolved = EffectRenderer.ResolvedColorParams(config, time);
                    var before = Js.Clone(resolved);
                    for (var port = 0; port < 8; port++)
                    {
                        var direct = EffectRenderer.EffectColor(config, port, 8, time, new EffectFacts(O("{link:true,traffic:150,speed:2500}")) { Hour = 13, AnimationState = new() });
                        var cached = EffectRenderer.EffectColor(config, port, 8, time, new EffectFacts(O("{link:true,traffic:150,speed:2500}")) { Hour = 13, AnimationState = new(), ResolvedParams = resolved });
                        ok &= Same(cached, direct);
                    }
                    ok &= Eq(resolved, before);
                }
                e(ok, id);
            }
        });

        // tests/live-traffic.test.mjs (the parts that use the effect renderer)
        T("effects: Live Traffic has automatic capacity", e =>
        {
            var effect = CatalogEffect("traffic");
            e(S(effect, "name") == "Live Traffic" && !Js.Objects(effect["params"]).Any(param => S(param, "key") is "max_mbps" or "scale"));
            e(Js.IsFalse(Get(Effects.DefaultConfig("traffic"), "params.custom_uplink")), "uplink customization is opt-in");
        });
        T("effects: Live Traffic preview matches automatic capacities, smoothing, offline and uplink behavior", e =>
        {
            var number = 0;
            foreach (var sequence in Js.Objects(Fixture("live-traffic-frames.json")))
            {
                number++;
                var states = Enumerable.Range(0, 4).Select(_ => new EffectAnimationState()).ToArray();
                var precise = Enumerable.Range(0, 4).Select(_ => new EffectAnimationState()).ToArray();
                string? mismatch = null;
                foreach (var frame in Js.Objects(sequence["frames"]))
                {
                    var time = Js.ToNumber(frame["time"]);
                    var colors = frame["colors"]!.AsArray();
                    for (var index = 0; index < colors.Count; index++)
                    {
                        var expected = ColorMath.HexToRgb(Js.Str(colors[index]));
                        var facts = Get(frame, $"facts.{index}");
                        var actual = HexRgb(Effects.EffectColor(sequence["config"], index, 4, time, new EffectFacts(facts) { AnimationState = states[index] }));
                        var rgb = Rgb(EffectRenderer.EffectColor(sequence["config"], index, 4, time, new EffectFacts(facts) { AnimationState = precise[index] }));
                        if (!Close(actual, expected) || !Close(rgb, expected)) mismatch ??= $"port {index + 1}, t={time}: {Text(actual)} / {Text(rgb)} vs {Js.Str(colors[index])}";
                    }
                }
                e(mismatch is null, $"sequence {number}{(mismatch is null ? "" : ": " + mismatch)}");
            }
        });
        T("effects: profile preview combines uplink and default brightness and keeps offline color", e =>
        {
            var parameters = Js.Spread(Effects.DefaultConfig("traffic")["params"], J("{custom_uplink:true,uplink_color:'#80C0FF',brightness_uplink_color:25,offline_color:'#FF0000'}"));
            var scene = O("{brightness:60,zones:[{id:'all',ports:[1,2],effect:{id:'traffic'}}],overrides:{},rules:[],sfp:{}}");
            scene["zones"]![0]!["effect"]!["params"] = parameters;
            var state = O("{ports:[{port:1,facts:{link:true,uplink:true,traffic:1000,speed:1000}},{port:2,facts:{link:false,uplink:true}}]}");
            var colors = ProfilePreview.ProfileColors(scene, state, 0);
            e(new[] { 128.0, 192, 255 }.Select((channel, i) => Math.Abs(colors[1][i] - channel * .25 * .6) < .01).All(ok => ok), "uplink");
            e(colors[2].SequenceEqual([153, 0, 0]), "offline");
        });

        // tests/new-effects.test.mjs (the parts that use the effect renderer)
        T("effects: every new effect is discoverable with its own metadata and shared color controls", e =>
        {
            e(EffectCatalog.Catalog.Count == 33, "catalog size");
            foreach (var id in AddedEffects.Append("activity_glow"))
            {
                var effect = Effects.EffectById(id);
                var colors = Js.Objects(effect["params"]).Where(param => S(param, "type") == "color");
                e(S(effect, "id") == id && Js.Objects(effect["params"]).Any(param => S(param, "key") == "speed")
                  && colors.All(param => Js.Objects(effect["params"]).Any(companion => Js.StrictEquals(companion["key"], param["random_param"]) && Js.StrictEquals(companion["randomizes"], param["key"]))), id);
            }
            e(Js.Objects(Effects.Library).Where(effect => S(effect, "category") == "Data").Select(effect => S(effect, "id")).SequenceEqual(["traffic", "traffic_trails", "activity_glow", "poe_usage"]), "Data effects");
        });

        // tests/poe-usage.test.mjs
        JsonObject PoeConfig() => EffectRenderer.DefaultConfig("poe_usage");
        double[] Settled(JsonObject settings, string facts)
        {
            var state = new EffectAnimationState();
            EffectRenderer.EffectColor(settings, 0, 1, 0, new EffectFacts(J(facts)) { AnimationState = state });
            return Rgb(EffectRenderer.EffectColor(settings, 0, 1, 30, new EffectFacts(J(facts)) { AnimationState = state }));
        }
        T("effects: PoE Usage exposes independent colors and fade speed with automatic power scale", e =>
        {
            var effect = CatalogEffect("poe_usage");
            e(S(effect, "name") == "PoE Usage" && S(effect, "category") == "data" && Js.IsTrue(Effects.EffectById("poe_usage")["supportsPreview"]), "metadata");
            e(S(Param(effect, "speed"), "label") == "Fade speed" && !Js.Objects(effect["params"]).Any(param => Regex.IsMatch(S(param, "key")!, "scale|max_watts|capacity")), "controls");
            foreach (var key in new[] { "low_color", "high_color", "no_power_color" })
                e(S(Param(effect, key), "random_param") == $"random_{key}" && S(Param(effect, key), "brightness_param") == $"brightness_{key}", key);
        });
        T("effects: missing, invalid, zero and non-delivering power use the chosen no-power color", e =>
        {
            var settings = PoeConfig();
            P(settings)["no_power_color"] = "#183142";
            var cases = new[] { "{}", "{poe:'off',poe_power:40}", "{poe:null,poe_power:40}", "{poe:'poe'}" }
                .Concat(new[] { "0", "-1", "null", "NaN", "Infinity", "'15'" }.Select(power => $"{{poe:'poe+',poe_power:{power}}}"));
            foreach (var facts in cases) e(Near(Settled(settings, facts), Rgb("#183142")), facts);
        });
        T("effects: power colors follow the automatic logarithmic 60-watt range", e =>
        {
            var settings = PoeConfig();
            Assign(P(settings), "{low_color:'#113355',high_color:'#BBDDFF'}");
            foreach (var watts in new[] { 0.1, 1, 5, 15, 30, 60, 120 })
                e(Near(Settled(settings, $"{{poe:'poe++',poe_power:{Js.NumberToString(watts)}}}"), EffectRenderer.Mix("#113355", "#BBDDFF", Math.Min(1, Js.Log1p(watts) / Js.Log1p(60)))), $"{watts} W");
        });
        T("effects: PoE usage remains visible when the data link is down or marked as uplink", e =>
            e(Near(Settled(PoeConfig(), "{poe:'poe++',poe_power:25,link:false,uplink:true}"), Settled(PoeConfig(), "{poe:'poe++',poe_power:25,link:true,uplink:false}"))));
        T("effects: power changes and repeated timestamps never jump to a new color", e =>
        {
            var settings = PoeConfig();
            var state = new EffectAnimationState();
            double[] Render(double time, string poe, double power) =>
                Rgb(EffectRenderer.EffectColor(settings, 0, 1, time, new EffectFacts(new JsonObject { ["poe"] = poe, ["poe_power"] = power }) { AnimationState = state }));
            var initial = Render(0, "off", 0);
            e(Near(Render(.1, "poe++", 60), initial), "a change starts from the shown color");
            var rising = Render(.2, "poe++", 60);
            e(!rising.SequenceEqual(initial), "rising");
            e(Near(Render(.2, "off", 0), rising) && Near(Render(.2, "off", 0), rising), "repeated timestamps hold");
            var falling = Render(.3, "off", 0);
            e(falling.Select((channel, index) => Math.Abs(channel - initial[index]) < Math.Abs(rising[index] - initial[index])).All(ok => ok), "falling");
        });
        T("effects: fade-speed changes preserve the current output before using the new speed", e =>
        {
            var settings = PoeConfig();
            var state = new EffectAnimationState();
            EffectFacts Facts(EffectAnimationState animation) => new(O("{poe:'poe++',poe_power:60}")) { AnimationState = animation };
            EffectRenderer.EffectColor(settings, 0, 1, 0, Facts(state));
            var before = Rgb(EffectRenderer.EffectColor(settings, 0, 1, .2, Facts(state)));
            var slower = state.Clone();
            P(settings)["speed"] = 5;
            e(Near(Rgb(EffectRenderer.EffectColor(settings, 0, 1, .2, Facts(state))), before), "output kept");
            var fast = Rgb(EffectRenderer.EffectColor(settings, 0, 1, .3, Facts(state)));
            P(settings)["speed"] = 1;
            var slow = Rgb(EffectRenderer.EffectColor(settings, 0, 1, .3, Facts(slower)));
            e(fast.Where((channel, index) => channel > slow[index]).Any(), "faster");
        });
        T("effects: slow fades retain a distinct fractional color at every 120-Hz sample", e =>
        {
            var settings = PoeConfig();
            P(settings)["speed"] = .1;
            var state = new EffectAnimationState();
            EffectFacts Facts() => new(O("{poe:'poe++',poe_power:60}")) { AnimationState = state };
            EffectRenderer.EffectColor(settings, 0, 1, 0, Facts());
            var frames = Enumerable.Range(0, 120).Select(index => Rgb(EffectRenderer.EffectColor(settings, 0, 1, (index + 1) / 120.0, Facts()))).ToList();
            e(frames.Select(color => Text(color)).Distinct().Count() == 120, "distinct");
            e(frames.All(color => color.Any(channel => channel % 1 != 0)), "fractional");
            e(Enumerable.Range(1, 119).All(index => frames[index].Select((channel, k) => Math.Abs(channel - frames[index - 1][k]) < .4).All(ok => ok)), "smooth");
        });
        T("effects: power samples for gallery demonstrations are smooth, bounded and include idle ports", e =>
        {
            var first = Effects.DataPreviewFacts(8, 3);
            var next = Effects.DataPreviewFacts(8, 3 + 1 / 120.0);
            e(Js.Objects(first).Any(facts => S(facts, "poe") == "off" && N(facts, "poe_power") == 0), "idle port");
            e(Js.Objects(first).Any(facts => S(facts, "poe") != "off" && N(facts, "poe_power") > 0), "powered port");
            e(Enumerable.Range(0, 8).All(index => N(first, $"{index}.poe_power") is >= 0 and <= 60 && Math.Abs(N(next, $"{index}.poe_power")!.Value - N(first, $"{index}.poe_power")!.Value) < .2), "bounded and smooth");
            e(!Js.Objects(first).Select(facts => N(facts, "poe_power")).SequenceEqual(Js.Objects(Effects.DataPreviewFacts(8, 10)).Select(facts => N(facts, "poe_power"))), "changes");
        });
        T("effects: custom colors, per-color brightness and random colors retain the shared renderer contract", e =>
        {
            var settings = PoeConfig();
            Assign(P(settings), "{low_color:'#153859',high_color:'#FDBA7D',brightness_low_color:23,brightness_high_color:61,random_low_color:true}");
            var before = Js.Clone(settings);
            var state = new EffectAnimationState();
            EffectFacts Facts() => new(O("{poe:'poe+',poe_power:10}")) { AnimationState = state };
            EffectRenderer.EffectColor(settings, 0, 1, 0, Facts());
            var actual = Rgb(EffectRenderer.EffectColor(settings, 0, 1, 30, Facts()));
            var colors = EffectRenderer.ResolvedColorParams(settings, 30);
            e(Near(actual, EffectRenderer.Mix(colors["low_color"], colors["high_color"], Js.Log1p(10) / Js.Log1p(60))), "blend");
            e(Eq(settings, before), "settings unchanged");
        });
        T("effects: preview matches backend precise output through rising, falling and reversing power readings", e =>
        {
            var frames = A("""
                [{time:0,powers:[0,1,15,60],colors:[[17,24,39],[17,24,39],[17,24,39],[17,24,39]]},
                 {time:.1,powers:[0,1,15,60],colors:[[17,24,39],[30.40449762217222,44.608876230236405,70.71037666761436],[42.984700968999405,41.03671972434721,66.36166439957533],[51.081057074283684,38.737754410501054,63.56292401750176]]},
                 {time:.4,powers:[0,1,15,60],colors:[[17,24,39],[59.48614092537154,89.32073372022971,139.5074244385261],[99.3596451273527,77.99862758880295,125.72399088722398],[125.0213995747646,70.71195657287117,116.8532609547853]]},
                 {time:1,powers:[0,1,15,60],colors:[[17,24,39],[87.82358791031669,132.88841928776847,206.54396269750677],[154.29196015202783,114.01468395987516,183.5672414287671],[197.0696161700613,101.86794212759406,168.77990354599012]]},
                 {time:1.5,powers:[60,15,1,0],colors:[[17,24,39],[97.14804390249361,147.22439553048866,228.60238070516414],[172.36747536227622,125.86579153573558,202.60060192894298],[220.7771303054945,112.11984013210572,185.86640022017622]]},
                 {time:2.5,powers:[60,15,1,0],colors:[[197.0696161700613,101.86794212759406,168.77990354599012],[169.4299702603627,137.28876594999036,219.37850538389185],[117.16871348071665,152.12838207260594,237.44412501142386],[55.48852833095865,40.64368792690104,66.7394798781684]]}]
                """);
            var settings = PoeConfig();
            var states = Enumerable.Range(0, 4).Select(_ => new EffectAnimationState()).ToArray();
            foreach (var frame in Js.Objects(frames))
                for (var index = 0; index < 4; index++)
                {
                    var color = Rgb(EffectRenderer.EffectColor(settings, index, 4, N(frame, "time")!.Value,
                        new EffectFacts(new JsonObject { ["poe"] = "poe++", ["poe_power"] = Js.Clone(Get(frame, $"powers.{index}")) }) { AnimationState = states[index] }));
                    e(Near(color, Js.Items(Get(frame, $"colors.{index}")).Select(Js.ToNumber).ToArray()), $"port {index + 1} at {N(frame, "time")}");
                }
        });

        // tests/preview-contract.test.mjs
        T("effects: deterministic browser previews match backend frames across options and port counts", e =>
        {
            var frames = Fixture("effect-frames.json");
            var expectedIds = Js.Objects(EffectCatalog.Catalog).Select(effect => S(effect, "id")!)
                .Where(id => id is not ("starry_night" or "lightning" or "traffic" or "traffic_trails" or "activity_glow" or "poe_usage")).Order(StringComparer.Ordinal);
            e(Js.Objects(frames).Select(frame => S(frame, "config.id")!).Distinct().Order(StringComparer.Ordinal).SequenceEqual(expectedIds), "covered effects");
            var failures = 0;
            string? first = null;
            foreach (var frame in Js.Objects(frames))
            {
                var colors = frame["colors"]!.AsArray();
                for (var index = 0; index < colors.Count; index++)
                {
                    var actual = HexRgb(Effects.EffectColor(frame["config"], index, N(frame, "count")!.Value, N(frame, "time")!.Value, new EffectFacts { Hour = N(frame, "hour") }));
                    if (Close(actual, ColorMath.HexToRgb(Js.Str(colors[index])))) continue;
                    failures++;
                    first ??= $"{S(frame, "config.id")} {Js.Stringify(Get(frame, "config.params"))} port {index + 1}/{N(frame, "count")} time {N(frame, "time")}: {Text(actual)} vs {Js.Str(colors[index])}";
                }
            }
            e(failures == 0, failures == 0 ? $"{frames.Count} frames" : $"{failures} differ, first {first}");
        });
        void Sequences(string fixture, string name, bool peak)
        {
            T($"effects: {name} previews match backend frames", e =>
            {
                var sequences = Fixture(fixture);
                e(Js.Objects(sequences).Select(sequence => N(sequence, "count")).Distinct().SequenceEqual([1, 8, 16]), "port counts");
                var failures = 0;
                string? first = null;
                foreach (var sequence in Js.Objects(sequences))
                {
                    var count = (int)N(sequence, "count")!.Value;
                    var states = Enumerable.Range(0, count).Select(_ => new EffectAnimationState()).ToArray();
                    foreach (var frame in Js.Objects(sequence["frames"]))
                    {
                        var colors = frame["colors"]!.AsArray();
                        var peakTraffic = peak ? Effects.ActivityPeak(frame["facts"]!.AsArray()) : (double?)null;
                        for (var index = 0; index < colors.Count; index++)
                        {
                            var actual = HexRgb(Effects.EffectColor(sequence["config"], index, count, N(frame, "time")!.Value,
                                new EffectFacts(Get(frame, $"facts.{index}")) { PeakTraffic = peakTraffic, AnimationState = states[index] }));
                            if (Close(actual, ColorMath.HexToRgb(Js.Str(colors[index])))) continue;
                            failures++;
                            first ??= $"port {index + 1}/{count} at {N(frame, "time")}: {Text(actual)} vs {Js.Str(colors[index])}";
                        }
                    }
                }
                e(failures == 0, failures == 0 ? "frames" : $"{failures} differ, first {first}");
            });
        }
        Sequences("poe-usage-frames.json", "PoE Usage", false);
        Sequences("activity-glow-frames.json", "Activity Glow", true);
        Sequences("traffic-trails-frames.json", "Traffic Trails", false);
        T("effects: every shared color source matches backend HSV fades without changing fixed parameters", e =>
        {
            var frames = Fixture("color-sources.json");
            e(Js.Objects(frames).Select(frame => S(frame, "config.id")).Distinct().Count()
              == Js.Objects(EffectCatalog.Catalog).Count(effect => Js.Objects(effect["params"]).Any(param => S(param, "type") == "color")), "covered effects");
            var failures = 0;
            string? first = null;
            foreach (var frame in Js.Objects(frames))
            {
                var before = Js.Clone(frame["config"]);
                var resolved = Effects.ResolvedColorParams(frame["config"], N(frame, "time")!.Value);
                foreach (var (key, expected) in Js.Entries(frame["colors"]))
                {
                    if (Close(HexRgb(Js.Str(resolved[key])), ColorMath.HexToRgb(Js.Str(expected)))) continue;
                    failures++;
                    first ??= $"{S(frame, "config.id")} {key} at {N(frame, "time")}: {Js.Str(resolved[key])} vs {Js.Str(expected)}";
                }
                if (!Eq(frame["config"], before)) { failures++; first ??= "a fixed color was overwritten"; }
            }
            e(failures == 0, failures == 0 ? "colors" : $"{failures} differ, first {first}");
        });
    }

    // ----- src/rules.mjs -----

    static JsonObject Anomaly(string ports = "[9]") =>
        Js.Spread(Rules.MakeRule("anomaly"), J($"{{id:'sfp-alert',ports:{ports},look:{{color:'#FF0000',style:'solid',brightness:null,rate:1}}}}"));

    static void RuleChecks()
    {
        T("rules: overnight schedules include midnight and exclude their end", e =>
        {
            var rule = O("{field:'time',op:'between',value:['22:00','07:00']}");
            foreach (var (h, m, expected) in new[] { (21, 59, false), (22, 0, true), (0, 0, true), (6, 59, true), (7, 0, false) })
                e(Rules.ConditionMatches(rule, new JsonObject(), new DateTime(2026, 10, 29, h, m, 0)) == expected, $"{h}:{m:00}");
        });
        T("rules: rule animation rates honor the conservative ranges and keep flashes visible", e =>
        {
            foreach (var (style, limits) in Rules.RuleRateLimits)
                e(Rules.RuleRate(style, 99) == limits.Max && Rules.RuleRate(style, -1) == limits.Min && Rules.RuleRate(style) == limits.Default, style);
            var look = O("{style:'flash',color:'#FFFFFF',rate:.5}");
            e(Same(Rules.RenderLook(look, "#000000", .35), "#FFFFFF") && Same(Rules.RenderLook(look, "#000000", .5), "#000000"), "flash");
            e(Same(Rules.RenderLook(O("{style:'effect'}"), "#ABCDEF", 1), "#ABCDEF"), "effect");
        });
        T("rules: unknown port data never triggers negative or offline conditions", e =>
        {
            var date = DateTime.Now;
            e(!Rules.ConditionMatches(O("{field:'link',op:'is',value:false}"), O("{link:null}"), date), "link null");
            e(!Rules.ConditionMatches(O("{field:'poe',op:'is_not',value:'poe'}"), new JsonObject(), date), "poe missing");
        });
        T("rules: rule priority, scope, all/any and enabled state are honored", e =>
        {
            var port = O("{id:2,facts:{link:true,anomaly:true,poe:'poe+'}}");
            var date = DateTime.Now;
            var online = O("{id:'online',enabled:true,match:'all',conditions:[{field:'link',op:'is',value:true}]}");
            var alert = O("{id:'alert',enabled:true,match:'all',conditions:[{field:'anomaly',op:'is',value:true}]}");
            e(S(Rules.MatchingRule(Arr(alert, online), port, date), "id") == "alert", "priority");
            e(S(Rules.MatchingRule(Arr(With(alert, "{ports:[1]}"), online), port, date), "id") == "online", "scope");
            e(S(Rules.MatchingRule(Arr(With(alert, "{enabled:false}"), online), port, date), "id") == "online", "enabled");
            e(S(Rules.MatchingRule(Arr(With(alert, "{match:'any',conditions:[{field:'link',op:'is',value:false},{field:'anomaly',op:'is',value:true}]}")), port, date), "id") == "alert", "any");
        });
        T("rules: rule editor labels identify scopes consistently", e =>
        {
            e(Rules.RulePortLabel(null) == "All copper ports", "all");
            e(Rules.RulePortLabel(A("[9,10]")) == "Port 9, Port 10", "list");
        });
        T("rules: SFP preview uses explicit first-match rules", e =>
        {
            var rule = Anomaly();
            var item = O("{port:9,mode:'color',color:'#0000FF',anomaly:true,link:true,brightness:50,color_brightness:100,facts:{}}");
            var rules = Arr(Anomaly("null"), rule, Anomaly());
            e(S(Rules.MatchingSfpRule(rules, item), "id") == S(rule, "id"), "first scoped rule");
            item["anomaly"] = null;
            e(Rules.MatchingSfpRule(rules, item) is null, "unknown anomaly");
        });
    }

    // ----- preview/port-display.mjs (tests/preview-display.test.mjs) -----

    static JsonObject LevelRule(string brightness) =>
        O($"{{enabled:true,ports:[1],conditions:[],look:{{style:'solid',color:'#4da6ff',brightness:{brightness}}}}}");

    static void PortDisplayChecks()
    {
        T("display: composed display channels retain fractional intensity without decoding twice", e =>
        {
            double[] display = [39.27, 84.66, 130.05];
            e(PortDisplay.PortDisplayRgb(O("{display_rgb:[39.27,84.66,130.05],preview:'#04173a'}")).SequenceEqual(display), "reported");
            e(Near(PortDisplay.ManualDisplayRgb("#4da6ff", 51, 20), display, 1e-9), "manual");
        });
        T("display: legacy hardware preview is inverse-gamma decoded once", e =>
        {
            e(Near(PortDisplay.PortDisplayRgb(O("{preview:'#4080ff'}"), 2.2), new[] { 64.0, 128, 255 }.Select(channel => 255 * Math.Pow(channel / 255, 1 / 2.2)).ToArray(), 1e-9), "preview");
            e(Near(PortDisplay.PortDisplayRgb(O("{output:{color:'#4080ff',level:50}}"), 2), new[] { 64.0, 128, 255 }.Select(channel => 255 * Math.Pow(channel / 255 * .5, .5)).ToArray(), 1e-9), "output level");
        });
        T("display: unmanaged or explicitly unknown display does not show a saved configured color", e =>
        {
            foreach (var port in new[] { "{display_rgb:null,color:'#ffffff',preview:'#ffffff'}", "{mode:'unmanaged',color:'#ffffff'}", "{mode:'off',preview:'#ffffff'}", "{display_rgb:[NaN,2,3],preview:'#ffffff'}" })
                e(PortDisplay.PortDisplayRgb(O(port)).SequenceEqual([0, 0, 0]), port);
            e(PortDisplay.PortDisplayRgb(O("{mode:'unmanaged',rule:{id:'alert'},display_rgb:[20,30,40]}")).SequenceEqual([20, 30, 40]), "rule on unmanaged");
        });
        T("display: manual brightness replaces master while null inherits it", e =>
        {
            e(Near(PortDisplay.ManualDisplayRgb("#4da6ff", 51, 5), [39.27, 84.66, 130.05], 1e-9), "own level");
            e(Near(PortDisplay.ManualDisplayRgb("#4da6ff", null, 20), [15.4, 33.2, 51], 1e-9), "inherited");
            e(PortDisplay.ManualDisplayRgb("#4da6ff", 0, 100).SequenceEqual([0, 0, 0]), "zero");
        });
        T("display: master preview scales composed display but preserves explicit override and rule levels", e =>
        {
            var port = O("{port:1,display_rgb:[39.27,84.66,130.05],color:'#4da6ff'}");
            double[] display = [39.27, 84.66, 130.05];
            e(Near(PortDisplay.MasterDisplayRgb(port, 25.5, 51), [19.635, 42.33, 65.025], 1e-9), "scaled");
            e(PortDisplay.MasterDisplayRgb(With(port, "{override:{brightness:51}}"), 0, 51).SequenceEqual(display), "override level");
            e(PortDisplay.MasterDisplayRgb(port, 0, 51, Arr(LevelRule("51"))).SequenceEqual(display), "rule level");
            e(PortDisplay.MasterDisplayRgb(With(port, "{override:{brightness:null}}"), 0, 51, Arr(LevelRule("51"))).SequenceEqual(display), "inherited override with rule level");
            e(Near(PortDisplay.MasterDisplayRgb(port, 25.5, 51, Arr(LevelRule("null"))), [19.635, 42.33, 65.025], 1e-9), "rule without level");
        });
        T("display: SFP master preview respects scoped rule ownership and explicit brightness", e =>
        {
            var port = O("{port:9,mode:'unmanaged',display_rgb:[39.27,84.66,130.05],facts:{}}");
            var rule = With(LevelRule("null"), "{ports:[9]}");
            e(Near(PortDisplay.MasterDisplayRgb(port, 25.5, 51, Arr(rule)), [19.635, 42.33, 65.025], 1e-9), "scoped rule");
            e(PortDisplay.MasterDisplayRgb(port, 0, 51, Arr(With(rule, "{look:{style:'solid',color:'#4da6ff',brightness:51}}"))).SequenceEqual([39.27, 84.66, 130.05]), "rule level");
            e(PortDisplay.MasterDisplayRgb(With(port, "{display_rgb:null}"), 100, 51).SequenceEqual([0, 0, 0]), "unknown display");
        });
    }

    // ----- preview/port-topology.mjs (tests/port-topology.test.mjs) -----

    static bool Layout(PortLayout? layout, int[] copper, int[] sfp) => layout is not null && layout.Copper.SequenceEqual(copper) && layout.Sfp.SequenceEqual(sfp);

    static void TopologyChecks()
    {
        T("topology: reported lighting IDs take precedence over physical port metadata", e =>
        {
            var state = O("{num_ports:24,ports:[{port:3},{port:1}],sfp:[],hardware_profile:{copper_ports:[1,2,3,4],optical_ports:[25,26],stack_ports:[27,28]}}");
            e(Layout(PortTopology.LightingPorts(state), [1, 3], []), "reported");
            e(Layout(PortTopology.LightingPorts(new JsonObject { ["hardware_profile"] = Js.Clone(state["hardware_profile"]) }), [], []), "metadata only");
        });
        T("topology: optical-only switches do not acquire eight imaginary Ethernet ports", e =>
        {
            var state = new JsonObject { ["num_ports"] = 0, ["sfp_ports"] = Js.NumberArray(Enumerable.Range(1, 32)) };
            e(Layout(PortTopology.LightingPorts(state), [], Enumerable.Range(1, 32).ToArray()), "ports");
            var scopes = PortMap.PortScopeOptions(state);
            e(Js.Objects(scopes).Select(scope => S(scope, "label")).SequenceEqual(["All ports"]) && Js.Length(Get(scopes, "0.ports")) == 32, "scopes");
        });
        T("topology: invalid and duplicated IDs never become selectable output ports", e =>
        {
            e(Layout(PortTopology.LightingPorts(O("{num_ports:2,sfp_ports:[0,2,3,3,4,257,'5']}")), [1, 2], [3, 4]), "invalid IDs");
            e(Layout(PortTopology.LightingPorts(O("{num_ports:-2,sfp:[]}")), [], []), "negative count");
            e(Layout(PortTopology.LightingPorts(O("{switch:{num_ports:0,sfp_ports:[1,2]}}")), [], [1, 2]), "switch fields");
        });
        T("topology: saved topology requires unique and disjoint port roles", e =>
        {
            var topology = O("{version:1,model:'sample',copper_ports:[2,1],sfp_ports:[3,4]}");
            e(Layout(PortTopology.SceneTopology(new JsonObject { ["topology"] = topology.DeepClone() }), [1, 2], [3, 4]), "valid");
            foreach (var patch in new[] { "{version:2}", "{copper_ports:[1,1]}", "{sfp_ports:[2,3]}", "{sfp_ports:[0]}", "{sfp_ports:null}" })
                e(PortTopology.SceneTopology(new JsonObject { ["topology"] = With(topology, patch) }) is null, patch);
            e(Layout(PortTopology.SceneTopology(O("{topology:{version:1,copper_ports:[],sfp_ports:[1,2]}}")), [], [1, 2]), "optical only");
        });
        T("topology: dense previews balance complete rows without changing the number of displayed ports", e =>
        {
            e(PortTopology.PreviewGrid(1) == (1, 1) && PortTopology.PreviewGrid(16) == (2, 8), "small");
            foreach (var count in new[] { 24, 48, 52, 54, 64 })
            {
                var (rows, columns) = PortTopology.PreviewGrid(count);
                e(columns <= 12 && rows * columns >= count && rows * columns - count < rows, count.ToString(CultureInfo.InvariantCulture));
            }
        });
        T("topology: long selections use exact ranges and preserve gaps and single ports", e =>
        {
            e(PortTopology.PortRanges(A("[4]")) == "4" && PortTopology.PortRanges(A("[9,10]")) == "9, 10", "short");
            e(PortTopology.PortRanges(A("[5,1,2,3,8,9,10,10]")) == "1–3, 5, 8–10", "gaps");
            e(PortTopology.PortRanges(Enumerable.Range(1, 48)) == "1–48", "full");
        });
    }

    // ----- preview/port-map.mjs (tests/port-map.test.mjs) -----

    static bool Lines(JsonObject label, params string[] lines) => Js.Items(label["lines"]).Select(Js.Str).SequenceEqual(lines);

    static void PortMapChecks()
    {
        T("port map: a multi-client port shows every reported name in stable order without choosing a primary", e =>
        {
            var port = O("{port:3,name:'2 clients',controller:{count:2,clients:[{name:'Workstation'},{name:'Home Assistant'}]}}");
            var shown = PortMap.PortLabel(port);
            e(Lines(shown, "Home Assistant", "Workstation") && S(shown, "text") == "Home Assistant, Workstation", "sorted");
            var reversed = Js.Clone(port);
            reversed["controller"]!["clients"] = Js.ArrayOf(Js.Items(port.At("controller.clients")).Reverse());
            e(Eq(PortMap.PortLabel(reversed), shown), "stable");
            e(S(port, "controller.clients.0.name") == "Workstation", "input unchanged");
        });
        T("port map: unknown and truncated clients retain honest counts and do not invent identities", e =>
        {
            e(Lines(PortMap.PortLabel(O("{port:3,controller:{count:4,clients:[{name:'Host'},{name:null}],clients_truncated:true}}")), "Host", "Unnamed device", "+2 more"), "truncated");
            e(Lines(PortMap.PortLabel(O("{port:2,controller:{count:2,clients:[{name:'Same'},{name:'Same'}]}}")), "Same", "Same"), "duplicates");
            e(S(PortMap.PortLabel(O("{port:2,label:'Desk'}")), "text") == "Desk", "label");
            e(S(PortMap.PortLabel(O("{port:9}"), true), "text") == "SFP 9" && S(PortMap.PortLabel(O("{port:4}")), "text") == "Port 4", "fallback");
            e(S(PortMap.PortLabel(O("{port:1,name:'<img src=x onerror=alert(1)>'}")), "text") == "<img src=x onerror=alert(1)>", "text kept");
        });
        T("port map: assigned controller names lead the tile while associated hostnames stay in details", e =>
        {
            foreach (var number in new[] { 3, 10, 24, 28 })
            {
                var port = O("{controller:{count:2,clients:[{name:'server',name_source:'hostname'},{name:'Travel router',name_source:'assigned'}]}}");
                port["port"] = number;
                e(Eq(PortMap.PortLabel(port), "{lines:['Travel router'],text:'Travel router'}"), $"{number} assigned");
                e(Eq(port.At("controller.clients"), "[{name:'server',name_source:'hostname'},{name:'Travel router',name_source:'assigned'}]"), $"{number} unchanged");
                var clients = port.At("controller.clients")!.AsArray();
                clients[0] = O("{name:'Home automation',name_source:'assigned'}");
                e(S(PortMap.PortLabel(port), "text") == "Home automation, Travel router", $"{number} two assigned");
                clients[1] = O("{name:'travel-router',name_source:'hostname'}");
                clients[0] = O("{name:'server',name_source:'hostname'}");
                e(S(PortMap.PortLabel(port), "text") == "server, travel-router", $"{number} hostnames");
                clients[0] = O("{name:' ',name_source:'assigned'}");
                e(S(PortMap.PortLabel(port), "text") == "travel-router, Unnamed device", $"{number} blank");
                port["link"] = O("{uplink:true}");
                e(S(PortMap.PortLabel(port), "text") == "Uplink", $"{number} uplink");
            }
        });
        T("port map: detected uplinks take precedence on any port and disappear when detection clears", e =>
        {
            foreach (var number in new[] { 1, 3, 5, 8, 9, 24 })
            {
                var details = O("{name:'Device',controller:{clients:[{name:'Downstream'}]}}");
                details["port"] = number;
                foreach (var detection in new[] { "{link:{uplink:true}}", "{facts:{uplink:true}}" })
                    e(PortMap.IsUplink(With(details, detection)) && Eq(PortMap.PortLabel(With(details, detection)), "{lines:['Uplink'],text:'Uplink'}"), $"{number} {detection}");
                e(S(PortMap.PortLabel(With(details, "{facts:{uplink:false}}")), "text") == "Downstream", $"{number} cleared");
            }
        });
        T("port map: link reporting distinguishes missing data from disconnected and maps UniFi speed classes", e =>
        {
            e(PortMap.LinkSpeeds.SequenceEqual([(100, "FE"), (1000, "GbE"), (2500, "2.5 GbE"), (5000, "5 GbE"), (10000, "10 GbE"), (25000, "25 GbE"), (40000, "40 GbE"), (100000, "100 GbE")]), "speeds");
            foreach (var port in new[] { "{}", "{link:null}", "{facts:{link:null}}", "{link:{link_up:null}}" }) e(S(PortMap.LinkStatus(O(port)), "key") == "unknown", port);
            e(S(PortMap.LinkStatus(O("{link:{link_up:false},facts:{link:true}}")), "key") == "offline", "offline");
            e(S(PortMap.LinkStatus(O("{link:{admin_up:false,link_up:true}}")), "key") == "disabled", "disabled");
            foreach (var (speed, label) in PortMap.LinkSpeeds) e(S(PortMap.LinkStatus(O($"{{facts:{{link:true,speed:{speed}}}}}")), "label") == label, label);
            e(N(PortMap.LinkStatus(O("{link:{link_up:true,speed_mbps:10}}")), "key") == 100, "10 Mbps");
            e(S(PortMap.LinkStatus(O("{link:{link_up:true,speed_mbps:25000}}")), "label") == "25 GbE", "25 GbE");
            e(S(PortMap.LinkStatus(O("{link:true,facts:{speed:10000}}")), "label") == "10 GbE", "boolean link");
        });
        T("port map: PoE telemetry never turns unknown watts into a measured zero or off state", e =>
        {
            foreach (var port in new[] { "{}", "{poe:null}", "{poe:{}}" }) e(S(PortMap.PortPower(O(port)), "description") == "Not reported", port);
            e(S(PortMap.PortPower(O("{poe:{delivering:false,power_w:null}}")), "description") == "Not supplying power", "not delivering");
            foreach (var power in new[] { "null", "undefined", "NaN", "Infinity", "-1", "'4'" })
                e(S(PortMap.PortPower(O($"{{poe:{{delivering:true,power_w:{power}}}}}")), "description") == "Power usage not reported", power);
            e(S(PortMap.PortPower(O("{poe:{delivering:true,power_w:0}}")), "watts") == "0.0 W" && S(PortMap.PortPower(O("{poe:{delivering:true,power_w:17.78}}")), "watts") == "17.8 W", "watts");
        });
        T("port map: scope labels cannot confuse the legacy copper group with all physical ports", e =>
        {
            var state = O("{ports:[{port:1},{port:2},{port:3},{port:4},{port:5},{port:6},{port:7},{port:8}],sfp:[{port:9},{port:10}],zones:[{id:'all',name:'All ports',ports:[1,2,3,4,5,6,7,8]}]}");
            var options = PortMap.PortScopeOptions(state);
            e(Js.Objects(options).Select(option => S(option, "label")).SequenceEqual(["All ports", "All Ethernet ports", "SFP ports"]), "labels");
            e(Js.Length(Get(options, "0.ports")) == 10 && Js.Length(Get(options, "1.ports")) == 8, "ports");
            state["zones"]!.AsArray().Add(O("{id:'custom',name:'All ports',ports:[1,2]}"));
            options = PortMap.PortScopeOptions(state);
            e(S(Js.Objects(options).First(option => S(option, "value") == "custom"), "label") == "Group: All ports" && S(state, "zones.1.name") == "All ports", "reserved name");
            state["zones"]![0]!["name"] = "My setup";
            e(Js.Objects(PortMap.PortScopeOptions(state)).Any(option => S(option, "label") == "My setup"), "renamed");
            e(Js.Objects(PortMap.PortScopeOptions(With(state, "{sfp:[],zones:[]}"))).Select(option => S(option, "label")).SequenceEqual(["All ports"]), "copper only");
        });
        T("port map: group names stop at the backend limit before submitting a mutation", e =>
        {
            e(PortMap.GroupNameError(new string('A', 40)) == "", "40");
            foreach (var length in new[] { 41, 60 }) e(PortMap.GroupNameError(new string('A', length)).Contains("40 characters"), length.ToString(CultureInfo.InvariantCulture));
            e(PortMap.GroupNameError("   ").Contains("Enter a name"), "blank");
        });
        T("port map: selection supports a solo port and additive interaction without mutating the previous set", e =>
        {
            int[] ports = [1, 2, 3, 4, 9, 10];
            var selected = new List<int> { 1, 2 };
            bool Is((List<int> Ports, int? Anchor) result, int[] expected, int? anchor) => result.Ports.SequenceEqual(expected) && result.Anchor == anchor;
            e(Is(PortMap.SelectPort(ports, selected, null, 3), [3], 3), "solo");
            e(Is(PortMap.SelectPort(ports, selected, null, 3, additive: true), [1, 2, 3], 3), "add");
            e(Is(PortMap.SelectPort(ports, selected, null, 2, additive: true), [1], 2), "remove");
            e(selected.SequenceEqual([1, 2]), "unchanged");
            e(Is(PortMap.SelectPort(ports, selected, 1, 99), [1, 2], 1), "unknown port");
        });
        T("port map: Shift selects the visible range in either direction while retaining its anchor", e =>
        {
            int[] ports = [1, 2, 3, 4, 9, 10];
            var selected = new List<int> { 1, 3 };
            bool Is((List<int> Ports, int? Anchor) result, int[] expected, int? anchor) => result.Ports.SequenceEqual(expected) && result.Anchor == anchor;
            e(Is(PortMap.SelectPort(ports, selected, 3, 10, range: true), [3, 4, 9, 10], 3), "forward");
            e(Is(PortMap.SelectPort(ports, selected, 9, 2, range: true), [2, 3, 4, 9], 9), "backward");
            e(Is(PortMap.SelectPort(ports, selected, 3, 9, additive: true, range: true), [1, 3, 4, 9], 3), "additive");
            e(Is(PortMap.SelectPort(ports, selected, null, 4, range: true), [4], 4), "no anchor");
        });
        T("port map: selection hints reflect the platform and expose the tap-based mode", e =>
        {
            e(PortMap.SelectionHint("MacIntel").StartsWith("Command-click") && PortMap.SelectionHint("macOS").StartsWith("Command-click"), "mac");
            e(PortMap.SelectionHint("Win32").StartsWith("Ctrl-click") && PortMap.SelectionHint("Linux").StartsWith("Ctrl-click"), "others");
            e(PortMap.SelectionHint("Win32", true).StartsWith("Select ports to add or remove them"), "multiple");
            foreach (var platform in new[] { "iPhone", "iPad", "Android", "Win32", "MacIntel" })
                e(PortMap.SelectionHint(platform, false, true).Contains("Use Select multiple") && PortMap.SelectionHint(platform, true, true) == "Tap ports to add or remove them.", platform);
            e(PortMap.SelectionHint("iPhone").StartsWith("Tap"), "phone");
        });
        T("port map: a reported controller name fills a missing switch model without inventing an identity", e =>
        {
            e(PortMap.SwitchTitle(O("{switch:{model:'Model'},controller:{switch_name:'Office'}}")) == "Model", "model");
            e(PortMap.SwitchTitle(O("{switch:{model:' '},controller:{switch_name:'Office'}}")) == "Office", "controller name");
            e(PortMap.SwitchTitle(O("{controller:{switch_name:null}}")) == "UniFi switch", "fallback");
        });
        T("port map: scope options use reported ports rather than assuming eight copper and two SFP", e =>
            e(Eq(Js.ArrayOf(Js.Objects(PortMap.PortScopeOptions(O("{ports:[{port:2},{port:8},{port:24}],sfp:[{port:25},{port:26},{port:27},{port:28}],zones:[]}"))).Select(option => option["ports"])),
                "[[2,8,24,25,26,27,28],[2,8,24],[25,26,27,28]]")));
        T("port map: chosen port names override client and uplink text while preserving uplink detection", e =>
        {
            var port = O("{port:9,facts:{uplink:true},name:'Client',controller:{clients:[{name:'Client'}]},display_name:'Rack fiber',name_mode:'manual'}");
            e(S(PortMap.PortLabel(port, true), "text") == "Rack fiber" && PortMap.IsUplink(port), "chosen name");
            e(S(PortMap.PortLabel(With(port, "{display_name:null}"), true), "text") == "SFP 9", "manual fallback");
            e(S(PortMap.PortLabel(With(port, "{name_mode:'unifi',display_name:'UniFi fiber'}"), true), "text") == "UniFi fiber", "UniFi name");
            e(S(PortMap.PortLabel(With(port, "{name_mode:'unifi',display_name:null}"), true), "text") == "Uplink", "uplink");
            e(S(PortMap.PortLabel(With(port, "{display_name:'<img src=x onerror=alert(1)>'}"), true), "text") == "<img src=x onerror=alert(1)>", "text kept");
        });
    }

    // ----- preview/effect-draft.mjs (tests/preview-effect-draft.test.mjs) -----

    static JsonObject Cfg(string id, string ports, string parameters = "{}", string extra = "{}") =>
        Js.Spread(new JsonObject { ["id"] = id, ["ports"] = J(ports), ["params"] = J(parameters) }, J(extra));

    static JsonArray EightPorts() => Js.ArrayOf(Enumerable.Range(1, 8).Select(index => (JsonNode?)new JsonObject { ["port"] = index, ["color"] = "#FFFFFF" }));

    static JsonObject DraftScene() => O("""
        {brightness:100,mode:'running',zones:[{id:'all',name:'All Ethernet ports',ports:[1,2,3,4,5,6,7,8],effect:{id:'static',params:{color:'#FFFFFF'}}}],
         rules:[],overrides:{2:{color:'#FF0000',brightness:60},6:{color:'#00FF00',brightness:80}},sfp:{},sfp_sync:{mode:'independent',source_port:1}}
        """);

    static JsonObject DraftState(JsonObject scene)
    {
        var state = Js.Clone(scene);
        state["ports"] = EightPorts();
        state["sfp"] = new JsonArray();
        return state;
    }

    static JsonArray DraftSummary(JsonNode? draft) =>
        Js.ArrayOf(EffectDraft.EffectDrafts(draft).Select(effect => (JsonNode?)new JsonArray(Js.Clone(Get(effect, "id")), Js.Clone(Get(effect, "ports")), Js.Clone(Get(effect, "zoneId")))));

    static JsonObject? ZoneWith(JsonNode? scene, int port) => Js.Objects(Get(scene, "zones")).FirstOrDefault(zone => Js.Includes(zone["ports"], port));

    static void EffectDraftChecks()
    {
        T("effect draft: a mixed activity group and return to SFP lighting round trip without changing saved optical settings", e =>
        {
            var original = DraftScene();
            original["sfp"] = O("{9:{mode:'off',color:'#ff0000'},10:{mode:'color',effect:'pulse',period:6}}");
            original["sfp_sync"] = O("{mode:'together',source_port:2}");
            JsonObject Runtime(JsonObject saved)
            {
                var state = Js.Spread(saved);
                state["ports"] = EightPorts();
                state["sfp"] = Js.ArrayOf(Js.Entries(saved["sfp"]).Select(entry =>
                {
                    var light = Js.Spread(entry.Value);
                    light["port"] = Js.StringToNumber(entry.Key);
                    return (JsonNode?)light;
                }));
                return state;
            }
            var staged = EffectDraft.StageEffectDraft(new JsonObject(), Cfg("activity_glow", "[2,10]", "{}", "{clearOverrides:true}"), "activity");
            var joined = LightingState.LightingSnapshot(original, Runtime(original), staged);
            JsonObject? Activity(JsonNode scene) => Js.Objects(scene["zones"]).FirstOrDefault(zone => S(zone, "id") == "activity");
            e(Eq(Activity(joined)?["ports"], "[2,10]"), "joined ports");
            e(Eq(joined["sfp"], original["sfp"]) && Eq(joined["sfp_sync"], original["sfp_sync"]), "optical settings kept");
            e(!Js.Has(joined["overrides"], "2") && Eq(joined.At("overrides.6"), original.At("overrides.6")), "fixed colors");
            var detach = EffectDraft.LeaveSfpEffect(new JsonObject(), [10]);
            e(EffectDraft.PortEffect(Runtime(joined), detach, 10) is null, "detached preview");
            var left = LightingState.LightingSnapshot(joined, Runtime(joined), detach);
            e(Eq(Activity(left)?["ports"], "[2]") && Eq(left["sfp"], original["sfp"]) && Eq(left["sfp_sync"], original["sfp_sync"]), "left");
            var optical = EffectDraft.StageEffectDraft(new JsonObject(), Cfg("gradient", "[9]"), "optical");
            var opticalOnly = LightingState.LightingSnapshot(left, Runtime(left), optical);
            var removed = LightingState.LightingSnapshot(opticalOnly, Runtime(opticalOnly), EffectDraft.LeaveSfpEffect(new JsonObject(), [9]));
            e(Eq(removed, left), "optical-only group removed");
            var restored = EffectDraft.StageEffectDraft(detach, Cfg("activity_glow", "[2,10]"), "activity");
            e(Eq(LightingState.LightingSnapshot(joined, Runtime(joined), restored), joined), "restored");
            e(Throws(() => LightingState.LightingSnapshot(joined, Runtime(joined), O("{sfpDetached:[2]}")), "available SFP port"), "copper cannot detach");
            e(Throws(() => LightingState.LightingSnapshot(joined, Runtime(joined), new JsonObject { ["effect"] = Cfg("static", "[11]") }), "available lighting ports"), "unknown port");
        });
        T("effect draft: different effect scopes remain staged together without changing earlier parameters or other edits", e =>
        {
            var original = O("{brightness:65,sfp:{9:{mode:'color',color:'#FF00FF'}},colors:{8:{color:'#112233',brightness:25}}}");
            var first = Cfg("breathing", "[3,1,3]", "{color:'#0066FF'}");
            var before = new JsonArray(original.DeepClone(), first.DeepClone());
            var one = EffectDraft.StageEffectDraft(original, first, "blue-pair");
            var two = EffectDraft.StageEffectDraft(one, Cfg("comet", "[5,7]", "{color:'#FF00AA'}"), "pink-pair");
            e(Eq(DraftSummary(two), "[['breathing',[1,3],'blue-pair'],['comet',[5,7],'pink-pair']]"), "scopes");
            e(Eq(EffectDraft.EffectDraftForPort(two, 1)?["params"], "{color:'#0066FF'}") && S(EffectDraft.EffectDraftForPort(two, 7), "id") == "comet"
              && EffectDraft.EffectDraftForPort(two, 2) is null, "lookups");
            e(N(two, "brightness") == 65 && Eq(two["sfp"], original["sfp"]) && Eq(two["colors"], original["colors"]), "other edits");
            e(Eq(new JsonArray(original.DeepClone(), first.DeepClone()), before) && EffectDraft.EffectDrafts(one).Count == 1, "inputs unchanged");
        });
        T("effect draft: an overlapping effect changes only its target ports and keeps the other staged scopes", e =>
        {
            var one = EffectDraft.StageEffectDraft(new JsonObject(), Cfg("breathing", "[1,2,3]", "{color:'#0066FF'}"), "first");
            var two = EffectDraft.StageEffectDraft(one, Cfg("gradient", "[5,6]"), "second");
            var before = Js.Clone(two);
            var result = EffectDraft.StageEffectDraft(two, Cfg("comet", "[2,3,4]"), "overlap");
            e(Eq(DraftSummary(result), "[['breathing',[1],'first'],['gradient',[5,6],'second'],['comet',[2,3,4],'overlap']]"), "scopes");
            e(new[] { 2, 3, 4 }.All(id => S(EffectDraft.EffectDraftForPort(result, id), "id") == "comet"), "targets");
            e(S(EffectDraft.EffectDraftForPort(result, 1), "id") == "breathing" && S(EffectDraft.EffectDraftForPort(result, 6), "id") == "gradient", "others");
            e(Eq(two, before), "input unchanged");
        });
        T("effect draft: editing an exact scope retains its group identity and override choice until explicitly changed", e =>
        {
            var initial = EffectDraft.StageEffectDraft(new JsonObject(), Cfg("comet", "[2,1]", "{color:'#FF00AA'}", "{clearOverrides:true}"), "stable-group");
            var edited = EffectDraft.StageEffectDraft(initial, Cfg("breathing", "[1,2]", "{color:'#0066FF'}"), "unused-id");
            e(EffectDraft.EffectDrafts(edited).Count == 1 && S(edited, "effects.0.zoneId") == "stable-group", "identity");
            e(Js.IsTrue(edited.At("effects.0.clearOverrides")) && S(edited, "effects.0.id") == "breathing", "override choice");
            var retained = EffectDraft.StageEffectDraft(edited, Cfg("breathing", "[2,1]", "{color:'#AA00FF'}", "{clearOverrides:false}"), "other-id");
            e(S(retained, "effects.0.zoneId") == "stable-group" && Js.IsFalse(retained.At("effects.0.clearOverrides")), "explicit change");
        });
        T("effect draft: replacing fixed colors removes only targeted draft overrides and follow-effect entries", e =>
        {
            var draft = O("{colors:{1:{color:'#111111'},3:{color:'#333333'},8:{color:'#888888'}},returnToEffect:[2,5],brightness:35}");
            var before = Js.Clone(draft);
            var result = EffectDraft.StageEffectDraft(draft, Cfg("breathing", "[1,2,3]", "{}", "{clearOverrides:true}"), "selection");
            e(Eq(result["colors"], "{8:{color:'#888888'}}") && Eq(result["returnToEffect"], "[5]") && N(result, "brightness") == 35, "replaced");
            e(Eq(draft, before), "input unchanged");
            var keeping = EffectDraft.StageEffectDraft(draft, Cfg("breathing", "[1,2,3]", "{}", "{clearOverrides:false}"), "selection");
            e(Eq(keeping["colors"], draft["colors"]) && Eq(keeping["returnToEffect"], draft["returnToEffect"]), "kept");
        });
        T("effect draft: legacy singular effect drafts remain readable and migrate without losing unrelated changes", e =>
        {
            var legacy = new JsonObject { ["effect"] = Cfg("breathing", "[1,2]", "{color:'#0066FF'}", "{zoneId:'legacy',clearOverrides:true}"), ["brightness"] = 70 };
            e(S(EffectDraft.EffectDraftForPort(legacy, 2), "id") == "breathing" && EffectDraft.EffectDrafts(legacy).Count == 1, "readable");
            var result = EffectDraft.StageEffectDraft(legacy, Cfg("comet", "[4,6]"), "new");
            e(!result.ContainsKey("effect") && N(result, "brightness") == 70, "migrated");
            e(Eq(DraftSummary(result), "[['breathing',[1,2],'legacy'],['comet',[4,6],'new']]"), "scopes");
            e(Js.IsTrue(EffectDraft.EffectDraftForPort(result, 2)?["clearOverrides"]) && S(legacy, "effect.zoneId") == "legacy", "kept");
            var combined = Js.Spread(legacy);
            combined["effects"] = Arr(Cfg("gradient", "[2,3]"));
            e(S(EffectDraft.EffectDraftForPort(combined, 2), "id") == "gradient" && EffectDraft.EffectDrafts(new JsonObject()).Count == 0, "later entries win");
        });
        T("effect draft: one snapshot includes both staged effects and clears only the selected applied overrides", e =>
        {
            var scene = DraftScene();
            var state = DraftState(scene);
            var before = new JsonArray(scene.DeepClone(), state.DeepClone());
            var draft = EffectDraft.StageEffectDraft(O("{colors:{8:{color:'#112233',brightness:25}}}"), Cfg("breathing", "[1,2]", "{color:'#0066FF'}", "{clearOverrides:true}"), "blue");
            draft = EffectDraft.StageEffectDraft(draft, Cfg("comet", "[3,4]", "{color:'#FF00AA'}", "{clearOverrides:true}"), "pink");
            var operations = LightingState.LightingOperations(state, draft);
            var snapshot = LightingState.LightingSnapshot(scene, state, draft);
            var effects = Js.Objects(operations).Where(operation => S(operation, "key") == "effect").ToList();
            e(effects.Count == 2 && effects.All(operation => Js.IsTrue(operation.At("body.clear_overrides"))), "operations");
            e(S(ZoneWith(snapshot, 1), "effect.id") == "breathing" && S(ZoneWith(snapshot, 2), "effect.id") == "breathing" && S(ZoneWith(snapshot, 3), "effect.id") == "comet"
              && S(ZoneWith(snapshot, 4), "effect.id") == "comet" && S(ZoneWith(snapshot, 5), "effect.id") == "static", "groups");
            e(!Js.Has(snapshot["overrides"], "2") && Eq(snapshot.At("overrides.6"), scene.At("overrides.6")) && Eq(snapshot.At("overrides.8"), "{color:'#112233',brightness:25}"), "overrides");
            e(Eq(new JsonArray(scene.DeepClone(), state.DeepClone()), before), "inputs unchanged");
        });
        T("effect draft: editing a subset of a replacement effect keeps clearing its prior fixed color", e =>
        {
            var scene = DraftScene();
            var state = DraftState(scene);
            var initial = EffectDraft.StageEffectDraft(new JsonObject(), Cfg("breathing", "[1,2,3]", "{color:'#0066FF'}", "{clearOverrides:true}"), "original");
            var edited = EffectDraft.StageEffectDraft(initial, Cfg("breathing", "[2]", "{color:'#AA00FF'}"), "subset");
            var snapshot = LightingState.LightingSnapshot(scene, state, edited);
            e(!Js.Has(snapshot["overrides"], "2") && Eq(snapshot.At("overrides.6"), scene.At("overrides.6")), "overrides");
            e(Eq(ZoneWith(snapshot, 2)?["effect"], "{id:'breathing',params:{color:'#AA00FF'}}") && Eq(EffectDraft.EffectDraftForPort(edited, 1)?["params"], "{color:'#0066FF'}"), "effects");
        });
        T("effect draft: parameter edits preserve a fixed color staged after choosing the effect", e =>
        {
            var scene = DraftScene();
            var state = DraftState(scene);
            var chosen = EffectDraft.StageEffectDraft(new JsonObject(), Cfg("breathing", "[1,2]", "{color:'#0066FF'}", "{clearOverrides:true}"), "pair");
            var manual = O("{color:'#55BB22',brightness:40}");
            var withColor = Js.Spread(chosen);
            withColor["colors"] = new JsonObject { ["1"] = manual.DeepClone() };
            var edited = EffectDraft.StageEffectDraft(withColor, Cfg("breathing", "[1,2]", "{color:'#AA00FF'}"), "ignored");
            var snapshot = LightingState.LightingSnapshot(scene, state, edited);
            e(Eq(snapshot.At("overrides.1"), manual) && !Js.Has(snapshot["overrides"], "2") && Eq(snapshot.At("overrides.6"), scene.At("overrides.6")), "kept");
            var replaced = EffectDraft.StageEffectDraft(edited, Cfg("comet", "[1,2]", "{}", "{clearOverrides:true}"), "replacement");
            e(!Js.Has(LightingState.LightingSnapshot(scene, state, replaced)["overrides"], "1"), "replaced");
        });
        T("effect draft: a partially overlapping edit preserves per-port override intent without splitting its effect geometry", e =>
        {
            var scene = DraftScene();
            var state = DraftState(scene);
            var draft = EffectDraft.StageEffectDraft(new JsonObject(), Cfg("gradient", "[1,2,3]", "{}", "{clearOverrides:true}"), "cleared");
            draft = EffectDraft.StageEffectDraft(draft, Cfg("gradient", "[5,6]", "{}", "{clearOverrides:false}"), "retained");
            draft = Js.Spread(draft, J("{colors:{3:{color:'#FFFFFF',brightness:30},8:{color:'#8899AA',brightness:75}}}"));
            draft = EffectDraft.StageEffectDraft(draft, Cfg("gradient", "[2,3,4,5,6]", "{color_a:'#FF00AA'}"), "mixed");
            var snapshot = LightingState.LightingSnapshot(scene, state, draft);
            e(Eq(ZoneWith(snapshot, 2)?["ports"], "[2,3,4,5,6]"), "geometry");
            e(!Js.Has(snapshot["overrides"], "2") && Eq(snapshot.At("overrides.3"), "{color:'#FFFFFF',brightness:30}") && Eq(snapshot.At("overrides.6"), scene.At("overrides.6"))
              && Eq(snapshot.At("overrides.8"), "{color:'#8899AA',brightness:75}"), "overrides");
            e(Js.IsTrue(EffectDraft.EffectDraftForPort(draft, 1)?["clearOverrides"]), "intent");
            var restored = (JsonObject)JsonNode.Parse(Js.Stringify(draft))!;
            var reedited = EffectDraft.StageEffectDraft(restored, Cfg("gradient", "[2,6]", "{color_a:'#0044FF'}"), "smaller");
            var next = LightingState.LightingSnapshot(scene, state, reedited);
            e(Eq(ZoneWith(next, 2)?["ports"], "[2,6]") && !Js.Has(next["overrides"], "2") && Eq(next.At("overrides.6"), scene.At("overrides.6"))
              && Eq(next.At("overrides.3"), snapshot.At("overrides.3")), "re-edited after a JSON round trip");
        });
    }

    // ----- preview/sfp-sync.mjs (tests/sfp-sync.test.mjs, tests/sfp-native-preview.test.mjs) -----

    static JsonObject SyncScene() => O("""
        {num_ports:8,mode:'running',brightness:80,rules:[],zones:[],overrides:{},sfp_sync:{mode:'independent',source_port:1},
         sfp:[{port:9,mode:'color',effect:'two_color',color:'#80C0FF',secondary_color:'#EF78C7',brightness:50,color_brightness:80,secondary_brightness:40,period:8,link:true,anomaly:false,facts:{link:true,anomaly:false},rule:null},
              {port:10,mode:'color',effect:'pulse',color:'#FFD090',secondary_color:'#336699',brightness:75,color_brightness:60,secondary_brightness:20,period:12,link:false,anomaly:true,facts:{link:false,anomaly:true},rule:null}]}
        """);

    static JsonObject AlertRule() => O("{id:'sfp-alert',name:'SFP alert',enabled:true,ports:[10],match:'all',conditions:[{field:'anomaly',op:'is',value:true}],look:{style:'solid',color:'#FF0000',brightness:50}}");

    static JsonObject Light(string changes = "{}") =>
        With(O("{mode:'color',effect:'two_color',color:'#80C0FF',secondary_color:'#EF78C7',color_brightness:80,secondary_brightness:40,brightness:50,period:6}"), changes);

    static JsonArray Sfp(JsonNode state) => state["sfp"]!.AsArray();

    static void SfpSyncChecks()
    {
        T("sfp sync: older scenes default to independent lights and drafts merge only their chosen sync fields", e =>
        {
            e(Eq(SfpSyncModel.SfpSync(), "{mode:'independent',source_port:1}"), "default");
            var state = SyncScene();
            state["sfp_sync"] = O("{mode:'ethernet',source_port:5}");
            var draft = O("{sfp_sync:{source_port:3}}");
            var before = new JsonArray(state.DeepClone(), draft.DeepClone());
            e(Eq(SfpSyncModel.SfpSync(state, draft), "{mode:'ethernet',source_port:3}") && Eq(new JsonArray(state.DeepClone(), draft.DeepClone()), before), "merged");
        });
        T("sfp sync: Together uses the first port's lighting settings while retaining each port's own telemetry and rule", e =>
        {
            var state = SyncScene();
            state["sfp_sync"]!["mode"] = "together";
            Sfp(state)[1]!["rule"] = O("{id:'sfp-alert',name:'SFP alert'}");
            var before = Js.Clone(state);
            var light = SfpSyncModel.EffectiveSfpLight(state, 10);
            e(Eq(LightingState.SfpSettings(light), LightingState.SfpSettings(Sfp(state)[0])) && N(light, "port") == 10, "shared settings");
            e(Eq(light["rule"], Sfp(state)[1]!["rule"]) && Eq(light["facts"], Sfp(state)[1]!["facts"]) && Eq(light["link"], Sfp(state)[1]!["link"]), "own telemetry");
            e(Eq(state, before), "unchanged");
        });
        T("sfp sync: switching draft sync modes restores saved independent settings without mutating the scene", e =>
        {
            var state = SyncScene();
            var before = Js.Clone(state);
            var draft = O("{sfp_sync:{mode:'together'},sfp:{9:{color:'#123456',period:4}}}");
            e(S(SfpSyncModel.EffectiveSfpLight(state, 10, draft), "color") == "#123456", "together");
            draft["sfp_sync"]!["mode"] = "independent";
            e(Eq(LightingState.SfpSettings(SfpSyncModel.EffectiveSfpLight(state, 10, draft)), LightingState.SfpSettings(Sfp(state)[1])), "independent");
            e(S(SfpSyncModel.EffectiveSfpLight(state, 9, draft), "color") == "#123456" && Eq(state, before), "edit kept");
        });
        T("sfp sync: saved-profile object maps and live arrays resolve the same SFP settings", e =>
        {
            var live = SyncScene();
            var saved = Js.Clone(live);
            var map = new JsonObject();
            foreach (var light in Js.Objects(live["sfp"])) map[Js.ToStr(light["port"])] = LightingState.SfpSettings(light);
            saved["sfp"] = map;
            saved["sfp_sync"]!["mode"] = "together";
            live["sfp_sync"]!["mode"] = "together";
            foreach (var port in new[] { 9, 10 })
                e(Eq(LightingState.SfpSettings(SfpSyncModel.EffectiveSfpLight(saved, port)), LightingState.SfpSettings(SfpSyncModel.EffectiveSfpLight(live, port))), $"port {port}");
            var expected = LightingState.SfpSettings(Sfp(live)[1]);
            expected["port"] = 10;
            e(Eq(SfpSyncModel.SfpLight(saved, 10), expected), "saved light");
        });
        T("sfp sync: Together edits target only the shared light and preserve the independent companion configuration", e =>
        {
            var state = SyncScene();
            var draft = O("{sfp_sync:{mode:'together'}}");
            e(SfpSyncModel.SfpEditTargets(state, new[] { 10 }, draft).SequenceEqual([9]) && SfpSyncModel.SfpEditTargets(state, new[] { 9, 10 }, draft).SequenceEqual([9]), "together");
            e(SfpSyncModel.SfpEditTargets(state, new[] { 10 }, O("{sfp_sync:{mode:'independent'}}")).SequenceEqual([10]), "independent");
            var copied = Js.Clone(Sfp(state)[1]);
            var edits = new JsonObject();
            draft["sfp"] = edits;
            foreach (var port in SfpSyncModel.SfpEditTargets(state, new[] { 9, 10 }, draft))
                edits[port.ToString(CultureInfo.InvariantCulture)] = With(LightingState.SfpSettings(SfpSyncModel.EditableSfpLight(state, port, draft)), "{color:'#123456'}");
            e(!edits.ContainsKey("10") && Eq(Sfp(state)[1], copied), "companion kept");
        });
        T("sfp sync: empty or Ethernet-only selection never stages SFP output in any sync mode", e =>
        {
            foreach (var mode in new[] { "independent", "together", "ethernet" })
            {
                var state = SyncScene();
                state["sfp_sync"]!["mode"] = mode;
                foreach (var selection in new[] { "[]", "[1,2,3,4,5,6,7,8]", "[11]", "['9']" })
                    e(SfpSyncModel.SfpEditTargets(state, A(selection)).Count == 0, $"{mode}: {selection}");
                e(SfpSyncModel.SfpEditTargets(state, new[] { 1, 10, 10 }).SequenceEqual(mode == "together" ? [9] : [10]), $"{mode}: mixed");
                var draft = O("{colors:{1:{color:'#1234ff',brightness:100}},sfp:{}}");
                e(Js.Objects(LightingState.LightingOperations(state, draft)).Select(operation => S(operation, "path")).SequenceEqual(["/api/ports"]), $"{mode}: operations");
            }
        });
        T("sfp sync: shared SFP targets and preview settings use reported IDs on larger port maps", e =>
        {
            foreach (var (count, ids) in new[] { (16, new[] { 17, 18 }), (24, new[] { 25, 26, 27, 28 }) })
            {
                var state = SyncScene();
                state["num_ports"] = count;
                state["sfp_sync"]!["mode"] = "together";
                var lights = ids.Select((port, index) =>
                {
                    var light = (JsonObject)Sfp(state)[index % 2]!.DeepClone();
                    light["port"] = port;
                    return (JsonNode?)light;
                }).Reverse().ToList();
                state["sfp"] = Js.ArrayOf(lights);
                var original = Js.Clone(state);
                var first = Js.Objects(state["sfp"]).First(light => N(light, "port") == ids[0]);
                foreach (var id in ids)
                {
                    var light = SfpSyncModel.EffectiveSfpLight(state, id);
                    e(Eq(LightingState.SfpSettings(light), LightingState.SfpSettings(first)) && N(light, "port") == id
                      && Eq(light["facts"], Js.Objects(state["sfp"]).First(item => N(item, "port") == id)["facts"]), $"{count}: port {id}");
                }
                e(SfpSyncModel.SfpEditTargets(state, new[] { ids[^1] }).SequenceEqual([ids[0]]), $"{count}: target");
                var draft = new JsonObject { ["sfp"] = new JsonObject { [ids[0].ToString(CultureInfo.InvariantCulture)] = O("{color:'#123456'}") } };
                e(SfpSyncModel.SfpDraftPorts(state, draft).SequenceEqual(ids) && S(SfpSyncModel.EffectiveSfpLight(state, ids[^1], draft), "color") == "#123456", $"{count}: draft");
                e(SfpSyncModel.SfpDraftPorts(state, O("{sfp_sync:{mode:'together'}}")).SequenceEqual(ids) && SfpSyncModel.SfpDraftPorts(state, O("{sfp:{9:{color:'#abcdef'}}}")).Count == 0, $"{count}: draft scope");
                e(Eq(state, original), $"{count}: unchanged");
                var saved = Js.Spread(state);
                var map = new JsonObject();
                foreach (var light in Js.Objects(state["sfp"])) map[Js.ToStr(light["port"])] = LightingState.SfpSettings(light);
                saved["sfp"] = map;
                e(SfpSyncModel.SfpEditTargets(saved, new[] { ids[^1] }).SequenceEqual([ids[0]]) && S(SfpSyncModel.EffectiveSfpLight(saved, ids[^1], draft), "color") == "#123456"
                  && SfpSyncModel.SfpDraftPorts(saved, draft).SequenceEqual(ids), $"{count}: saved map");
            }
        });
        T("sfp sync: SFP preview scope follows actual synchronized or independently edited indicators", e =>
        {
            var state = SyncScene();
            state["sfp"] = Arr(With(Sfp(state)[0], "{port:25}"), With(Sfp(state)[1], "{port:26}"));
            e(SfpSyncModel.SfpDraftPorts(state, O("{sfp:{9:{color:'#ffffff'},26:{color:'#abcdef'}}}")).SequenceEqual([26]), "independent");
            state["sfp_sync"] = O("{mode:'ethernet',source_port:3}");
            foreach (var draft in new[] { "{colors:{3:{color:'#123456'}}}", "{effect:{ports:[2,3]}}", "{returnToEffect:[3]}" })
                e(SfpSyncModel.SfpDraftPorts(state, O(draft)).SequenceEqual([25, 26]), draft);
            e(SfpSyncModel.SfpDraftPorts(state, O("{colors:{2:{color:'#123456'}}}")).Count == 0, "other port");
        });
        T("sfp sync: missing SFP indicators never synthesize ports from sync settings or drafts", e =>
        {
            foreach (var sfp in new[] { "[]", "{}", "undefined" })
            {
                var state = O($"{{sfp:{sfp},sfp_sync:{{mode:'together',source_port:1}}}}");
                e(SfpSyncModel.SfpEditTargets(state, new[] { 9, 10 }).Count == 0 && SfpSyncModel.SfpDraftPorts(state, O("{sfp_sync:{mode:'together'},sfp:{9:{color:'#ffffff'}}}")).Count == 0, sfp);
            }
        });
        T("sfp sync: editing flattens the legacy SFP brightness into both colors once", e =>
        {
            var state = SyncScene();
            var before = Js.Clone(state);
            var editable = SfpSyncModel.EditableSfpLight(state, 9);
            e(N(editable, "brightness") == 100 && N(editable, "color_brightness") == 40 && N(editable, "secondary_brightness") == 20, "flattened");
            foreach (var time in new[] { 0, 1, 2, 4, 6 })
                e(Near(EffectGallery.SfpPreviewColor(editable, "two_color", time, 80), EffectGallery.SfpPreviewColor(Sfp(state)[0], "two_color", time, 80)), $"same preview at {time}");
            var draft = new JsonObject { ["sfp"] = new JsonObject { ["9"] = With(LightingState.SfpSettings(editable), "{color:'#234567'}") } };
            e(Eq(LightingState.SfpSettings(SfpSyncModel.EditableSfpLight(state, 9, draft)), draft.At("sfp.9")) && Eq(state, before), "draft round trip");
        });
        T("sfp sync: legacy brightness conversion is bounded by one half percentage point and handles zero", e =>
        {
            var state = SyncScene();
            Assign((JsonObject)Sfp(state)[0]!, "{brightness:33,color_brightness:37,secondary_brightness:89}");
            var editable = SfpSyncModel.EditableSfpLight(state, 9);
            e(N(editable, "color_brightness") == 12 && N(editable, "secondary_brightness") == 29, "rounded");
            e(Math.Abs(N(editable, "color_brightness")!.Value - 37 * .33) <= .5 && Math.Abs(N(editable, "secondary_brightness")!.Value - 89 * .33) <= .5, "bounded");
            Sfp(state)[0]!["brightness"] = 0;
            e(N(SfpSyncModel.EditableSfpLight(state, 9), "color_brightness") == 0 && N(SfpSyncModel.EditableSfpLight(state, 9), "secondary_brightness") == 0, "zero");
        });
        T("sfp sync: Apply prepares shared settings before enabling Together", e =>
        {
            var state = SyncScene();
            var draft = O("{sfp:{9:{color:'#123456',brightness:100,color_brightness:40}},sfp_sync:{mode:'together'}}");
            var before = new JsonArray(state.DeepClone(), draft.DeepClone());
            var plan = LightingState.LightingOperations(state, draft);
            e(Js.Objects(plan).Select(item => S(item, "path")).SequenceEqual(["/api/sfp/9", "/api/sfp/sync"]) && Eq(plan.At("1.body"), "{mode:'together',source_port:1}"), "plan");
            e(Eq(new JsonArray(state.DeepClone(), draft.DeepClone()), before), "inputs unchanged");
        });
        T("sfp sync: invalid sync mode, source type, range and unknown fields are rejected before any request", e =>
        {
            var state = SyncScene();
            foreach (var sync in new[] { "{mode:'invalid'}", "{mode:null}", "{source_port:0}", "{source_port:9}", "{source_port:'1'}", "{source_port:true}", "{source_port:1.5}", "{mode:'together',extra:true}", "[]", "'together'" })
                e(Throws(() => LightingState.LightingOperations(state, O($"{{sfp_sync:{sync}}}"))), sync);
        });
        T("sfp sync: saved profiles distinguish Follow Ethernet source changes and retain sync after JSON round-trip", e =>
        {
            var state = SyncScene();
            state["sfp_sync"] = O("{mode:'ethernet',source_port:3}");
            var profile = (JsonObject)JsonNode.Parse(Js.Stringify(state))!;
            e(SecondaryModel.MatchingProfile(new JsonObject { ["Evening"] = profile.DeepClone() }, state) == "Evening", "match");
            state["sfp_sync"]!["source_port"] = 5;
            e(SecondaryModel.SceneFingerprint(profile) != SecondaryModel.SceneFingerprint(state) && SecondaryModel.MatchingProfile(new JsonObject { ["Evening"] = profile.DeepClone() }, state) is null, "source change");
            profile.Remove("sfp_sync");
            e(SecondaryModel.SceneFingerprint(profile) == SecondaryModel.SceneFingerprint(With(profile, "{sfp_sync:{mode:'independent',source_port:1}}")), "default sync");
        });
        T("sfp sync: gallery statuses describe shared, staged and followed output without claiming the inactive effect", e =>
        {
            var state = SyncScene();
            state["sfp_sync"]!["mode"] = "together";
            e(Eq(EffectGallery.SfpGalleryStatus(O("{id:'two_color'}"), state, [9, 10]), "{label:'Applied',selected:true}"), "applied");
            e(Eq(EffectGallery.SfpGalleryStatus(O("{id:'pulse'}"), state, [9, 10]), "{label:'',selected:false}"), "inactive");
            Sfp(state)[1]!["rule"] = O("{id:'sfp-alert',name:'SFP alert'}");
            e(Eq(EffectGallery.SfpGalleryStatus(O("{id:'two_color'}"), state, [9, 10]), "{label:'Applied to 1 of 2',selected:false}"), "rule");
            e(Eq(EffectGallery.SfpGalleryStatus(O("{id:'pulse'}"), state, [9, 10], O("{sfp:{9:{effect:'pulse'}}}")), "{label:'Previewing',selected:true}"), "draft");
            state["sfp_sync"] = O("{mode:'ethernet',source_port:3}");
            e(Eq(EffectGallery.SfpGalleryStatus(O("{id:'two_color'}"), state, [9, 10]), "{label:'',selected:false}"), "following");
        });
        T("sfp sync: shared Off suppresses fallback rules just as the backend does, while Follow Ethernet permits them", e =>
        {
            var state = SyncScene();
            state["sfp_sync"]!["mode"] = "together";
            Sfp(state)[0]!["mode"] = "off";
            ((JsonObject)Sfp(state)[1]!).Remove("rule");
            state["rules"] = Arr(AlertRule());
            e(S(LightingState.ControlSource(state, 10), "kind") == "off", "shared off");
            state["sfp_sync"] = O("{mode:'ethernet',source_port:3}");
            e(S(LightingState.ControlSource(state, 10), "kind") == "rule", "following");
            Sfp(state)[1]!["mode"] = "off";
            e(S(LightingState.ControlSource(state, 10), "kind") == "rule", "own off");
            Sfp(state)[1]!["rule"] = null;
            e(Eq(LightingState.ControlSource(state, 10), "{kind:'sync',label:'Following port 3'}"), "reported none");
        });
        T("sfp sync: Follow Ethernet preview includes its source in any retained effect draft", e =>
        {
            foreach (var ids in new[] { new[] { 9, 10 }, new[] { 25, 26, 27, 28 } })
            {
                var state = new JsonObject { ["sfp"] = Js.ArrayOf(ids.Select(port => (JsonNode?)new JsonObject { ["port"] = port })), ["sfp_sync"] = O("{mode:'ethernet',source_port:3}") };
                var cases = new[]
                {
                    ("{effects:[{id:'marquee',ports:[1,2]},{id:'breathing',ports:[3]},{id:'static',ports:[4]}]}", ids),
                    ("{effect:{id:'breathing',ports:[3]},effects:[{id:'marquee',ports:[1,2]}]}", ids),
                    ("{effect:{id:'marquee',ports:[1,2]},effects:[{id:'breathing',ports:[3]}]}", ids),
                    ("{effect:{id:'breathing',ports:[3]}}", ids),
                    ("{effects:[{id:'marquee',ports:[1,2]}]}", Array.Empty<int>()),
                    ("{effects:[]}", Array.Empty<int>()),
                };
                foreach (var (draftText, expected) in cases)
                {
                    var draft = O(draftText);
                    var before = new JsonArray(state.DeepClone(), draft.DeepClone());
                    e(SfpSyncModel.SfpDraftPorts(state, draft).SequenceEqual(expected) && Eq(new JsonArray(state.DeepClone(), draft.DeepClone()), before), $"{ids.Length} ports: {draftText}");
                }
            }
        });

        // tests/sfp-native-preview.test.mjs
        T("sfp native: native preview requires both switch capability and the new backend playback state", e =>
        {
            foreach (var state in new[] { "{}", "{switch:{sfp_native_gradient:true}}", "{switch:{sfp_native_gradient:true},sfp:[{port:9},{port:10}]}",
                         "{sfp:[{port:9,playback:{kind:'native'}}]}", "{switch:{sfp_native_gradient:false},sfp:[{port:9,playback:{kind:'native'}}]}",
                         "{switch:{sfp_native_gradient:'true'},sfp:[{port:9,playback:{kind:'native'}}]}" })
                e(!SfpSyncModel.SfpNativeAvailable(O(state)), state);
            e(SfpSyncModel.SfpNativeAvailable(O("{switch:{sfp_native_gradient:true},sfp:[{port:9},{port:10,playback:{kind:'native',periods:[2,4,6],segment_seconds:3}}]}")), "available");
        });
        T("sfp native: an older backend keeps its saved duration and cosine preview even at native-compatible periods", e =>
        {
            var settings = Light("{color:'#FF0000',secondary_color:'#0000FF',brightness:100,color_brightness:100,secondary_brightness:100}");
            var legacy = new JsonObject { ["switch"] = O("{sfp_native_gradient:true}"), ["sfp"] = Arr(With(settings, "{port:9}")) };
            var native = SfpSyncModel.SfpNativeAvailable(legacy);
            e(Near(EffectGallery.SfpPreviewColor(settings, "pulse", .75, 100, 2.2, native), [217.6561146012848, 0, 0]), "pulse");
            e(Near(EffectGallery.SfpPreviewColor(settings, "two_color", .75, 100, 2.2, native), [217.6561146012848, 0, 37.34388539871519]), "two color");
            e(Eq(SfpSyncModel.SfpEffectChange(With(settings, "{period:10.5}"), "pulse", native), "{effect:'pulse',period:10.5}"), "custom period kept");
            e(Eq(SfpSyncModel.SfpEffectChange(With(settings, "{period:4}"), "color_cycle", native), "{effect:'color_cycle',period:4}") && N(settings, "period") == 6, "period kept");
        });
        T("sfp native: gallery candidates replacing Follow Ethernet use the same shared color and duration as Apply", e =>
        {
            var state = new JsonObject
            {
                ["switch"] = O("{sfp_native_gradient:true}"),
                ["sfp_sync"] = O("{mode:'ethernet',source_port:3}"),
                ["sfp"] = Arr(With(Light("{effect:'pulse',period:4}"), "{port:9,playback:{kind:'follow'}}"), With(Light("{effect:'color_cycle',color:'#FF0000',period:12}"), "{port:10,playback:{kind:'follow'}}")),
            };
            var draft = O("{sfp:{9:{color:'#13579B'}}}");
            var before = new JsonArray(state.DeepClone(), draft.DeepClone());
            var first = SfpSyncModel.SfpCandidateSettings(state, 9, "color_cycle", draft);
            var second = SfpSyncModel.SfpCandidateSettings(state, 10, "color_cycle", draft);
            e(S(first, "color") == "#13579B" && S(second, "color") == "#13579B" && N(first, "period") == 6 && N(second, "period") == 6 && N(second, "port") == 10, "settings");
            foreach (var time in new[] { 0, .5, 1, 3 })
                e(Near(EffectGallery.SfpPreviewColor(first, "color_cycle", time), EffectGallery.SfpPreviewColor(second, "color_cycle", time)), $"same at {time}");
            e(Eq(new JsonArray(state.DeepClone(), draft.DeepClone()), before), "unchanged");
        });
        T("sfp native: applied SFP output remains authoritative until a relevant light or sync draft exists", e =>
        {
            foreach (var mode in new[] { "independent", "together", "ethernet" })
            {
                var state = O($"{{sfp:[{{port:9}},{{port:10}}],sfp_sync:{{mode:'{mode}',source_port:3}}}}");
                e(SfpSyncModel.SfpDraftPorts(state).Count == 0 && SfpSyncModel.SfpDraftPorts(state, O("{brightness:30}")).Count == 0, mode);
            }
            var together = O("{sfp:[{port:9},{port:10}],sfp_sync:{mode:'together',source_port:3}}");
            e(SfpSyncModel.SfpDraftPorts(together, O("{sfp:{9:{color:'#123456'}}}")).SequenceEqual([9, 10]), "shared edit");
            e(SfpSyncModel.SfpDraftPorts(together, O("{sfp:{10:{color:'#123456'}}}")).Count == 0, "dormant edit");
            e(SfpSyncModel.SfpDraftPorts(With(together, "{sfp_sync:{mode:'independent'}}"), O("{sfp:{10:{color:'#123456'}}}")).SequenceEqual([10]), "independent edit");
            e(SfpSyncModel.SfpDraftPorts(together, O("{sfp_sync:{mode:'independent'}}")).SequenceEqual([9, 10]), "sync edit");
        });
        T("sfp native: Follow Ethernet previews propagate only edits to the selected source port", e =>
        {
            var state = O("{sfp:[{port:9},{port:10}],sfp_sync:{mode:'ethernet',source_port:3}}");
            foreach (var draft in new[] { "{colors:{3:{color:'#123456'}}}", "{effect:{id:'pulse',ports:[1,3]}}", "{returnToEffect:[3]}", "{sfp_sync:{source_port:4}}" })
                e(SfpSyncModel.SfpDraftPorts(state, O(draft)).SequenceEqual([9, 10]), draft);
            foreach (var draft in new[] { "{colors:{2:{color:'#123456'}}}", "{effect:{id:'pulse',ports:[1,2]}}", "{returnToEffect:[4]}", "{sfp:{9:{color:'#123456'}}}" })
                e(SfpSyncModel.SfpDraftPorts(state, O(draft)).Count == 0, draft);
        });
        T("sfp native: native duration choices reflect full effect cycles rather than individual fades", e =>
        {
            foreach (var effect in new[] { "pulse", "two_color" })
            {
                e(new[] { 2, 4, 6 }.All(period => SfpSyncModel.SfpNativePeriod(effect, period)) && !new[] { 3, 5, 8, 12 }.Any(period => SfpSyncModel.SfpNativePeriod(effect, period)), $"{effect} periods");
            }
            e(new[] { 6, 12, 18 }.All(period => SfpSyncModel.SfpNativePeriod("color_cycle", period)) && !new[] { 2, 4, 8, 30 }.Any(period => SfpSyncModel.SfpNativePeriod("color_cycle", period)), "cycle periods");
            e(!SfpSyncModel.SfpNativePeriod("steady", 6), "steady");
        });
        T("sfp native: native duration sliders reach only verified complete fade cycles", e =>
        {
            foreach (var effect in new[] { "pulse", "two_color", "color_cycle" })
            {
                var range = SfpSyncModel.SfpDurationRange(effect, 6, true);
                var ok = true;
                for (var value = N(range, "min")!.Value; value <= N(range, "max"); value += N(range, "step")!.Value) ok &= SfpSyncModel.SfpNativePeriod(effect, value);
                e(ok && Js.IsFalse(range["savedCustom"]), effect);
            }
            e(Eq(SfpSyncModel.SfpDurationRange("pulse", 4, true), "{min:2,max:6,step:2,value:4,savedCustom:false}"), "pulse");
            e(Eq(SfpSyncModel.SfpDurationRange("color_cycle", 12, true), "{min:6,max:18,step:6,value:12,savedCustom:false}"), "cycle");
        });
        T("sfp native: custom saved periods remain untouched until the duration slider is moved", e =>
        {
            var settings = Light("{period:10.5}");
            e(Eq(SfpSyncModel.SfpDurationRange(S(settings, "effect"), settings["period"], true), "{min:2,max:6,step:2,value:6,savedCustom:true}") && N(settings, "period") == 10.5, "native");
            e(Eq(SfpSyncModel.SfpDurationRange("pulse", 4.1, false), "{min:2,max:30,step:.1,value:4.1,savedCustom:false}"), "legacy");
        });
        T("sfp native: previewing a saved custom duration does not round or replace it", e =>
        {
            var settings = Light("{period:10.5}");
            EffectGallery.SfpPreviewColor(settings, S(settings, "effect"), 1.5, 80);
            e(N(settings, "period") == 10.5, "unchanged");
        });
        T("sfp native: only an explicit animated-effect change replaces an unsupported saved duration", e =>
        {
            var settings = Light("{period:10.5}");
            e(Eq(SfpSyncModel.SfpEffectChange(settings, "two_color"), "{effect:'two_color',period:10.5}"), "same effect");
            e(Eq(SfpSyncModel.SfpEffectChange(settings, "pulse"), "{effect:'pulse',period:6}") && Eq(SfpSyncModel.SfpEffectChange(settings, "color_cycle"), "{effect:'color_cycle',period:6}"), "replaced");
            e(Eq(SfpSyncModel.SfpEffectChange(settings, "steady"), "{effect:'steady',period:10.5}"), "steady");
            e(Eq(SfpSyncModel.SfpEffectChange(Light("{period:4}"), "pulse"), "{effect:'pulse',period:4}") && Eq(SfpSyncModel.SfpEffectChange(Light("{period:12}"), "color_cycle"), "{effect:'color_cycle',period:12}"), "native kept");
            e(N(settings, "period") == 10.5 && S(settings, "effect") == "two_color", "unchanged");
        });
        T("sfp native: effect changes retain each independent light's supported or custom duration", e =>
        {
            e(Eq(Arr(SfpSyncModel.SfpEffectChange(Light("{period:2}"), "pulse"), SfpSyncModel.SfpEffectChange(Light("{period:4}"), "pulse")), "[{effect:'pulse',period:2},{effect:'pulse',period:4}]"), "pulse");
            var custom = new[] { Light("{period:10.5}"), Light("{period:12}") };
            e(Eq(Arr(custom.Select(item => (JsonNode?)SfpSyncModel.SfpEffectChange(item, "color_cycle")).ToArray()), "[{effect:'color_cycle',period:6},{effect:'color_cycle',period:12}]"), "cycle");
            e(Eq(Arr(custom.Select(item => (JsonNode?)SfpSyncModel.SfpEffectChange(item, "steady")).ToArray()), "[{effect:'steady',period:10.5},{effect:'steady',period:12}]"), "steady");
        });
        T("sfp native: native pulse reaches dark halfway through each selected period and loops to the primary color", e =>
        {
            var settings = Light("{color:'#FF0000',color_brightness:100,brightness:100}");
            foreach (var period in new[] { 2, 4, 6 })
            {
                settings["period"] = period;
                e(Near(EffectGallery.SfpPreviewColor(settings, "pulse", 0, 100, 1), [255, 0, 0]) && Near(EffectGallery.SfpPreviewColor(settings, "pulse", period / 4.0, 100, 1), [127.5, 0, 0])
                  && Near(EffectGallery.SfpPreviewColor(settings, "pulse", period / 2.0, 100, 1), [0, 0, 0]) && Near(EffectGallery.SfpPreviewColor(settings, "pulse", period * .75, 100, 1), [127.5, 0, 0])
                  && Near(EffectGallery.SfpPreviewColor(settings, "pulse", period, 100, 1), [255, 0, 0]), $"period {period}");
            }
        });
        T("sfp native: native two-color preview composes both color levels, saved SFP level and master once", e =>
        {
            var settings = Light();
            e(Near(EffectGallery.SfpPreviewColor(settings, "two_color", 0, 80, 1), [40.96, 61.44, 81.6], .002) && Near(EffectGallery.SfpPreviewColor(settings, "two_color", 3, 80, 1), [38.24, 19.2, 31.84], .002)
              && Near(EffectGallery.SfpPreviewColor(settings, "two_color", 1.5, 80, 1), [39.6, 40.32, 56.72], .002), "levels");
            foreach (var effect in new[] { "steady", "pulse", "two_color", "color_cycle" })
                e(Near(EffectGallery.SfpPreviewColor(settings, effect, 1, 0), [0, 0, 0]) && Near(EffectGallery.SfpPreviewColor(With(settings, "{brightness:0}"), effect, 1, 100), [0, 0, 0]), $"{effect} off");
        });
        T("sfp native: native previews match backend RGB16 anchor interpolation at the configured gamma", e =>
        {
            var vectors = A("""
                [['pulse',6,[[0,[40.95396471852974,61.440823948781336,81.59699142318527]],[.75,[35.93400442844279,53.90967284942135,71.59518428313993]],[1.5,[29.885748377737002,44.83583011496126,59.54459283279925]],[3,[0,0,0]],[5.25,[35.93400442844279,53.90967284942135,71.59518428313993]]]],
                 ['two_color',6,[[0,[40.95396471852974,61.440823948781336,81.59699142318527]],[.75,[40.29414029413783,54.53843571603115,72.94752814170903]],[1.5,[39.621088113580626,46.38348692461317,62.84677109360379]],[3,[38.232101056960325,19.21483246681641,31.833003965396433]],[5.25,[40.29414029413783,54.53843571603115,72.94752814170903]]]],
                 ['color_cycle',12,[[0,[20.39620887648304,51.23764426858132,81.59699142318527]],[1.5,[45.435055590391805,31.74660668190112,81.59699142318527]],[3,[68.2916729371186,20.39620887648304,68.45968411502898]],[6,[81.59699142318527,50.75991919690523,20.39620887648304]],[10.5,[20.39620887648304,75.37240279815471,60.29414295775652]]]]]
                """);
            foreach (var vector in vectors)
            {
                var effect = S(vector, "0")!;
                var settings = Light($"{{effect:'{effect}',period:{Js.NumberToString(N(vector, "1")!.Value)}}}");
                foreach (var sample in Js.Items(Get(vector, "2")))
                {
                    var time = N(sample, "0")!.Value;
                    var expected = Js.Items(Get(sample, "1")).Select(Js.ToNumber).ToArray();
                    e(Near(EffectGallery.SfpPreviewColor(settings, effect, time, 80, 2.2), expected) && Near(EffectGallery.SfpPreviewColor(settings, effect, time, 80), expected), $"{effect} at {time}");
                }
            }
            e(EffectGallery.SfpPreviewColor(Light(), "pulse", 1.5, 80, 2.2)[2] > EffectGallery.SfpPreviewColor(Light(), "pulse", 1.5, 80, 1)[2] + 10, "gamma");
        });
        T("sfp native: color-specific brightness remains independent when one side of a native fade is off", e =>
        {
            var settings = Light("{color_brightness:0}");
            e(Near(EffectGallery.SfpPreviewColor(settings, "two_color", 0, 80, 1), [0, 0, 0]) && Near(EffectGallery.SfpPreviewColor(settings, "two_color", 3, 80, 1), [38.24, 19.2, 31.84], .002), "one side off");
            e(Near(EffectGallery.SfpPreviewColor(With(settings, "{secondary_brightness:0}"), "two_color", 1.5, 80), [0, 0, 0]), "both off");
        });
        T("sfp native: native cycle visits six color anchors in order and returns to the selected hue", e =>
        {
            var settings = Light("{color:'#FF0000',color_brightness:100,brightness:100,period:12}");
            var anchors = new[] { new double[] { 255, 0, 0 }, [255, 255, 0], [0, 255, 0], [0, 255, 255], [0, 0, 255], [255, 0, 255], [255, 0, 0] };
            for (var index = 0; index < anchors.Length; index++) e(Near(EffectGallery.SfpPreviewColor(settings, "color_cycle", index * 2, 100, 1), anchors[index]), $"anchor {index}");
            e(Near(EffectGallery.SfpPreviewColor(settings, "color_cycle", 1, 100, 1), [255, 127.5, 0]) && Near(EffectGallery.SfpPreviewColor(settings, "color_cycle", 11, 100, 1), [255, 0, 127.5]), "midpoints");
        });
        T("sfp native: native cycle is continuous at every segment boundary and full-cycle wrap", e =>
        {
            var settings = Light("{period:12}");
            const double epsilon = 1e-7;
            for (var boundary = 0; boundary <= 12; boundary += 2)
            {
                var center = EffectGallery.SfpPreviewColor(settings, "color_cycle", boundary, 80, 2.2);
                var before = EffectGallery.SfpPreviewColor(settings, "color_cycle", boundary == 0 ? 12 - epsilon : boundary - epsilon, 80, 2.2);
                var after = EffectGallery.SfpPreviewColor(settings, "color_cycle", boundary + epsilon, 80, 2.2);
                e(Near(before, center, .001) && Near(after, center, .001), $"boundary {boundary}");
            }
        });
        T("sfp native: custom durations preserve the existing cosine fade without native quantization", e =>
        {
            var settings = Light("{color:'#FF0000',secondary_color:'#0000FF',color_brightness:100,secondary_brightness:100,brightness:100,period:10.5}");
            var time = 10.5 / 8;
            e(Near(EffectGallery.SfpPreviewColor(settings, "pulse", time, 100, 2.2), [217.6561146012848, 0, 0]), "pulse");
            e(Near(EffectGallery.SfpPreviewColor(settings, "two_color", time, 100, 2.2), [217.6561146012848, 0, 37.34388539871519]), "two color");
            e(Near(EffectGallery.SfpPreviewColor(settings, "two_color", time, 100, 1), EffectGallery.SfpPreviewColor(settings, "two_color", time, 100, 2.2)), "no gamma");
        });
        T("sfp native: steady output preserves full-precision brightness and does not animate", e =>
        {
            var settings = Light("{period:10.5}");
            foreach (var time in new[] { 0, .1, 3, 10.5, 300 }) e(Near(EffectGallery.SfpPreviewColor(settings, "steady", time, 80, 2.2), [40.96, 61.44, 81.6]), time.ToString(CultureInfo.InvariantCulture));
        });
    }

    // ----- preview/lighting-state.mjs (tests/lighting-state.test.mjs, tests/preview-lighting-state.test.mjs) -----

    static JsonObject LightingBase()
    {
        var state = O("""
            {num_ports:8,brightness:100,mode:'running',zones:[
              {id:'left',name:'Left',ports:[1,2,3,4],effect:{id:'marquee',params:{color:'#123456'}}},
              {id:'right',name:'Right',ports:[5,6,7,8],effect:{id:'breathing',params:{color:'#ABCDEF'}}}],
             sfp:[{port:9,mode:'color',effect:'steady',color:'#FF0000',brightness:30,secondary_color:'#112233',period:6},
                  {port:10,mode:'color',effect:'pulse',color:'#0000FF',brightness:70,secondary_color:'#334455',period:12}],rules:[]}
            """);
        state["ports"] = Js.ArrayOf(Enumerable.Range(1, 8).Select(port => (JsonNode?)new JsonObject { ["port"] = port, ["facts"] = O("{link:true}") }));
        return state;
    }

    static JsonObject LightingRule(string changes = "{}") =>
        With(O("{id:'alarm',name:'Alarm',enabled:true,ports:[1],match:'all',conditions:[],look:{style:'solid',color:'#FF0000',brightness:50}}"), changes);

    static JsonObject LightingSnapshotOf(JsonObject state)
    {
        var sfp = new JsonObject();
        foreach (var light in Js.Objects(state["sfp"])) sfp[Js.ToStr(light["port"])] = LightingState.SfpSettings(light);
        return new JsonObject
        {
            ["version"] = 2,
            ["brightness"] = Js.Clone(state["brightness"]),
            ["zones"] = Js.Clone(state["zones"]),
            ["rules"] = Js.Clone(state["rules"]),
            ["overrides"] = O("{1:{color:'#123456',brightness:20},2:{color:'#654321',brightness:null},8:{color:'#FEDCBA',brightness:80}}"),
            ["sfp"] = sfp,
            ["sfp_sync"] = O("{mode:'independent',source_port:1}"),
        };
    }

    static IEnumerable<string?> Keys(JsonNode? plan) => Js.Objects(plan).Select(item => S(item, "key"));

    static JsonArray Ops(JsonNode? state, string draft) => LightingState.LightingOperations(state, J(draft));

    static void LightingStateChecks()
    {
        T("lighting state: optical-only drafts allow shared SFP settings and reject imaginary Ethernet output", e =>
        {
            var state = With(LightingBase(), "{num_ports:0,ports:[],zones:[],sfp:[{port:1,mode:'color'},{port:2,mode:'color'}],sfp_sync:{mode:'independent',source_port:1}}");
            foreach (var mode in new[] { "independent", "together" }) e(S(Ops(state, $"{{sfp_sync:{{mode:'{mode}'}}}}"), "0.body.mode") == mode, mode);
            e(Throws(() => Ops(state, "{sfp_sync:{mode:'ethernet'}}"), "Ethernet port"), "no follow source");
            e(Throws(() => Ops(state, "{colors:{1:{color:'#FFFFFF'}}}"), "Ethernet ports"), "no fixed copper color");
            e(Eq(Ops(state, "{effect:{id:'static',ports:[1],params:{}}}").At("0.body.zones.0.ports"), "[1]"), "optical group");
            e(S(Ops(state, "{sfp:{2:{color:'#FFFFFF'}}}"), "0.path") == "/api/sfp/2", "SFP write");
        });
        T("lighting state: draft validation follows reported copper membership rather than a numeric range", e =>
        {
            var state = With(LightingBase(), "{num_ports:48,ports:[{port:3},{port:24}]}");
            e(Throws(() => Ops(state, "{colors:{2:{color:'#FFFFFF'}}}"), "Ethernet ports"), "unreported port");
            e(Throws(() => Ops(state, "{sfp_sync:{mode:'ethernet',source_port:2}}"), "Ethernet port"), "unreported source");
            e(S(Ops(state, "{colors:{24:{color:'#FFFFFF'}}}"), "0.key") == "colors", "reported port");
        });
        T("lighting state: a combined draft plans brightness, port colors and SFP writes in that order", e =>
        {
            var plan = Ops(LightingBase(), "{brightness:50,colors:{1:{color:'#123456',brightness:20}},sfp:{9:{effect:'pulse'}}}");
            e(Keys(plan).SequenceEqual(["brightness", "colors", "sfp:9"]) && Js.Objects(plan).Select(item => S(item, "path")).SequenceEqual(["/api/brightness", "/api/ports", "/api/sfp/9"]), "plan order");
        });
        T("lighting state: new effect group preserves unrelated ports and regroups before setting the effect", e =>
        {
            var state = LightingBase();
            var before = Js.Clone(state);
            var draft = O("{effect:{id:'comet',ports:[5,2],params:{color:'#3366FF'},clearOverrides:true}}");
            var plan = LightingState.LightingOperations(state, draft);
            var zones = plan.At("0.body.zones");
            e(Keys(plan).SequenceEqual(["group", "effect"]), "operations");
            e(Eq(Get(zones, "0"), With(Get(state, "zones.0"), "{ports:[1,3,4]}")) && Eq(Get(zones, "1"), With(Get(state, "zones.1"), "{ports:[6,7,8]}")), "unrelated ports");
            e(Eq(Get(zones, "2.ports"), "[2,5]") && Js.IsTrue(plan.At("1.body.clear_overrides")) && !Js.Has(plan.At("0.body"), "clear_overrides"), "new group");
            e(Eq(state, before) && Eq(draft.At("effect.ports"), "[5,2]"), "inputs unchanged");
            e(Js.Objects(plan).Select(item => S(item, "path")).SequenceEqual(["/api/zones", $"/api/zones/{S(zones, "2.id")}/effect"]), "paths");
        });
        T("lighting state: exact groups use one effect update and generated names cannot collide with surviving groups", e =>
        {
            var state = LightingBase();
            var plan = Ops(state, "{effect:{id:'static',ports:[4,3,2,1,1],params:{color:'#FFFFFF'}}}");
            e(plan.Count == 1 && S(plan, "0.path") == "/api/zones/left/effect", "exact group");
            state["zones"]![1]!["id"] = "selection-1-2";
            plan = Ops(state, "{effect:{id:'static',ports:[1,2],params:{}}}");
            var groups = Js.Objects(plan.At("0.body.zones")).ToList();
            e(groups.Select(group => S(group, "id")).Distinct().Count() == groups.Count && S(groups[^1], "id") == "selection-1-2-2", "unique id");
            e(S(plan, "1.path") == "/api/zones/selection-1-2-2/effect", "effect path");
        });
        T("lighting state: mixed Ethernet/SFP draft coalesces partial SFP changes without copying one port over another", e =>
        {
            var state = LightingBase();
            var draft = O("{effect:{id:'comet',ports:[1,2],params:{}},colors:{3:{color:'#DDEEFF'}},sfp:{9:{effect:'two_color',period:8},10:{color_brightness:25}}}");
            var plan = LightingState.LightingOperations(state, draft);
            var writes = Js.ArrayOf(Js.Objects(plan).Where(item => S(item, "key")!.StartsWith("sfp:", StringComparison.Ordinal)));
            e(writes.Count == 2 && Eq(writes.At("0.body"), "{effect:'two_color',period:8}") && Eq(writes.At("1.body"), "{color_brightness:25}"), "partial writes");
            var actual = Js.Clone(state["sfp"])!.AsArray();
            foreach (var write in Js.Objects(writes)) Js.Assign(Js.Objects(actual).First(item => S(write, "path")!.EndsWith("/" + Js.ToStr(item["port"]), StringComparison.Ordinal)), write["body"]);
            e(S(actual, "0.color") == "#FF0000" && S(actual, "1.color") == "#0000FF" && N(actual, "0.brightness") == 30 && N(actual, "1.brightness") == 70
              && S(actual, "0.effect") == "two_color" && S(actual, "1.effect") == "pulse", "applied");
            e(Eq(draft.At("sfp.9"), "{effect:'two_color',period:8}"), "draft unchanged");
        });
        T("lighting state: planning rejects crossed or unknown port scopes and never treats an invalid mode as release", e =>
        {
            var state = LightingBase();
            foreach (var ports in new[] { "[]", "[1,11]", "[0]", "[1.5]", "['1']" }) e(Throws(() => Ops(state, $"{{effect:{{id:'static',ports:{ports},params:{{}}}}}}"), "port"), ports);
            e(Eq(Js.Items(Ops(state, "{effect:{id:'static',ports:[1,9],params:{}}}").At("0.body.zones")).Last()?["ports"], "[1,9]"), "mixed group");
            e(Throws(() => Ops(state, "{colors:{9:{color:'#FFFFFF'}}}"), "Ethernet ports") && Throws(() => Ops(state, "{returnToEffect:[9]}"), "Ethernet ports"), "SFP is not copper");
            e(Throws(() => Ops(state, "{sfp:{11:{mode:'color'}}}"), "available SFP") && Throws(() => Ops(state, "{mode:'unknown'}"), "lighting mode"), "unknown targets");
        });
        T("lighting state: settings snapshots omit telemetry and compare object values independent of key order", e =>
        {
            var settings = LightingState.SfpSettings(With(Get(LightingBase(), "sfp.0"), "{display_rgb:[1,2,3],rule:{id:'alarm'},source:{kind:'rule'},control:'active'}"));
            e(settings.Select(pair => pair.Key).Order(StringComparer.Ordinal).SequenceEqual(["brightness", "color", "effect", "mode", "period", "secondary_color"]), "keys");
            e(LightingState.EqualSetting(A("[{color:'#123456',brightness:50},{brightness:50,color:'#123456'}]")), "key order");
            e(!LightingState.EqualSetting(A("[[1,2],[2,1]]")) && !LightingState.EqualSetting(new JsonArray()) && !LightingState.EqualSetting(A("[0,null]")), "differences");
        });
        T("lighting state: copper control source follows manual-over-rule-over-effect precedence", e =>
        {
            var state = LightingBase();
            state["rules"] = Arr(LightingRule());
            e(S(LightingState.ControlSource(state, 1), "kind") == "rule", "rule");
            state["overrides"] = O("{1:{color:'#FFFFFF'}}");
            e(S(LightingState.ControlSource(state, 1), "kind") == "color", "override");
            state.Remove("overrides");
            state["ports"]![0]!["source"] = O("{kind:'override'}");
            e(S(LightingState.ControlSource(state, 1), "kind") == "color", "reported override");
            state["ports"]![0]!["source"] = O("{kind:'zone',id:'left'}");
            e(S(LightingState.ControlSource(state, 1, A("[{id:'marquee',name:'Marquee'}]")), "label") == "Marquee", "effect");
            state["ports"]![0]!["source"] = O("{kind:'none'}");
            e(S(LightingState.ControlSource(state, 1), "kind") == "off" && S(LightingState.ControlSource(state, 999), "kind") == "unknown", "off and unknown");
        });
        T("lighting state: reported rule ownership wins over a later browser clock or incomplete rule list", e =>
        {
            var state = LightingBase();
            state["rules"] = Arr(LightingRule("{conditions:[{field:'link',op:'is',value:false}]}"));
            state["ports"]![0]!["source"] = O("{kind:'rule',id:'alarm',name:'Alarm'}");
            e(S(LightingState.ControlSource(state, 1), "kind") == "rule", "reported");
            state["rules"] = new JsonArray();
            e(S(LightingState.ControlSource(state, 1), "label") == "Alarm", "reported name");
            state["sfp"]![0]!["rule"] = O("{id:'remote',name:'SFP alarm'}");
            e(S(LightingState.ControlSource(state, 9), "label") == "SFP alarm", "SFP rule");
            state["sfp"]![0]!["mode"] = "off";
            e(S(LightingState.ControlSource(state, 9), "kind") == "off", "SFP off");
        });
        T("lighting state: SFP fallback requires explicit scope and respects link-managed and off semantics", e =>
        {
            var state = LightingBase();
            state["sfp"]![0]!["mode"] = "unmanaged";
            state["rules"] = Arr(LightingRule("{ports:null}"));
            e(S(LightingState.ControlSource(state, 9), "kind") == "link", "copper scope");
            state["rules"] = Arr(LightingRule("{ports:[9]}"));
            e(S(LightingState.ControlSource(state, 9), "kind") == "rule", "SFP scope");
            state["rules"]![0]!["look"]!["style"] = "effect";
            e(S(LightingState.ControlSource(state, 9), "kind") == "link", "effect style on link status");
            state["sfp"]![0]!["mode"] = "color";
            e(S(LightingState.ControlSource(state, 9), "kind") == "rule", "effect style on color");
            state["sfp"]![0]!["rule"] = null;
            e(S(LightingState.ControlSource(state, 9), "kind") == "sfp", "reported no rule");
            state["sfp"]![0]!["mode"] = "off";
            e(S(LightingState.ControlSource(state, 9), "kind") == "off", "off");
        });
        T("lighting state: Together control source uses the lowest actual SFP ID and preserves target rule ownership", e =>
        {
            foreach (var first in new[] { 17, 25 })
            {
                var state = LightingBase();
                state["num_ports"] = first - 1;
                state["sfp_sync"] = O("{mode:'together',source_port:1}");
                state["sfp"] = Arr(With(O("{mode:'unmanaged',effect:'steady'}"), $"{{port:{first + 1}}}"), With(O("{mode:'color',effect:'pulse',color:'#123456'}"), $"{{port:{first}}}"));
                e(S(LightingState.ControlSource(state, first + 1), "label") == "Shared Gentle pulse", $"{first} shared");
                state["rules"] = Arr(LightingRule($"{{ports:[{first + 1}],look:{{style:'effect'}}}}"));
                e(S(LightingState.ControlSource(state, first + 1), "kind") == "rule" && S(LightingState.ControlSource(state, first), "kind") == "sfp", $"{first} rule");
                state["sfp"]![1]!["mode"] = "off";
                e(S(LightingState.ControlSource(state, first + 1), "label") == "SFP lights off", $"{first} off");
                state["sfp"]![1]!["mode"] = "unmanaged";
                e(S(LightingState.ControlSource(state, first + 1), "kind") == "link" && S(LightingState.ControlSource(state, 9), "kind") == "unknown", $"{first} link");
            }
        });
        T("lighting state: atomic lighting snapshot preserves unrelated groups, rules, pins and SFP settings without mutating inputs", e =>
        {
            var state = LightingBase();
            state["rules"] = Arr(LightingRule());
            var before = LightingSnapshotOf(state);
            var draft = O("{brightness:60,effect:{id:'comet',ports:[1,2],params:{color:'#334455'},clearOverrides:true},colors:{3:{color:'#ABCDEF',brightness:35}},sfp:{9:{color_brightness:40}},sfp_sync:{mode:'together'}}");
            var original = new JsonArray(before.DeepClone(), state.DeepClone(), draft.DeepClone());
            var result = LightingState.LightingSnapshot(before, state, draft);
            e(Eq(new JsonArray(before.DeepClone(), state.DeepClone(), draft.DeepClone()), original) && !ReferenceEquals(result, before), "inputs unchanged");
            e(N(result, "version") == 2 && N(result, "brightness") == 60 && Eq(result["rules"], before["rules"]), "scene fields");
            e(Eq(Js.Objects(result["zones"]).First(zone => S(zone, "id") == "right"), Get(before, "zones.1")), "unrelated group");
            e(Eq(Js.Objects(result["zones"]).First(zone => S(zone, "id") == "left")["ports"], "[3,4]") && Js.Objects(result["zones"]).Count(zone => Js.Includes(zone["ports"], 1)) == 1, "regrouped");
            e(Eq(Js.Items(result["zones"]).Last()?["effect"], "{id:'comet',params:{color:'#334455'}}"), "effect");
            e(!Js.Has(result["overrides"], "1") && !Js.Has(result["overrides"], "2") && Eq(result.At("overrides.8"), before.At("overrides.8")) && Eq(result.At("overrides.3"), draft.At("colors.3")), "overrides");
            e(Eq(result.At("sfp.9"), With(before.At("sfp.9"), "{color_brightness:40}")) && Eq(result.At("sfp.10"), before.At("sfp.10")), "SFP");
            e(Eq(result["sfp_sync"], "{mode:'together',source_port:1}"), "sync");
        });
        T("lighting state: parameter edits retain fixed pins unless explicit effect assignment requests their replacement", e =>
        {
            var state = LightingBase();
            var before = LightingSnapshotOf(state);
            var effect = O("{id:'marquee',ports:[1,2,3,4],params:{color:'#112233'}}");
            var adjusted = LightingState.LightingSnapshot(before, state, new JsonObject { ["effect"] = effect.DeepClone() });
            e(Eq(adjusted["overrides"], before["overrides"]) && Js.Length(adjusted["zones"]) == Js.Length(before["zones"]), "pins kept");
            var assigned = LightingState.LightingSnapshot(before, state, new JsonObject { ["effect"] = With(effect, "{clearOverrides:true}") });
            e(Js.Keys(assigned["overrides"]).SequenceEqual(["8"]), "pins replaced");
            e(Js.IsFalse(LightingState.LightingOperations(state, new JsonObject { ["effect"] = effect.DeepClone() }).At("0.body.clear_overrides")), "request flag");
        });
        T("lighting state: snapshot uses authoritative base groups, preserves additional scene fields and safely handles encoded group IDs", e =>
        {
            var state = LightingBase();
            var before = LightingSnapshotOf(state);
            before["metadata"] = O("{futureSetting:true}");
            before["zones"]![0]!["id"] = "group with / and %";
            before["zones"]![0]!["name"] = "Current name";
            before["zones"]![1]!["effect"]!["params"]!["color"] = "#777777";
            var result = LightingState.LightingSnapshot(before, state, O("{effect:{id:'static',ports:[1,2,3,4],params:{color:'#FFFFFF'}}}"));
            e(S(result, "zones.0.id") == "group with / and %" && S(result, "zones.0.name") == "Current name", "encoded id");
            e(Eq(Get(result, "zones.1"), Get(before, "zones.1")) && Eq(result["metadata"], "{futureSetting:true}"), "other fields");
        });
        T("lighting state: snapshot scopes return-to-effect and manual edits independently with fixed colors winning explicitly", e =>
        {
            var state = LightingBase();
            var before = LightingSnapshotOf(state);
            var result = LightingState.LightingSnapshot(before, state, O("{returnToEffect:[1,2],colors:{2:{color:'#000000',brightness:null}}}"));
            e(!Js.Has(result["overrides"], "1") && Eq(result.At("overrides.2"), "{color:'#000000',brightness:null}"), "edits");
            e(Eq(result.At("overrides.8"), before.At("overrides.8")) && Eq(result["zones"], before["zones"]), "others");
        });
        T("lighting state: empty and equal-brightness drafts keep the complete scene unchanged and mode is separate from configuration", e =>
        {
            var state = LightingBase();
            var before = LightingSnapshotOf(state);
            e(Eq(LightingState.LightingSnapshot(before, state, new JsonObject()), before), "empty");
            e(Ops(state, "{brightness:100}").Count == 0, "equal brightness");
            e(Eq(LightingState.LightingSnapshot(before, state, O("{brightness:100,mode:'released'}")), before), "mode not in scene");
            e(Keys(Ops(state, "{brightness:100,mode:'released'}")).SequenceEqual(["mode"]), "mode operation");
        });
        T("lighting state: restored drafts reject malformed structure, nonfinite and out-of-range values before planning", e =>
        {
            var state = LightingBase();
            foreach (var draft in new[] { "null", "[]", "1", "{unknown:true}", "{brightness:null}", "{brightness:'50'}", "{brightness:NaN}", "{brightness:Infinity}", "{brightness:-1}",
                         "{brightness:101}", "{brightness:1.5}", "{effect:null}", "{effect:{}}", "{effect:{id:'unknown',ports:[1]}}", "{effect:{id:'static',ports:[1],params:null}}",
                         "{effect:{id:'static',ports:[1],params:[]}}", "{effect:{id:'static',ports:[1],params:{unknown:1}}}", "{effect:{id:'static',ports:[1],params:{color:'red'}}}",
                         "{effect:{id:'breathing',ports:[1],params:{period:0}}}", "{effect:{id:'static',ports:[1],params:{random_color:1}}}",
                         "{effect:{id:'daylight',ports:[1],params:{palette:'missing'}}}", "{effect:{id:'static',ports:[1],clearOverrides:'true'}}",
                         "{colors:null}", "{colors:[]}", "{colors:{1:{color:'#abc'}}}", "{colors:{1:{color:'#123456',brightness:101}}}", "{colors:{'01':{color:'#123456'}}}",
                         "{colors:{1:{color:'#123456',extra:true}}}", "{returnToEffect:null}", "{returnToEffect:false}", "{mode:null}" })
                e(Throws(() => Ops(state, draft)), draft);
        });
        T("lighting state: restored SFP settings validate enums, color channels, duration and individual brightness", e =>
        {
            var state = LightingBase();
            foreach (var settings in new[] { "null", "[]", "{extra:true}", "{mode:'invalid'}", "{effect:'comet'}", "{color:'blue'}", "{secondary_color:'#gggggg'}",
                         "{brightness:1.5}", "{brightness:101}", "{color_brightness:-1}", "{secondary_brightness:null}", "{period:1}", "{period:31}", "{period:NaN}" })
                e(Throws(() => Ops(state, $"{{sfp:{{9:{settings}}}}}")), settings);
            e(Throws(() => Ops(state, "{sfp:{'09':{mode:'off'}}}"), "available SFP") && Throws(() => Ops(state, "{sfp:null}"), "SFP lighting changes") && Throws(() => Ops(state, "{sfp_sync:null}"), "SFP sync"), "structure");
            e(Eq(Ops(state, "{sfp:{9:{mode:'color',effect:'two_color',color:'#abcdef',secondary_color:'#012345',brightness:0,color_brightness:100,secondary_brightness:25,period:2}}}").At("0.body"),
                "{mode:'color',color:'#abcdef',brightness:0,effect:'two_color',secondary_color:'#012345',period:2,color_brightness:100,secondary_brightness:25}"), "valid body");
        });
        T("lighting state: mutually exclusive effect options cannot both survive restored drafts", e =>
            e(Throws(() => Ops(LightingBase(), "{effect:{id:'strobe',ports:[1],params:{random_color:true,random_colors:true}}}"), "conflicting")));
        T("lighting state: every active catalog effect accepts its complete declared default parameters", e =>
        {
            var state = LightingBase();
            var before = LightingSnapshotOf(state);
            foreach (var effect in Js.Objects(Effects.EffectList))
            {
                var parameters = new JsonObject();
                foreach (var param in Js.Objects(effect["params"])) parameters[S(param, "key")!] = Js.Clone(param["default"]);
                var draft = new JsonObject { ["effect"] = new JsonObject { ["id"] = Js.Clone(effect["id"]), ["ports"] = A("[1,2,3,4]"), ["params"] = parameters.DeepClone() } };
                var result = LightingState.LightingSnapshot(before, state, draft);
                e(Eq(Get(result, "zones.0.effect"), new JsonObject { ["id"] = Js.Clone(effect["id"]), ["params"] = parameters.DeepClone() }), S(effect, "id"));
            }
        });
        T("lighting state: snapshot rejects an unavailable base or SFP settings missing from the authoritative scene", e =>
        {
            var state = LightingBase();
            foreach (var before in new[] { "null", "{}", "[]", "{zones:[],rules:[],overrides:{},sfp:[]}" })
                e(Throws(() => LightingState.LightingSnapshot(J(before), state, new JsonObject()), "Refresh"), before);
            var snapshot = LightingSnapshotOf(state);
            ((JsonObject)snapshot["sfp"]!).Remove("9");
            e(Throws(() => LightingState.LightingSnapshot(snapshot, state, O("{sfp:{9:{mode:'off'}}}")), "SFP light changed"), "missing light");
        });

        // tests/preview-lighting-state.test.mjs
        JsonObject Current() => O("""
            {brightness:40,ports:[{port:1,color:'#123456'},{port:2,color:'#ABCDEF'}],overrides:{2:{color:'#FFEEDD',brightness:65}},sfp_sync:{mode:'independent',source_port:1},
             sfp:[{port:9,color:'#00AAFF',brightness:100,color_brightness:60},{port:10,color:'#FF00AA',brightness:50,color_brightness:80}]}
            """);
        T("lighting state: mixed fixed-color controls display the SFP color brightness they edit", e =>
        {
            var current = Current();
            var draft = O("{sfp:{9:{color_brightness:25}}}");
            var before = new JsonArray(current.DeepClone(), draft.DeepClone());
            e(Eq(LightingState.FixedColorSettings(current, 9), "{color:'#00AAFF',brightness:60}") && Eq(LightingState.FixedColorSettings(current, 9, draft), "{color:'#00AAFF',brightness:25}"), "draft");
            Js.Assign((JsonObject)current["sfp"]![0]!, draft.At("sfp.9"));
            e(N(LightingState.FixedColorSettings(current, 9), "brightness") == 25, "applied");
            current["sfp"]![0]!["color_brightness"] = 60;
            e(Eq(new JsonArray(current.DeepClone(), draft.DeepClone()), before), "unchanged");
        });
        T("lighting state: legacy SFP brightness is composed once, including zero and fractional products", e =>
        {
            var current = Current();
            e(N(LightingState.FixedColorSettings(current, 10), "brightness") == 40, "composed");
            e(N(LightingState.FixedColorSettings(current, 10, O("{sfp:{10:{brightness:25,color_brightness:33}}}")), "brightness") == 8, "fractional");
            e(N(LightingState.FixedColorSettings(current, 10, O("{sfp:{10:{brightness:0}}}")), "brightness") == 0 && N(LightingState.FixedColorSettings(current, 10, O("{sfp:{10:{color_brightness:0}}}")), "brightness") == 0, "zero");
            e(LightingState.FixedColorBrightness(LightingState.FixedColorSettings(current, 10), current) == 40, "brightness");
        });
        T("lighting state: Together displays the shared SFP setting instead of the dormant indicator setting", e =>
        {
            var current = Current();
            var draft = O("{sfp_sync:{mode:'together'},sfp:{9:{color:'#112233',color_brightness:30},10:{color_brightness:5}}}");
            var before = new JsonArray(current.DeepClone(), draft.DeepClone());
            e(Eq(LightingState.FixedColorSettings(current, 10, draft), "{color:'#112233',brightness:30}") && Eq(LightingState.FixedColorSettings(current, 9, draft), LightingState.FixedColorSettings(current, 10, draft)), "shared");
            e(Eq(new JsonArray(current.DeepClone(), draft.DeepClone()), before), "unchanged");
        });
        T("lighting state: copper overrides remain absolute and inherited brightness remains inherited", e =>
        {
            var current = Current();
            var draft = O("{brightness:20}");
            var before = Js.Clone(current);
            var inherited = LightingState.FixedColorSettings(current, 1, draft);
            var pinned = LightingState.FixedColorSettings(current, 2, draft);
            e(Eq(inherited, "{color:'#123456',brightness:null}") && LightingState.FixedColorBrightness(inherited, current, draft) == 20 && LightingState.FixedColorBrightness(inherited, current) == 40, "inherited");
            e(LightingState.FixedColorBrightness(pinned, current, draft) == 65, "pinned");
            e(Eq(LightingState.FixedColorSettings(current, 2, O("{colors:{2:{color:'#001122',brightness:null}}}")), "{color:'#001122',brightness:null}"), "draft color");
            pinned["brightness"] = 1;
            e(Eq(current, before), "copies");
        });
        T("lighting state: SFP membership follows actual port IDs and no unknown port is fabricated", e =>
        {
            var current = Current();
            current["sfp"]![0]!["port"] = 25;
            current["sfp"]![1]!["port"] = 26;
            current["sfp_sync"]!["mode"] = "together";
            e(Eq(LightingState.FixedColorSettings(current, 26), "{color:'#00AAFF',brightness:60}"), "shared");
            e(Throws(() => LightingState.FixedColorSettings(current, 9), "available port"), "unknown");
        });
        JsonObject OnlineRule(string ports = "[2]") =>
            O($"{{id:'online',name:'Online color',enabled:true,ports:{ports},match:'all',conditions:[{{field:'link',op:'is',value:true}}],look:{{style:'solid',color:'#FFFFFF'}}}}");
        JsonObject Accepted() => With(O("{overrides:{},sfp:{9:{mode:'color'},10:{mode:'color'}},sfp_sync:{mode:'independent',source_port:1}}"), "{}").Also(scene => scene["rules"] = Arr(OnlineRule()));
        JsonArray Matches(JsonObject scene, JsonObject current, string ports) => LightingState.SceneAutomationMatches(scene, current, A(ports));
        T("lighting state: accepted-scene automation feedback ignores stale copper override frames after an override is cleared", e =>
        {
            var current = Current();
            var scene = Accepted();
            Js.Assign((JsonObject)current["ports"]![1]!, J("{facts:{link:true},override:{color:'#FF0000'},source:{kind:'override'}}"));
            current["rules"] = Arr(OnlineRule());
            var before = new JsonArray(current.DeepClone(), scene.DeepClone());
            e(Eq(Js.ArrayOf(Js.Objects(Matches(scene, current, "[2,'2',99]")).Select(match => (JsonNode?)new JsonArray(Js.Clone(match["port"]), Js.Clone(match.At("rule.name"))))), "[[2,'Online color']]"), "matches");
            e(S(LightingState.ControlSource(current, 2), "kind") == "color" && Eq(new JsonArray(current.DeepClone(), scene.DeepClone()), before), "frame state");
        });
        T("lighting state: accepted manual colors take precedence over stale rule output in Apply feedback", e =>
        {
            var current = Current();
            var scene = Accepted();
            Js.Assign((JsonObject)current["ports"]![1]!, J("{facts:{link:true},source:{kind:'rule',id:'online'}}"));
            current["overrides"] = new JsonObject();
            current["rules"] = Arr(OnlineRule());
            scene["overrides"]!["2"] = O("{color:'#0066FF',brightness:70}");
            e(Matches(scene, current, "[2]").Count == 0 && S(LightingState.ControlSource(current, 2), "kind") == "rule");
        });
        T("lighting state: Apply feedback evaluates accepted rule scope, enabled state, priority, and current facts", e =>
        {
            var current = Current();
            var scene = Accepted();
            current["ports"]![1]!["facts"] = O("{link:true}");
            current["ports"]![1]!["source"] = O("{kind:'rule',id:'old'}");
            current["rules"] = Arr(With(OnlineRule(), "{id:'old'}"));
            scene["rules"] = Arr(With(OnlineRule(), "{enabled:false}"), With(OnlineRule("[1]"), "{id:'other-port'}"), OnlineRule(), With(OnlineRule(), "{id:'lower'}"));
            e(S(Matches(scene, current, "[2]"), "0.rule.id") == "online", "priority");
            current["ports"]![1]!["facts"]!["link"] = false;
            e(Matches(scene, current, "[2]").Count == 0, "facts");
            scene["rules"] = new JsonArray();
            current["ports"]![1]!["facts"]!["link"] = true;
            e(Matches(scene, current, "[2]").Count == 0, "no rules");
        });
        T("lighting state: SFP feedback uses accepted modes and explicit SFP rule scope instead of stale frame rules", e =>
        {
            var current = Current();
            var scene = Accepted();
            foreach (var port in Js.Objects(current["sfp"])) Js.Assign(port, J("{link:true,mode:'off',rule:{id:'stale'}}"));
            scene["rules"] = Arr(OnlineRule("null"), With(OnlineRule("[9,10]"), "{id:'sfp'}"));
            e(Eq(Js.ArrayOf(Js.Objects(Matches(scene, current, "[9,10]")).Select(match => (JsonNode?)new JsonArray(Js.Clone(match["port"]), Js.Clone(match.At("rule.id"))))), "[[9,'sfp'],[10,'sfp']]"), "scoped");
            scene["sfp"]!["9"]!["mode"] = "off";
            e(Js.Objects(Matches(scene, current, "[9,10]")).Select(match => N(match, "port")).SequenceEqual([10.0]), "accepted off");
            scene["rules"] = Arr(OnlineRule("null"));
            e(Matches(scene, current, "[9,10]").Count == 0, "copper scope");
        });
        T("lighting state: shared and Ethernet-follow SFP modes use accepted settings while retaining each port facts", e =>
        {
            var current = Current();
            var scene = Accepted();
            foreach (var port in Js.Objects(current["sfp"])) Js.Assign(port, J("{link:true,mode:'color'}"));
            scene["rules"] = Arr(OnlineRule("[9,10]"));
            scene["sfp"]!["9"]!["mode"] = "off";
            scene["sfp_sync"]!["mode"] = "together";
            e(Matches(scene, current, "[9,10]").Count == 0, "shared off");
            scene["sfp"]!["9"]!["mode"] = "color";
            scene["sfp"]!["10"]!["mode"] = "off";
            current["sfp"]![0]!["link"] = false;
            e(Js.Objects(Matches(scene, current, "[9,10]")).Select(match => N(match, "port")).SequenceEqual([10.0]), "own facts");
            scene["sfp"]!["9"]!["mode"] = "off";
            scene["sfp_sync"]!["mode"] = "ethernet";
            current["sfp"]![0]!["link"] = true;
            e(Js.Objects(Matches(scene, current, "[9,10]")).Select(match => N(match, "port")).SequenceEqual([9.0, 10]), "following");
        });
        T("lighting state: unmanaged SFP feedback matches solid automation but not a pass-through effect", e =>
        {
            var current = Current();
            var scene = Accepted();
            current["sfp"]![0]!["link"] = true;
            scene["sfp"]!["9"]!["mode"] = "unmanaged";
            scene["rules"] = Arr(OnlineRule("[9]"));
            e(Matches(scene, current, "[9]").Count == 1, "solid");
            scene["rules"]![0]!["look"]!["style"] = "effect";
            e(Matches(scene, current, "[9]").Count == 0, "effect");
            scene["sfp_sync"]!["mode"] = "ethernet";
            e(Matches(scene, current, "[9]").Count == 1, "following");
        });
        T("lighting state: automation feedback uses supplied schedule time and does not invent a match when clock is absent", e =>
        {
            var current = Current();
            var scene = Accepted();
            scene["rules"] = Arr(With(OnlineRule(), "{conditions:[{field:'time',op:'between',value:['20:00','23:00']}]}"));
            e(LightingState.SceneAutomationMatches(scene, current, A("[2]"), new DateTime(2026, 1, 1, 21, 0, 0)).Count == 1, "inside");
            e(LightingState.SceneAutomationMatches(scene, current, A("[2]"), new DateTime(2026, 1, 1, 12, 0, 0)).Count == 0, "outside");
            e(Matches(scene, current, "[2]").Count == 0, "no clock");
        });
    }

    // ----- preview/secondary-model.mjs (tests/secondary-model.test.mjs) -----

    static void SecondaryModelChecks()
    {
        JsonObject Scene() => O("""
            {brightness:50,zones:[{id:'desk',name:'Desk',ports:[1,2],effect:{id:'static',name:'Static',params:{color:'#ABCDEF'}}}],
             rules:[],overrides:{'1':{color:'#123456',brightness:null}},sfp:{'9':{mode:'color',color:'#FFFFFF',brightness:80,effect:'steady',period:6,secondary_color:'#006fff',color_brightness:100,secondary_brightness:100}}}
            """);
        T("secondary: profile comparison ignores telemetry but includes pins, brightness and rule precedence", e =>
        {
            var saved = Scene();
            var live = Js.Clone(saved);
            live["sfp"] = Js.ArrayOf(Js.Entries(live["sfp"]).Select(entry =>
            {
                var light = Js.Spread(entry.Value, J("{display_rgb:[50,60,70],preview:'#112233',link:{up:false}}"));
                light["port"] = Js.StringToNumber(entry.Key);
                return (JsonNode?)light;
            }));
            live["revision"] = 33;
            live["zones"]![0]!["effect"]!["name"] = "Localized name";
            live["zones"]![0]!["effect"]!["params"]!["color"] = "#abcdef";
            live["zones"]![0]!["ports"] = A("[2,1]");
            e(SecondaryModel.SceneFingerprint(live) == SecondaryModel.SceneFingerprint(saved) && SecondaryModel.MatchingProfile(new JsonObject { ["Evening"] = saved.DeepClone() }, live) == "Evening", "same settings");
            live["overrides"]!["1"]!["brightness"] = 42;
            e(SecondaryModel.MatchingProfile(new JsonObject { ["Evening"] = saved.DeepClone() }, live) is null, "pins");
            var a = Scene();
            var b = Scene();
            a["rules"] = A("[{id:'a'},{id:'b'}]");
            b["rules"] = A("[{id:'b'},{id:'a'}]");
            e(SecondaryModel.SceneFingerprint(a) != SecondaryModel.SceneFingerprint(b), "rule order");
        });
        T("secondary: profile SFP summaries describe active synchronization instead of dormant per-port settings", e =>
        {
            var saved = O("{sfp:{9:{mode:'color',effect:'pulse'},10:{mode:'unmanaged',effect:'steady'}},sfp_sync:{mode:'together',source_port:5}}");
            var before = Js.Clone(saved);
            e(Eq(SecondaryModel.ProfileSfpSummary(saved), "[{port:9,label:'Gentle pulse · Together'},{port:10,label:'Gentle pulse · Together'}]"), "together");
            e(Eq(SecondaryModel.ProfileSfpSummary(With(saved, "{sfp_sync:{mode:'ethernet',source_port:5}}")), "[{port:9,label:'Following port 5'},{port:10,label:'Following port 5'}]"), "ethernet");
            e(Eq(SecondaryModel.ProfileSfpSummary(With(saved, "{sfp_sync:{mode:'independent'}}")), "[{port:9,label:'Gentle pulse'},{port:10,label:'Link status'}]"), "independent");
            var list = Js.Spread(saved);
            list["sfp"] = Js.ArrayOf(Js.Entries(saved["sfp"]).Select(entry => (JsonNode?)Js.Spread(entry.Value).Also(light => light["port"] = Js.StringToNumber(entry.Key))));
            e(Eq(SecondaryModel.ProfileSfpSummary(list), SecondaryModel.ProfileSfpSummary(saved)) && Eq(saved, before), "array form");
        });
        T("secondary: rule templates retain SFP scope, enabled state and existing name", e =>
        {
            var draft = O("{id:'alert',name:'Rack alert',ports:[9,10],enabled:false,match:'any',conditions:[],look:{color:'#fff'}}");
            var template = O("{name:'Anomaly',conditions:[{field:'anomaly',op:'is',value:true}],look:{color:'#ff0000'}}");
            var applied = SecondaryModel.TemplateForRule(template, draft, [1, 2, 3]);
            e(Eq(applied["ports"], "[9,10]") && S(applied, "name") == "Rack alert" && Js.IsFalse(applied["enabled"]) && S(applied, "id") == "alert", "kept");
            applied["conditions"]![0]!["value"] = false;
            e(Js.IsTrue(template.At("conditions.0.value")), "copied conditions");
            e(Eq(SecondaryModel.TemplateForRule(template, With(draft, "{ports:[]}"), [1, 2])["ports"], "[]"), "empty scope");
            e(S(SecondaryModel.TemplateForRule(template, With(draft, "{name:''}"), [1, 2]), "name") == "Anomaly" && S(SecondaryModel.TemplateForRule(template, With(draft, "{name:'   '}"), [1, 2]), "name") == "Anomaly", "template name");
        });
        T("secondary: rules cannot apply an empty scope or silently become unconditional", e =>
        {
            var draft = O("{name:'Alert',ports:[9],conditions:[{field:'anomaly',op:'is',value:true}]}");
            e(SecondaryModel.ValidateRuleDraft(draft) == "", "valid");
            e(SecondaryModel.ValidateRuleDraft(With(draft, "{conditions:[]}")).Contains("condition") && SecondaryModel.ValidateRuleDraft(With(draft, "{ports:[]}")).Contains("port"), "missing parts");
            e(SecondaryModel.ValidateRuleDraft(With(draft, "{conditions:[{field:'weekday',op:'in',value:[]}]}")).Contains("value"), "empty value");
        });
        T("secondary: bulk scope summaries distinguish Ethernet, SFP, all and empty", e =>
        {
            e(SecondaryModel.ScopeSummary(A("[1,2,9,10]"), [1, 2], [9, 10]) == "All ports" && SecondaryModel.ScopeSummary(null, [1, 2], [9, 10]) == "All Ethernet ports", "all");
            e(SecondaryModel.ScopeSummary(A("[9,10]"), [1, 2], [9, 10]) == "All SFP ports" && SecondaryModel.ScopeSummary(A("[]"), [1, 2], [9, 10]) == "No ports selected", "SFP and empty");
        });
        T("secondary: profile name validation agrees with the store character and length rules", e =>
        {
            foreach (var name in new[] { "Evening", "Café 2", "Desk_v2.0-test", new string('A', 64), "夜間 ²" }) e(SecondaryModel.ProfileNameError(name) == "", name);
            foreach (var name in new[] { "", "  ", ".", "..", " . ", " .. ", new string('A', 65), "Desk / Night", "Desk: Night", "Desk\nNight", "Desk💡" })
                e(SecondaryModel.ProfileNameError(name) != "", name);
        });
        T("secondary: unavailable rule fields and operators require replacement without rejecting valid condition values", e =>
        {
            var specs = A("[{key:'anomaly',operators:['is','is_not']}]");
            var draft = O("{name:'Alert',ports:[1],conditions:[{field:'anomaly',op:'is',value:true}]}");
            e(SecondaryModel.ConditionAvailable(draft.At("conditions.0"), specs) && SecondaryModel.ValidateRuleDraft(draft, specs) == "", "available");
            foreach (var condition in new[] { "null", "{field:'old_field',op:'is',value:true}", "{field:'anomaly',op:'old_operator',value:true}" })
                e(!SecondaryModel.ConditionAvailable(J(condition), specs) && SecondaryModel.ValidateRuleDraft(With(draft, $"{{conditions:[{condition}]}}"), specs).Contains("unavailable"), condition);
        });
        T("secondary: unchanged cycle controls retain the exact saved rate, including faster legacy settings", e =>
        {
            foreach (var rate in new[] { .123456789, .333333333333, 2 })
                e(SecondaryModel.RuleRateFromCycle(rate, Js.NumberToString(1 / rate), Js.NumberToString(1 / rate)) == rate, Js.NumberToString(rate));
            e(SecondaryModel.RuleRateFromCycle(.5, "2", "2.00") == .5 && SecondaryModel.RuleRateFromCycle(.5, "2", "4") == .25, "changed cycle");
        });
        T("secondary: rule snapshot replacement preserves the loaded scene and does not mutate the draft", e =>
        {
            var before = Scene();
            var rules = A("[{id:'rule-b',name:'Second'},{id:'rule-a',name:'First'}]");
            var saved = new JsonArray(before.DeepClone(), rules.DeepClone());
            var result = SecondaryModel.RulesSnapshot(before, rules);
            e(Eq(With(result, "{rules:[]}"), before) && Eq(result["rules"], rules) && Eq(new JsonArray(before.DeepClone(), rules.DeepClone()), saved), "replaced");
            result["rules"]![0]!["name"] = "Changed";
            e(S(rules, "0.name") == "Second", "copied");
            foreach (var invalid in new[] { "[{id:'same'},{id:'same'}]", "[{name:'No ID'}]", "[null]" }) e(Throws(() => SecondaryModel.RulesSnapshot(before, A(invalid)), "Refresh"), invalid);
        });
        T("secondary: pending writes block duplicate actions and settle waits for completion", e =>
        {
            var release = new TaskCompletionSource();
            var writes = 0;
            var busy = new List<bool>();
            var errors = new List<Exception>();
            var gate = SecondaryModel.CreateOperationGate(value => { lock (busy) busy.Add(value); }, error => { lock (errors) errors.Add(error); });
            var pending = gate.Run(async () => { Interlocked.Increment(ref writes); await release.Task; });
            var duplicate = gate.Run(() => { Interlocked.Increment(ref writes); });
            e(ReferenceEquals(pending, duplicate), "same pending task");
            var leaving = gate.Settle();
            Thread.Sleep(50);
            e(Volatile.Read(ref writes) == 1 && !leaving.IsCompleted, "waiting");
            release.SetResult();
            var left = leaving.GetAwaiter().GetResult();
            e(busy.SequenceEqual([true, false]) && left && errors.Count == 0, "settled");
            gate.Run(new Action(() => throw new InvalidOperationException("Failed"))).GetAwaiter().GetResult();
            e(errors.Count == 1, "error reported");
            gate.Close();
            gate.Run(() => { Interlocked.Increment(ref writes); }).GetAwaiter().GetResult();
            e(writes == 1 && !gate.Settle().GetAwaiter().GetResult(), "closed");
        });
        T("secondary: saved optical group effects take precedence over the saved Follow Ethernet description", e =>
        {
            var saved = O("{version:3,topology:{version:1,copper_ports:[1],sfp_ports:[9,10]},sfp_sync:{mode:'ethernet',source_port:1},zones:[{ports:[1,10],effect:{id:'activity_glow'}}],sfp:{9:{mode:'off'},10:{mode:'off'}}}");
            e(Eq(SecondaryModel.ProfileSfpSummary(saved), "[{port:9,label:'Following port 1'},{port:10,label:'Activity Glow'}]"));
        });
    }

    // ----- preview/scene-transaction.mjs (tests/preview-api.test.mjs) -----

    static void SceneTransactionChecks()
    {
        var scene = O("{brightness:100,zones:[],rules:[],overrides:{},sfp:{}}");
        T("scene transaction: atomic transaction sends one versioned config and preserves unchanged phases", e =>
        {
            var calls = new List<JsonArray>();
            var after = Await(() => SceneTransaction.CommitScene(O("{revision:'before'}"), scene, (path, method, body) =>
            {
                calls.Add(new JsonArray(path, method, Js.Clone(body)));
                return Task.FromResult<JsonNode?>(new JsonObject { ["revision"] = "after", ["scene"] = scene.DeepClone() });
            }));
            var expected = new JsonArray("/api/scene", "PUT", new JsonObject
            {
                ["scene"] = scene.DeepClone(),
                ["expected_revision"] = "before",
                ["preserve_unchanged"] = true,
                ["confirm_output"] = true,
            });
            e(calls.Count == 1 && Eq(calls[0], expected) && S(after, "revision") == "after");
        });
        T("scene transaction: accepted write with a lost reply is reconciled, never automatically resent", e =>
        {
            foreach (var code in new[] { "connection_unavailable", "output_unconfirmed", "scene_changed" })
            {
                var calls = new List<string>();
                var failure = new ApiException("Confirmation unavailable", code, 0, code == "connection_unavailable", null);
                var error = Rejection(() => SceneTransaction.CommitScene(O("{revision:'before'}"), scene, (path, method, body) =>
                {
                    calls.Add(method);
                    if (method == "PUT") throw failure;
                    return Task.FromResult<JsonNode?>(new JsonObject { ["revision"] = "after", ["scene"] = scene.DeepClone() });
                }));
                e(ReferenceEquals(error, failure) && S(SceneTransaction.AcceptedScene(failure), "revision") == "after", $"{code} reconciled");
                e(calls.Count(method => method == "PUT") == 1 && calls.Count == 2, $"{code} not resent");
            }
        });
        T("scene transaction: reconciliation cannot mistake another configuration for the requested one", e =>
        {
            var failure = new ApiException("Conflict", "scene_changed", 0, false, null);
            var error = Rejection(() => SceneTransaction.CommitScene(O("{revision:'before'}"), scene, (path, method, body) =>
                method == "PUT" ? throw failure : Task.FromResult<JsonNode?>(new JsonObject { ["revision"] = "other", ["scene"] = With(scene, "{brightness:20}") })));
            e(ReferenceEquals(error, failure) && SceneTransaction.AcceptedScene(failure) is null);
        });
        T("request errors: every refusal body becomes an ApiException with the browser's message", e =>
        {
            var list = EeApi.FromError(422, true, O("{detail:[{loc:['body','ports'],msg:'x',type:'missing'}]}"));
            e(list.Message == "The change could not be confirmed. Try again." && list.Code is null && list.Status == 422 && !list.Uncertain, "validation list");
            var mistyped = EeApi.FromError(409, true, O("{detail:{message:5,code:3}}"));
            e(mistyped.Message == "The change could not be confirmed. Try again." && mistyped.Code is null && mistyped.Details.Int("code") == 3, "mistyped fields");
            var text = EeApi.FromError(503, false, O("{detail:'The EE service is unavailable.'}"));
            e(text.Message == "The EE service is unavailable." && text.Code is null && !text.Uncertain, "message text");
            var coded = EeApi.FromError(500, true, O("{detail:{message:'Lighting was not confirmed.',code:'output_unconfirmed'}}"));
            e(coded.Message == "Lighting was not confirmed." && coded.Code == "output_unconfirmed" && coded.Uncertain, "message and code");
            e(EeApi.FromError(502, true, null).Message == "The change could not be confirmed. Try again.", "no body");
        });
    }

    static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }

    // ----- preview/effect-gallery.mjs logic (tests/preview-gallery.test.mjs, tests/preview-gallery-drafts.test.mjs) -----

    static void GalleryChecks()
    {
        var marquee = CatalogEffect("marquee");
        JsonObject Scene() => O("{mode:'running',num_ports:8,brightness:51,zones:[{id:'all',ports:[1,2],effect:{id:'marquee',params:{color:'#4DA6FF',background:'#E472B5'}}}],ports:[{port:1,source:{kind:'zone',id:'all'}},{port:2,source:{kind:'zone',id:'all'}}]}");
        T("gallery: applied gallery state follows actual manual and automation ownership", e =>
        {
            var state = Scene();
            e(S(EffectGallery.GalleryStatus(marquee, state, [1, 2]), "label") == "Applied", "applied");
            state["ports"]![0]!["override"] = O("{color:'#FFFFFF'}");
            e(S(EffectGallery.GalleryStatus(marquee, state, [1]), "label") == "Fixed color active" && Eq(EffectGallery.GalleryStatus(marquee, state, [1, 2]), "{label:'Applied to 1 of 2',selected:false}"), "fixed color");
            ((JsonObject)state["ports"]![0]!).Remove("override");
            state["ports"]![0]!["source"] = O("{kind:'rule',name:'Offline'}");
            e(Eq(EffectGallery.GalleryStatus(marquee, state, [1]), "{label:'Automation active',selected:false}") && S(EffectGallery.GalleryStatus(marquee, state, [1, 2]), "label") == "Applied to 1 of 2", "automation");
        });
        T("gallery: mixed configuration and remembered palettes never claim current output", e =>
        {
            var state = Scene();
            state["zones"]![0]!["ports"] = A("[1]");
            state["zones"]!.AsArray().Add(O("{ports:[2],effect:{id:'marquee',params:{color:'#FF0000'}}}"));
            e(Eq(EffectGallery.GalleryStatus(marquee, state, [1, 2]), "{label:'Mixed settings',selected:false}"), "mixed");
            var remembered = new Dictionary<string, JsonObject> { ["marquee"] = O("{id:'marquee',params:{color:'#00FF00'}}") };
            e(S(EffectGallery.GalleryConfig(marquee, state, [1], remembered), "params.color") == "#4DA6FF" && S(EffectGallery.GalleryConfig(marquee, state, [2], remembered), "params.color") == "#FF0000", "applied first");
            e(S(EffectGallery.GalleryConfig(marquee, O("{zones:[]}"), [1], remembered), "params.color") == "#00FF00", "remembered");
        });
        T("gallery: only an actual pending edit is labeled Previewing", e =>
        {
            var state = Scene();
            var draft = O("{effect:{id:'marquee',ports:[1,2],params:{color:'#00FF00'}}}");
            e(Eq(EffectGallery.GalleryStatus(marquee, state, [1, 2], draft), "{label:'Previewing',selected:true}") && S(EffectGallery.GalleryConfig(marquee, state, [1], new Dictionary<string, JsonObject>(), draft), "params.color") == "#00FF00", "previewing");
            draft["colors"] = O("{1:{color:'#0000FF'}}");
            e(S(EffectGallery.GalleryStatus(marquee, state, [1], draft), "label") == "Fixed color preview" && Js.IsFalse(EffectGallery.GalleryStatus(marquee, state, [])["selected"]), "fixed color preview");
            state["mode"] = "released";
            e(S(EffectGallery.GalleryStatus(marquee, state, [1]), "label") == "Saved effect", "released");
        });
        T("gallery: parameter-only drafts keep fixed-color ownership until the override is explicitly cleared", e =>
        {
            var state = Scene();
            state["overrides"] = O("{1:{color:'#FF0000'}}");
            var draft = O("{effect:{id:'marquee',ports:[1,2],params:{color:'#00FF00'},clearOverrides:false}}");
            e(Eq(EffectGallery.GalleryStatus(marquee, state, [1], draft), "{label:'Fixed color active',selected:false}") && Eq(EffectGallery.GalleryStatus(marquee, state, [1, 2], draft), "{label:'Previewing on 1 port',selected:true}"), "kept");
            draft["effect"]!["clearOverrides"] = true;
            e(Eq(EffectGallery.GalleryStatus(marquee, state, [1, 2], draft), "{label:'Previewing',selected:true}"), "cleared");
            draft["effect"]!["clearOverrides"] = false;
            draft["returnToEffect"] = A("[1]");
            e(Eq(EffectGallery.GalleryStatus(marquee, state, [1, 2], draft), "{label:'Previewing',selected:true}"), "returning");
            draft["colors"] = O("{1:{color:'#0000FF'}}");
            e(S(EffectGallery.GalleryStatus(marquee, state, [1], draft), "label") == "Fixed color preview", "new fixed color");
        });
        T("gallery: SFP status distinguishes custom output, rules, link control and drafts", e =>
        {
            var state = O("{mode:'running',num_ports:8,sfp:[{port:9,mode:'color',effect:'pulse'},{port:10,mode:'unmanaged',effect:'pulse'}]}");
            var pulse = O("{id:'pulse'}");
            e(S(EffectGallery.SfpGalleryStatus(pulse, state, [9, 10]), "label") == "Applied to 1 of 2", "partial");
            state["sfp"]![0]!["rule"] = O("{id:'alert'}");
            e(Eq(EffectGallery.SfpGalleryStatus(pulse, state, [9]), "{label:'Automation active',selected:false}") && S(EffectGallery.SfpGalleryStatus(pulse, state, [10]), "label") == "", "rule and link");
            e(S(EffectGallery.SfpGalleryStatus(pulse, state, [10], O("{sfp:{10:{mode:'color',effect:'pulse'}}}")), "label") == "Previewing", "draft");
            e(S(EffectGallery.SfpGalleryStatus(pulse, state, [9, 10], O("{sfp:{10:{mode:'off'}}}")), "label") == "Automation active", "off draft");
            ((JsonObject)state["sfp"]![0]!).Remove("rule");
            state["sfp"]![0]!["color"] = "#FF0000";
            state["sfp"]![1] = O("{port:10,mode:'color',effect:'pulse',color:'#0000FF'}");
            e(Eq(EffectGallery.SfpGalleryStatus(pulse, state, [9, 10]), "{label:'Mixed settings',selected:false}"), "mixed");
        });
        T("gallery: Together gallery drafts follow actual sorted SFP IDs instead of a fixed port number", e =>
        {
            foreach (var first in new[] { 17, 25 })
            {
                var state = O($"{{mode:'running',num_ports:{first - 1},sfp_sync:{{mode:'together'}},sfp:[{{port:{first + 1},mode:'unmanaged',effect:'steady'}},{{port:{first},mode:'color',effect:'pulse'}}]}}");
                var pulse = O("{id:'pulse'}");
                int[] both = [first, first + 1];
                JsonObject Draft(int port) => O($"{{sfp:{{{port}:{{period:4}}}}}}");
                e(S(EffectGallery.SfpGalleryStatus(pulse, state, both), "label") == "Applied", $"{first} applied");
                e(S(EffectGallery.SfpGalleryStatus(pulse, state, [first + 1], Draft(first)), "label") == "Previewing" && S(EffectGallery.SfpGalleryStatus(pulse, state, both, Draft(first)), "label") == "Previewing", $"{first} shared draft");
                e(S(EffectGallery.SfpGalleryStatus(pulse, state, both, Draft(first + 1)), "label") == "Applied", $"{first} dormant draft");
                e(S(EffectGallery.SfpGalleryStatus(pulse, state, [9], O("{sfp:{9:{mode:'color',effect:'pulse'}}}")), "label") == "", $"{first} unknown port");
            }
        });
        T("gallery: legacy SFP previews use each selected color brightness, SFP brightness and default brightness", e =>
        {
            var settings = O("{color:'#4DA6FF',secondary_color:'#E472B5',color_brightness:40,secondary_brightness:80,brightness:50,period:6}");
            double[] Legacy(string effect, double time, double master) => EffectGallery.SfpPreviewColor(settings, effect, time, master, 2.2, false);
            e(Near(Legacy("steady", 0, 51), EffectRenderer.Dim("#4DA6FF", .4 * .5 * .51)) && Near(Legacy("pulse", 0, 51), EffectRenderer.Dim("#4DA6FF", .4 * .5 * .51)), "primary");
            e(Near(Legacy("pulse", 3, 51), [0, 0, 0]) && Near(Legacy("two_color", 3, 51), EffectRenderer.Dim("#E472B5", .8 * .5 * .51)) && Near(Legacy("color_cycle", 0, 0), [0, 0, 0]), "fades");
        });
        T("gallery: slow SFP fade retains fractional samples and continuous cycle boundaries", e =>
        {
            var settings = O("{color:'#102040',color_brightness:20,period:30}");
            var samples = Enumerable.Range(0, 120).Select(index => EffectGallery.SfpPreviewColor(settings, "pulse", .4 + index / 120.0)).ToList();
            e(samples.Any(color => color.Any(channel => channel % 1 != 0)) && Enumerable.Range(1, 119).All(index => samples[index][2] < samples[index - 1][2]), "fractional and falling");
            foreach (var effect in new[] { "pulse", "two_color", "color_cycle" })
            {
                var before = EffectGallery.SfpPreviewColor(settings, effect, 29.99999);
                var after = EffectGallery.SfpPreviewColor(settings, effect, 30.00001);
                e(before.Select((channel, index) => Math.Abs(channel - after[index]) < .001).All(ok => ok), effect);
            }
        });
        var breathing = CatalogEffect("breathing");
        JsonObject DraftsScene() => O("{mode:'running',num_ports:8,brightness:100,zones:[{id:'all',ports:[1,2,3],effect:{id:'static',params:{color:'#FFFFFF'}}}],ports:[{port:1,source:{kind:'zone',id:'all'}},{port:2,source:{kind:'zone',id:'all'}},{port:3,source:{kind:'zone',id:'all'}}]}");
        T("gallery: gallery retains the separate effect and color for each draft target", e =>
        {
            var state = DraftsScene();
            var draft = O("{effects:[{id:'marquee',ports:[1],params:{color:'#4DA6FF',background:'#E472B5'}},{id:'breathing',ports:[2],params:{color:'#C08BFF'}}]}");
            var none = new Dictionary<string, JsonObject>();
            e(S(EffectGallery.GalleryConfig(marquee, state, [1], none, draft), "params.color") == "#4DA6FF" && S(EffectGallery.GalleryConfig(breathing, state, [2], none, draft), "params.color") == "#C08BFF", "configs");
            e(S(EffectGallery.GalleryStatus(marquee, state, [1], draft), "label") == "Previewing" && S(EffectGallery.GalleryStatus(breathing, state, [2], draft), "label") == "Previewing", "previewing");
            e(S(EffectGallery.GalleryStatus(marquee, state, [1, 2], draft), "label") == "Previewing on 1 port" && S(EffectGallery.GalleryStatus(marquee, state, [3], draft), "label") == "", "partial");
        });
        T("gallery: manual color and retained overrides take precedence over an effect in a multi-target draft", e =>
        {
            var state = DraftsScene();
            state["overrides"] = O("{1:{color:'#FFFFFF'}}");
            var draft = O("{effects:[{id:'marquee',ports:[1,2],params:{color:'#4DA6FF'},clearOverrides:false}],colors:{2:{color:'#FF0000'}}}");
            e(S(EffectGallery.GalleryStatus(marquee, state, [1], draft), "label") == "Fixed color active" && S(EffectGallery.GalleryStatus(marquee, state, [2], draft), "label") == "Fixed color preview", "kept");
            draft["effects"]![0]!["clearOverrides"] = true;
            e(S(EffectGallery.GalleryStatus(marquee, state, [1], draft), "label") == "Previewing" && S(EffectGallery.GalleryStatus(marquee, state, [2], draft), "label") == "Fixed color preview", "cleared");
        });
    }

    // ----- preview/draft-preview.mjs (tests/product-tools.test.mjs, tests/preview-effect-draft.test.mjs, tests/preview-schedule-clock.test.mjs) -----

    static JsonObject ToolsScene() => O("{version:3,topology:{version:1,copper_ports:[1,2,3,4],sfp_ports:[]},brightness:100,zones:[{id:'rack',name:'rack',ports:[1,2,3,4],effect:{id:'static',params:{color:'#ff0000'}}}],rules:[],overrides:{},sfp:{},sfp_sync:{mode:'independent',source_port:1}}");

    static JsonObject ToolsPort(int id) => O($"{{port:{id},facts:{{link:true,speed:10000,traffic:0}},link:{{link_up:true,speed_mbps:10000}},color:'#ff0000',display_rgb:[255,0,0],source:{{kind:'zone',id:'rack'}}}}");

    static JsonObject ToolsState()
    {
        var state = Js.Spread(ToolsScene(), J("{sfp:[],num_ports:4,connected:true,output:{gamma:1}}"));
        state["ports"] = Arr(ToolsPort(1), ToolsPort(2), ToolsPort(3), ToolsPort(4));
        return state;
    }

    static JsonObject Optical(int id, string extra = "{}") =>
        With(O($"{{port:{id},mode:'unmanaged',effect:'steady',color:'#ff0000',brightness:100,color_brightness:100,display_rgb:[255,0,0],facts:{{link:true}},link:true}}"), extra);

    static void DraftPreviewChecks()
    {
        EffectRenderer.SetEffectCatalog(EffectCatalog.Catalog);
        JsonObject StaticAt(string ports) => Js.Spread(EffectRenderer.DefaultConfig("static"), J($"{{ports:{ports},clearOverrides:true}}"));
        T("draft preview: splitting a spatial group recomputes colors on ports left behind", e =>
        {
            var current = ToolsState();
            current["zones"]![0]!["effect"] = O("{id:'gradient',params:{color_a:'#ff0000',color_b:'#0000ff',speed:0}}");
            var index = 0;
            foreach (var port in Js.Objects(current["ports"])) { port["display_rgb"] = Js.NumberArray([255 * (1 - index / 3.0), 0, 255 * index / 3.0]); index++; }
            var draft = EffectDraft.StageEffectDraft(new JsonObject(), StaticAt("[2]"), "selection");
            var before = new JsonArray(current.DeepClone(), draft.DeepClone());
            var colors = DraftPreview.PreviewDraftColors(current, draft, 0);
            e(colors.Keys.Order().SequenceEqual([1, 2, 3, 4]), "ports");
            e(Close(colors[3], [128, 0, 127]) && Close(colors[1], [255, 0, 0]) && Close(colors[4], [0, 0, 255]), "recomputed");
            e(Eq(new JsonArray(current.DeepClone(), draft.DeepClone()), before), "inputs unchanged");
        });
        T("draft preview: previewing split geometry preserves fixed color and first matching rule priority", e =>
        {
            var current = ToolsState();
            current["zones"]![0]!["effect"] = O("{id:'gradient',params:{color_a:'#ff0000',color_b:'#0000ff',speed:0}}");
            current["overrides"]!["1"] = O("{color:'#00ff00',brightness:60}");
            current["ports"]![0]!["override"] = O("{color:'#00ff00',brightness:60}");
            current["ports"]![0]!["display_rgb"] = A("[0,153,0]");
            current["rules"] = A("[{id:'rule',enabled:true,ports:[3],match:'all',conditions:[{field:'link',op:'is',value:true}],look:{style:'solid',color:'#ffffff',brightness:40}}]");
            var colors = DraftPreview.PreviewDraftColors(current, EffectDraft.StageEffectDraft(new JsonObject(), StaticAt("[2]"), "selection"), 0);
            e(Close(colors[1], [0, 153, 0]) && Close(colors[3], [102, 102, 102]));
        });
        T("draft preview: SFP Follow Ethernet previews master dimming despite dormant native Link status", e =>
        {
            var current = ToolsState();
            current["sfp"] = Arr(Optical(9), Optical(10));
            current["sfp_sync"] = O("{mode:'ethernet',source_port:1}");
            var colors = DraftPreview.PreviewDraftColors(current, O("{brightness:50}"), 0);
            e(Close(colors[1], [127.5, 0, 0]) && Close(colors[9], colors[1]) && Close(colors[10], colors[1]));
        });
        T("draft preview: grouped SFP previews brightness using the group source despite dormant native mode", e =>
        {
            var current = ToolsState();
            current["sfp"] = Arr(Optical(9));
            current["zones"]![0]!["ports"]!.AsArray().Add(9);
            e(Close(DraftPreview.PreviewDraftColors(current, O("{brightness:50}"), 0)[9], [127.5, 0, 0]));
        });
        T("draft preview: SFP rules inherit master brightness unless the rule explicitly pins its level", e =>
        {
            var current = ToolsState();
            current["sfp"] = Arr(Optical(9, "{mode:'color'}"));
            current["rules"] = A("[{id:'alert',enabled:true,ports:[9],match:'all',conditions:[{field:'link',op:'is',value:true}],look:{style:'solid',color:'#ff0000'}}]");
            e(Close(DraftPreview.PreviewDraftColors(current, O("{brightness:50}"), 0)[9], [127.5, 0, 0]), "inherited");
            current["rules"]![0]!["look"]!["brightness"] = 100;
            e(Close(DraftPreview.PreviewDraftColors(current, O("{brightness:50}"), 0)[9], [255, 0, 0]), "pinned");
        });
        T("draft preview: reapplying the same effect previews removal of an individual fixed color", e =>
        {
            var current = ToolsState();
            current["overrides"]!["1"] = O("{color:'#00ff00',brightness:100}");
            current["ports"]![0]!["override"] = O("{color:'#00ff00',brightness:100}");
            current["ports"]![0]!["display_rgb"] = A("[0,255,0]");
            var draft = new JsonObject { ["effect"] = Js.Spread(current.At("zones.0.effect"), J("{ports:[1,2,3,4],clearOverrides:true}")) };
            var colors = DraftPreview.PreviewDraftColors(current, draft, 0);
            e(colors.ContainsKey(1) && Close(colors[1], [255, 0, 0]));
        });
        T("draft preview: port map previews an optical member's own traffic and brightness independently of Follow Ethernet", e =>
        {
            var state = O("{brightness:100,ports:[{port:1,facts:{link:true,traffic:0}}],sfp:[{port:10,link:true,mode:'off',brightness:40,facts:{link:true,uplink:true,traffic:1000}}],sfp_sync:{mode:'ethernet',source_port:1},rules:[],zones:[],overrides:{}}");
            var draft = new JsonObject { ["effect"] = Cfg("activity_glow", "[1,10]", "{idle_color:'#000000',active_color:'#ffffff',include_uplinks:true}") };
            var clocks = new Dictionary<string, EffectClockPair>();
            var states = new Dictionary<int, EffectAnimationState>();
            DraftPreview.PreviewDraftColors(state, draft, 0, clocks, states);
            var shown = DraftPreview.PreviewDraftColors(state, draft, 5, clocks, states);
            e(shown[1].Max() == 0 && shown[10].All(channel => channel > 101 && channel <= 102), "own traffic");
            state["rules"] = A("[{id:'alert',enabled:true,ports:[10],conditions:[],look:{style:'solid',color:'#ff0000',brightness:50}}]");
            e(DraftPreview.PreviewDraftColors(state, draft, 6, clocks, states)[10].SequenceEqual([51, 0, 0]), "rule level");
        });
        T("draft preview: Lighting, profile and ownership previews use the same host schedule", e =>
        {
            var state = O("""
                {schedule_clock:{local_iso:'2026-10-31T23:30:00-05:00',zone_name:'Central Daylight Time'},brightness:100,num_ports:1,ports:[{port:1,facts:{},color:'#ffffff'}],
                 zones:[{id:'all',ports:[1],effect:{id:'static',params:{color:'#ffffff'}}}],
                 rules:[{id:'night',name:'Night',enabled:true,ports:[1],match:'all',conditions:[{field:'time',op:'between',value:['22:00','07:00']}],look:{style:'solid',color:'#ff0000',brightness:100}}],overrides:{},sfp:[]}
                """);
            ScheduleClock.Receive(state["schedule_clock"]);
            var draft = O("{effect:{id:'static',params:{color:'#00ffff'},ports:[1],clearOverrides:true}}");
            e(ScheduleClock.HostDate(state)?.Hour == 23, "host hour");
            e(S(LightingState.ControlSource(state, 1), "kind") == "rule", "ownership");
            e(DraftPreview.PreviewDraftColors(state, draft, 0)[1].SequenceEqual([255, 0, 0]), "lighting preview");
            e(ProfilePreview.ProfileColors(state, state, 0)[1].SequenceEqual([255, 0, 0]), "profile preview");
            var daylight = Js.Spread(state, J("{rules:[]}"));
            daylight["zones"] = new JsonArray(new JsonObject { ["id"] = "all", ["ports"] = A("[1]"), ["effect"] = EffectRenderer.DefaultConfig("daylight") });
            e(ProfilePreview.ProfileColors(daylight, state, 0)[1].Any(channel => channel > 0), "daylight uses the host hour");
        });
    }

    // ----- preview/profile-preview.mjs logic (tests/profile-preview.test.mjs, tests/live-traffic.test.mjs, tests/product-automation.test.mjs) -----

    static JsonObject ProfileScene(string effect = "static", string parameters = "{}")
    {
        var defaults = EffectRenderer.DefaultConfig(effect);
        var config = Js.Spread(defaults);
        config["params"] = Js.Spread(defaults["params"], J(parameters));
        var scene = O("{brightness:100,rules:[],overrides:{},sfp:{9:{mode:'color',effect:'two_color',color:'#0000ff',secondary_color:'#ff0000',period:6,brightness:100,color_brightness:100,secondary_brightness:100},10:{mode:'off'}},sfp_sync:{mode:'independent'}}");
        scene["zones"] = new JsonArray(new JsonObject { ["id"] = "all", ["ports"] = A("[1,2]"), ["effect"] = config });
        return scene;
    }

    static void ProfilePreviewChecks()
    {
        T("profile preview: saved traffic previews use actual telemetry rather than empty facts", e =>
        {
            var colors = ProfilePreview.ProfileColors(ProfileScene("traffic"), O("{ports:[{port:1,facts:{link:true,speed:1000,traffic:0}},{port:2,facts:{link:true,speed:1000,traffic:1000}}],sfp:[]}"), 1.2);
            e(!colors[1].SequenceEqual(colors[2]) && colors[2].Any(value => value > 0));
        });
        T("profile preview: profile preview includes rule priority, manual overrides, and independent brightness", e =>
        {
            var saved = ProfileScene("static", "{color:'#ffffff'}");
            saved["brightness"] = 20;
            saved["rules"] = A("[{enabled:true,ports:[1,2],match:'all',conditions:[{field:'link',op:'is',value:true}],look:{style:'solid',color:'#ff0000',brightness:50}}]");
            saved["overrides"] = O("{2:{color:'#0000ff',brightness:80}}");
            var colors = ProfilePreview.ProfileColors(saved, O("{ports:[{port:1,facts:{link:true}},{port:2,facts:{link:true}}]}"), 1);
            e(colors[1].SequenceEqual([127.5, 0, 0]) && colors[2].SequenceEqual([0, 0, 204]));
        });
        T("profile preview: saved SFP effects animate, share settings, and follow Ethernet colors", e =>
        {
            var saved = ProfileScene("static", "{color:'#00ff00'}");
            var a = ProfilePreview.ProfileColors(saved, new JsonObject(), 0);
            var b = ProfilePreview.ProfileColors(saved, new JsonObject(), 3);
            e(!a[9].SequenceEqual(b[9]) && a[10].SequenceEqual([0, 0, 0]), "independent");
            saved["sfp_sync"] = O("{mode:'together'}");
            e(ProfilePreview.ProfileColors(saved, new JsonObject(), 2)[9].SequenceEqual(ProfilePreview.ProfileColors(saved, new JsonObject(), 2)[10]), "together");
            saved["sfp_sync"] = O("{mode:'ethernet',source_port:1}");
            var follows = ProfilePreview.ProfileColors(saved, new JsonObject(), 1);
            e(follows[9].SequenceEqual(follows[1]) && follows[10].SequenceEqual(follows[1]), "following");
        });
        T("profile preview: saved preview includes ungrouped copper ports and reported SFP identifiers", e =>
        {
            var saved = ProfileScene();
            saved["zones"]![0]!["ports"] = A("[3,1,2]");
            saved["sfp"] = O("{17:{mode:'off'},18:{mode:'off'}}");
            e(Layout(ProfilePreview.ProfilePorts(saved, O("{num_ports:16}")), Enumerable.Range(1, 16).ToArray(), [17, 18]));
        });
        T("profile preview: backend profile color parity", e =>
        {
            foreach (var fixture in Js.Objects(Fixture("profile-colors.json")))
            {
                var colors = ColorsJson(ProfilePreview.ProfileColors(fixture["scene"], fixture["state"], 0));
                e(Eq(colors, fixture["colors"]), $"{S(fixture, "name")}{(Eq(colors, fixture["colors"]) ? "" : ": " + Js.Stringify(colors))}");
            }
        });
        T("profile preview: native SFP interpolation uses rule brightness even when the saved master is zero", e =>
        {
            var saved = ProfileScene();
            saved["brightness"] = 0;
            saved["sfp"]!["9"] = O("{mode:'color',effect:'pulse',color:'#0000ff',period:4,brightness:100,color_brightness:100}");
            saved["rules"] = A("[{enabled:true,ports:[9],conditions:[],look:{style:'effect',brightness:80}}]");
            var state = O("{switch:{sfp_native_gradient:true},output:{gamma:1},sfp:[{port:9,playback:{}}]}");
            e(ProfilePreview.ProfileColors(saved, state, 0)[9].SequenceEqual([0, 0, 204]) && ProfilePreview.ProfileColors(saved, state, 1)[9].SequenceEqual([0, 0, 102]));
        });
        T("profile preview: saved topology stays with the profile when a different switch is connected", e =>
        {
            var saved = Js.Spread(ProfileScene(), J("{version:3,topology:{version:1,model:'eight',copper_ports:[1,2,3,4,5,6,7,8],sfp_ports:[9,10]}}"));
            var state = new JsonObject { ["num_ports"] = 48, ["ports"] = Js.ArrayOf(Enumerable.Range(1, 48).Select(i => (JsonNode?)new JsonObject { ["port"] = i })), ["sfp"] = A("[{port:49},{port:50}]") };
            var ports = ProfilePreview.ProfilePorts(saved, state);
            e(Layout(ports, [1, 2, 3, 4, 5, 6, 7, 8], [9, 10]) && ports.Copper.Concat(ports.Sfp).Distinct().Count() == 10);
        });
        T("profile preview: legacy profile roles stay separate on a larger switch and explain the layout difference", e =>
        {
            var saved = ProfileScene();
            var state = O("{num_ports:24}");
            e(Layout(ProfilePreview.ProfilePorts(saved, state), [1, 2, 3, 4, 5, 6, 7, 8], [9, 10]), "roles");
            e(Regex.IsMatch(ProfilePreview.ProfileLayoutNotice(saved, state), "older profile.*different port layout", RegexOptions.IgnoreCase), "notice");
        });
        T("profile preview: optical-only saved profiles never invent copper ports or overwrite an optical color", e =>
        {
            var saved = Js.Spread(ProfileScene(), J("{version:3,zones:[],topology:{version:1,copper_ports:[],sfp_ports:[1,2]},sfp:{1:{mode:'color',effect:'steady',color:'#00ff00'},2:{mode:'off'}}}"));
            e(Layout(ProfilePreview.ProfilePorts(saved, new JsonObject()), [], [1, 2]), "ports");
            e(Eq(ColorsJson(ProfilePreview.ProfileColors(saved, new JsonObject(), 0)), "{1:[0,255,0],2:[0,0,0]}"), "colors");
            saved["topology"]!["sfp_ports"] = A("[1,1]");
            e(Layout(ProfilePreview.ProfilePorts(saved, new JsonObject()), [], []) && ProfilePreview.ProfileLayoutNotice(saved, new JsonObject()).Contains("Review"), "invalid topology");
        });
        T("profile preview: saved mixed effects retain socket position and individual brightness", e =>
        {
            var saved = ProfileScene("gradient", "{color_a:'#ff0000',color_b:'#0000ff',speed:0}");
            Assign(saved, "{version:3,topology:{version:1,copper_ports:[1,2],sfp_ports:[9,10]},brightness:80,sfp_sync:{mode:'together'}}");
            saved["sfp"]!["9"]!["brightness"] = 25;
            saved["sfp"]!["10"]!["brightness"] = 50;
            saved["zones"]![0]!["ports"] = A("[1,9,10]");
            var colors = ProfilePreview.ProfileColors(saved, new JsonObject(), 0);
            e(colors[1].SequenceEqual([204, 0, 0]) && colors[9].SequenceEqual([25.5, 0, 25.5]) && colors[10].SequenceEqual([0, 0, 102]) && colors[2].SequenceEqual([0, 0, 0]));
        });
        T("profile preview: saved optical-only Activity Glow uses individual optical traffic and targeted rules", e =>
        {
            var saved = ProfileScene("activity_glow", "{active_color:'#ffffff',idle_color:'#000000'}");
            Assign(saved, "{version:3,topology:{version:1,copper_ports:[],sfp_ports:[9,10]}}");
            saved["zones"]![0]!["ports"] = A("[9,10]");
            var state = O("{sfp:[{port:9,link:false,facts:{link:false,traffic:1000}},{port:10,link:true,facts:{link:true,speed:1000,traffic:40}}]}");
            var animation = new Dictionary<string, ProfileAnimation>();
            ProfilePreview.ProfileColors(saved, state, 0, animation);
            var colors = ProfilePreview.ProfileColors(saved, state, 1, animation);
            e(colors[9].SequenceEqual([0, 0, 0]) && colors[10][0] > 245, "traffic");
            saved["brightness"] = 0;
            saved["sfp"]!["10"]!["brightness"] = 50;
            saved["rules"] = A("[{enabled:true,ports:[10],conditions:[{field:'link',op:'is',value:true}],look:{style:'solid',color:'#ff0000',brightness:80}}]");
            e(ProfilePreview.ProfileColors(saved, state, 2, animation)[10].SequenceEqual([102, 0, 0]), "rule");
        });
        T("profile preview: saved Activity Glow scales copper and SFP together when uplink traffic is included", e =>
        {
            var saved = ProfileScene("activity_glow", "{active_color:'#ffffff',idle_color:'#000000'}");
            Assign(saved, "{version:3,topology:{version:1,copper_ports:[1],sfp_ports:[9,10]}}");
            saved["zones"]![0]!["ports"] = A("[1,10]");
            var state = O("{ports:[{port:1,facts:{link:true,uplink:false,traffic:10}}],sfp:[{port:10,link:true,facts:{link:true,uplink:true,traffic:100}}]}");
            var frames = new Dictionary<string, ProfileAnimation>();
            ProfilePreview.ProfileColors(saved, state, 0, frames);
            var excluded = ProfilePreview.ProfileColors(saved, state, 2, frames);
            e(excluded[1][0] > 250 && excluded[10].SequenceEqual([0, 0, 0]), "uplink excluded");
            saved["zones"]![0]!["effect"]!["params"]!["include_uplinks"] = true;
            ProfilePreview.ProfileColors(saved, state, 3, frames);
            var included = ProfilePreview.ProfileColors(saved, state, 5, frames);
            e(included[1][0] > 24 && included[1][0] < 26 && included[10][0] > 250, "uplink included");
        });
        T("profile preview: v3 profile layout warning compares the exact current copper and SFP IDs", e =>
        {
            var scene = new JsonObject { ["version"] = 3, ["topology"] = new JsonObject { ["version"] = 1, ["model"] = null, ["copper_ports"] = Js.NumberArray(Enumerable.Range(1, 24)), ["sfp_ports"] = A("[25,26]") } };
            e(ProfilePreview.ProfileLayoutNotice(scene, O("{num_ports:8,sfp_ports:[9,10]}")).Contains("24 Ethernet and 2 SFP"), "different");
            e(ProfilePreview.ProfileLayoutNotice(scene, O("{num_ports:24,sfp_ports:[25,26]}")) == "", "same");
            scene["topology"]!["sfp_ports"] = A("[27,28]");
            e(ProfilePreview.ProfileLayoutNotice(scene, O("{num_ports:24,sfp_ports:[25,26]}")) != "", "SFP IDs");
        });
        T("profile preview: profile model and legacy layout notices follow backend topology rules", e =>
        {
            var state = O("{num_ports:8,sfp_ports:[9,10],hardware_profile:{id:'current'}}");
            var scene = O("{version:3,topology:{version:1,model:'saved',copper_ports:[1,2,3,4,5,6,7,8],sfp_ports:[9,10]}}");
            e(ProfilePreview.ProfileLayoutNotice(scene, state).Contains("different switch model"), "model");
            scene["topology"]!["model"] = null;
            e(ProfilePreview.ProfileLayoutNotice(scene, state) == "" && ProfilePreview.ProfileLayoutNotice(O("{version:2,zones:[],sfp:{}}"), state) == "", "no model");
            e(ProfilePreview.ProfileLayoutNotice(O("{version:2,zones:[],sfp:{}}"), O("{num_ports:24,sfp_ports:[25,26]}")).Contains("older profile")
              && ProfilePreview.ProfileLayoutNotice(O("{version:2,zones:[]}"), O("{num_ports:8,sfp_ports:[]}")).Contains("older profile"), "version 2");
            e(ProfilePreview.ProfileLayoutNotice(O("{version:1,effect:'static',params:{}}"), O("{num_ports:24,sfp_ports:[25,26]}")) == ""
              && ProfilePreview.ProfileLayoutNotice(O("{version:1,effect:'static',rules:[{}]}"), O("{num_ports:24,sfp_ports:[25,26]}")).Contains("older profile"), "version 1");
        });
    }

    // ----- preview/weekly-schedules.mjs, preview/switch-health.mjs, preview/last-session.mjs -----

    static void SettingsChecks()
    {
        T("schedules: schedule editor warns only for enabled same-time overlapping weekdays on the connected switch", e =>
        {
            var draft = O("{id:'one',name:'Evening',enabled:true,at:'18:00',days:['wed'],action:{type:'brightness',value:23}}");
            var rows = Arr(With(draft, "{id:'two'}"), With(draft, "{id:'three',days:['thu']}"), With(draft, "{id:'four',enabled:false}"), With(draft, "{id:'five',needs_device_confirmation:true}"));
            e(WeeklySchedules.ScheduleConflicts(draft, rows).Select(row => S(row, "id")).SequenceEqual(["two"]), "conflicts");
            e(WeeklySchedules.ScheduleDraftError(draft) == "" && WeeklySchedules.ScheduleDraftError(With(draft, "{days:[]}")).Contains("day")
              && WeeklySchedules.ScheduleDraftError(With(draft, "{action:{type:'preset'}}")).Contains("profile"), "validation");
        });
        Dictionary<string, string> Rows(JsonObject health)
        {
            var rows = new Dictionary<string, string>();
            foreach (var row in Js.Items(health["rows"])) rows[S(row, "0")!] = S(row, "1")!;
            return rows;
        }
        T("switch health: switch health keeps load average separate from sampled CPU usage", e =>
        {
            var rows = Rows(SwitchHealthModel.SwitchHealth(O("{connected:true,stats:{commands_per_second:28,command_budget:30,ack_p95_ms:2.25,switch_cpu_idle:.82,switch_load:.97,cpu_sample_age_ms:1250}}")));
            e(rows["CPU usage · last sample"] == "18%" && rows["Load average"] == "0.97" && rows["LED commands"] == "28 / 30 per second"
              && rows["CPU sample age"] == "1.3 s" && rows["Acknowledgment · 95th percentile"] == "2.25 ms", string.Join("; ", rows.Select(pair => pair.Key + "=" + pair.Value)));
        });
        T("switch health: missing or invalid measurements are not reported as zero", e =>
        {
            foreach (var invalid in new[] { "undefined", "null", "-1", "NaN", "Infinity", "'0'" })
            {
                var rows = Rows(SwitchHealthModel.SwitchHealth(O($"{{stats:{{switch_cpu_idle:{invalid},switch_load:{invalid},ack_p95_ms:{invalid}}}}}")));
                e(rows["CPU usage · last sample"] == "—" && rows["Load average"] == "—" && rows["Acknowledgment · 95th percentile"] == "—", invalid);
            }
            e(Rows(SwitchHealthModel.SwitchHealth(O("{stats:{switch_cpu_idle:1,switch_load:0,cpu_sample_age_ms:0}}")))["CPU usage · last sample"] == "0%", "idle");
        });
        T("switch health: a failed CPU sample cannot present retained readings as current", e =>
        {
            var health = SwitchHealthModel.SwitchHealth(O("{connected:true,stats:{switch_cpu_idle:.8,switch_load:.97,cpu_sample_age_ms:null}}"));
            e(Rows(health)["CPU usage · last sample"] == "—" && Rows(health)["Load average"] == "—" && S(health, "status") == "Switch readings");
        });
        T("switch health: disconnected and terminal reconnect states never claim live health", e =>
        {
            e(S(SwitchHealthModel.SwitchHealth(O("{connected:true}"), true), "status")!.Contains("disconnected"), "unavailable");
            e(S(SwitchHealthModel.SwitchHealth(O("{connected:false}")), "status")!.Contains("Last reported"), "disconnected");
            e(S(SwitchHealthModel.SwitchHealth(O("{connected:false,reconnect_required:true}")), "status")!.Contains("Reconnect"), "reconnect");
        });
        T("switch health: resource rows show reported sensor names, memory and writable storage with units", e =>
        {
            var state = O("{connected:true,stats:{health_sample_age_ms:2400,switch_health:{temperatures:[{label:'Board',celsius:44.125},{label:'CPU',celsius:52.25}],storage:[{mount:'/persistent'}]}}}");
            state.At("stats.switch_health")!["memory"] = new JsonObject { ["used_bytes"] = 256.0 * 1024 * 1024, ["total_bytes"] = 1024.0 * 1024 * 1024 };
            Js.Assign((JsonObject)state.At("stats.switch_health.storage.0")!, new JsonObject { ["used_bytes"] = 128.0 * 1024 * 1024, ["total_bytes"] = 512.0 * 1024 * 1024 });
            var rows = Rows(SwitchHealthModel.SwitchHealth(state));
            e(rows["Temperature · Board"] == "44.1 °C" && rows["Temperature · CPU"] == "52.3 °C", "temperatures " + rows["Temperature · CPU"]);
            e(rows["Memory used"] == "256 MiB / 1 GiB · 25%" && rows["Storage · /persistent"] == "128 MiB / 512 MiB · 25%" && rows["Resource sample age"] == "2.4 s", "resources");
        });
        T("switch health: missing sensors and invalid resource sizes are not invented or shown as zero", e =>
        {
            var rows = Rows(SwitchHealthModel.SwitchHealth(O("{stats:{switch_health:{temperatures:[],memory:{total_bytes:10,used_bytes:20},storage:[]}}}")));
            e(rows["Temperature"] == "Not reported" && rows["Memory used"] == "Not reported" && rows["Storage used"] == "Not reported", "not reported");
            e(!rows.ContainsKey("Temperature · CPU") && !rows.ContainsKey("Temperature · Drive"), "no invented sensors");
            e(Regex.IsMatch(S(SwitchHealthModel.SwitchHealth(O("{connected:true,stats:{health_error:'unavailable'}}")), "status")!, "Last|last"), "health error");
        });
        T("last session: last session displays persisted cleanup outcome and preserves the need for review", e =>
        {
            var result = LastSession.LastSessionView(O("{available:true,last_session:{time:'2026-11-04T13:30:00Z',stage:'parent_cleanup',outcome:'cleanup_unconfirmed',build:'0.4.19',attempts:4}}"));
            string? Row(string label) => Js.Items(result["rows"]).Where(row => S(row, "0") == label).Select(row => S(row, "1")).FirstOrDefault();
            e(Row("Result") == "Lighting cleanup needs review" && Row("Cleanup attempts") == "4" && S(result, "message")!.Contains("connection status"));
        });
        T("last session: missing and unavailable session evidence are distinct from a confirmed shutdown", e =>
        {
            e(S(LastSession.LastSessionView(O("{available:true,last_session:null}")), "message")!.Contains("No earlier session"), "missing");
            e(S(LastSession.LastSessionView(O("{available:false,last_session:null}")), "message")!.Contains("could not be read"), "unavailable");
            e(S(LastSession.LastSessionView(O("{available:true,last_session:{time:'2026-11-04T13:30:00Z',stage:'shutdown',outcome:'closed',build:'0.4.19'}}")), "message") == "", "closed");
        });
    }

    // ----- preview/scene-review.mjs logic (tests/product-tools.test.mjs, tests/scene-review-partitions.test.mjs) -----

    static JsonObject Group(string id, string ports, string effect = "{id:'static',params:{color:'#ff0000',brightness_color:100}}") =>
        O($"{{id:'{id}',name:'{id}',ports:{ports},effect:{effect}}}");

    static void SceneReviewChecks()
    {
        JsonObject Zone(string id, string ports) => O($"{{id:'{id}',name:'{id}',ports:{ports},effect:{{id:'static',params:{{color:'#ff0000'}}}}}}");
        T("scene review: scene review reapplies brightness while preserving another client group rename", e =>
        {
            var baseScene = ToolsScene();
            var current = Js.Clone(baseScene);
            var draft = Js.Clone(baseScene);
            current["zones"]![0]!["name"] = "Cameras";
            draft["brightness"] = 81;
            var inputs = new JsonArray(baseScene.DeepClone(), current.DeepClone(), draft.DeepClone());
            var result = SceneReview.CompareScenes(baseScene, current, draft);
            e(Js.Length(result["conflicts"]) == 0 && N(result, "scene.brightness") == 81 && S(result, "scene.zones.0.name") == "Cameras" && Js.IsTrue(result["changed"]), "merged");
            e(Eq(new JsonArray(baseScene.DeepClone(), current.DeepClone(), draft.DeepClone()), inputs), "inputs unchanged");
        });
        T("scene review: scene review exposes overlapping changes and obeys the explicit selected winner", e =>
        {
            var baseScene = ToolsScene();
            var current = With(baseScene, "{brightness:60}");
            var draft = With(baseScene, "{brightness:25}");
            var keep = SceneReview.CompareScenes(baseScene, current, draft);
            var replace = SceneReview.CompareScenes(baseScene, current, draft, true);
            e(Js.Objects(keep["conflicts"]).Select(conflict => S(conflict, "path")).SequenceEqual(["brightness"]) && N(keep, "scene.brightness") == 60 && Js.IsFalse(keep["changed"]), "keep");
            e(N(replace, "scene.brightness") == 25, "replace");
        });
        T("scene review: scene review preserves independent additions and detects deletion against editing", e =>
        {
            var baseScene = ToolsScene();
            baseScene["zones"]!.AsArray().Add(Zone("spare", "[]"));
            var current = Js.Clone(baseScene);
            var draft = Js.Clone(baseScene);
            current["zones"]![1]!["name"] = "Renamed";
            draft["zones"]!.AsArray().RemoveAt(1);
            current["overrides"]!["1"] = O("{color:'#00ff00'}");
            draft["overrides"]!["2"] = O("{color:'#0000ff'}");
            var kept = SceneReview.CompareScenes(baseScene, current, draft);
            var removed = SceneReview.CompareScenes(baseScene, current, draft, true);
            e(Js.Objects(kept["conflicts"]).Select(conflict => S(conflict, "path")).SequenceEqual(["zones.spare"]) && Js.Length(kept.At("scene.zones")) == 2, "kept");
            e(Js.Length(removed.At("scene.zones")) == 1 && Eq(removed.At("scene.overrides"), "{1:{color:'#00ff00'},2:{color:'#0000ff'}}"), "removed");
        });
        T("scene review: scene review treats rule order as one decision instead of combining competing priorities", e =>
        {
            var baseScene = ToolsScene();
            baseScene["rules"] = A("[{id:'first'},{id:'second'}]");
            var current = Js.Clone(baseScene);
            var draft = Js.Clone(baseScene);
            current["rules"] = A("[{id:'second'},{id:'first'}]");
            draft["rules"]![0]!["name"] = "Changed";
            var result = SceneReview.CompareScenes(baseScene, current, draft);
            e(S(result, "conflicts.0.path") == "rules" && Eq(result.At("scene.rules"), current["rules"]));
        });
        T("scene review: scene snapshot excludes transient telemetry from a draft baseline", e =>
        {
            var current = ToolsState();
            current["ports"]![0]!["controller"] = O("{clients:[{name:'Device'}]}");
            current["telemetry"] = O("{sequence:1}");
            current["identify"] = O("{port:1}");
            var snapshot = SceneReview.SceneFromState(current);
            e(!snapshot.ContainsKey("telemetry") && !snapshot.ContainsKey("identify") && Eq(snapshot["zones"], current["zones"]) && Eq(snapshot["topology"], current["topology"]));
        });
        T("scene review: keeping a concurrently edited group after deletion also preserves valid port membership", e =>
        {
            var baseScene = ToolsScene();
            baseScene["zones"] = Arr(Zone("rack", "[1,2]"), Zone("spare", "[3,4]"));
            var current = Js.Clone(baseScene);
            var draft = Js.Clone(baseScene);
            current["zones"]![1]!["name"] = "Renamed";
            draft["zones"] = Arr(Zone("rack", "[1,2,3,4]"));
            draft["brightness"] = 81;
            var kept = SceneReview.CompareScenes(baseScene, current, draft);
            var replaced = SceneReview.CompareScenes(baseScene, current, draft, true);
            var ports = Js.Objects(kept.At("scene.zones")).SelectMany(zone => Js.Items(zone["ports"]).Select(Js.ToNumber)).ToList();
            e(Js.Length(kept["conflicts"]) > 0 && ports.Distinct().Count() == ports.Count, "valid membership");
            e(Eq(kept.At("scene.zones"), current["zones"]) && N(kept, "scene.brightness") == 81 && Eq(replaced.At("scene.zones"), draft["zones"]), "chosen groups");
        });
        JsonObject Base() => new JsonObject
        {
            ["version"] = 3,
            ["topology"] = O("{version:1,copper_ports:[1,2,3,4],sfp_ports:[]}"),
            ["brightness"] = 100,
            ["zones"] = Arr(Group("all", "[1,2,3,4]")),
            ["rules"] = new JsonArray(),
            ["overrides"] = new JsonObject(),
            ["sfp"] = new JsonObject(),
            ["sfp_sync"] = O("{mode:'independent',source_port:1}"),
        };
        bool Partition(JsonNode? scene)
        {
            var ports = Js.Objects(Get(scene, "zones")).SelectMany(zone => Js.Items(zone["ports"]).Select(Js.ToNumber)).ToList();
            return ports.Distinct().Count() == ports.Count && ports.Order().SequenceEqual([1, 2, 3, 4]);
        }
        foreach (var preferDraft in new[] { false, true })
        {
            T($"scene review: concurrent splits preserve one valid group partition with preferDraft={preferDraft}", e =>
            {
                var baseScene = Base();
                var current = Js.Clone(baseScene);
                var draft = Js.Clone(baseScene);
                current["zones"] = Arr(Group("all", "[2,3,4]"), Group("client-a", "[1]"));
                draft["zones"] = Arr(Group("all", "[1,3,4]"), Group("client-b", "[2]"));
                draft["brightness"] = 42;
                var inputs = new JsonArray(baseScene.DeepClone(), current.DeepClone(), draft.DeepClone());
                var result = SceneReview.CompareScenes(baseScene, current, draft, preferDraft);
                e(Partition(result["scene"]) && Js.Length(result["conflicts"]) > 0 && Eq(result.At("scene.zones"), preferDraft ? draft["zones"] : current["zones"]), "groups");
                e(N(result, "scene.brightness") == 42 && Eq(new JsonArray(baseScene.DeepClone(), current.DeepClone(), draft.DeepClone()), inputs), "brightness and inputs");
            });
            T($"scene review: consolidation against renamed deleted group keeps a valid partition with preferDraft={preferDraft}", e =>
            {
                var baseScene = Base();
                baseScene["zones"] = Arr(Group("left", "[1,2]"), Group("right", "[3,4]"));
                var current = Js.Clone(baseScene);
                var draft = Js.Clone(baseScene);
                current["zones"]![1]!["name"] = "Camera ports";
                draft["zones"] = Arr(Group("left", "[1,2,3,4]"));
                draft["brightness"] = 61;
                var result = SceneReview.CompareScenes(baseScene, current, draft, preferDraft);
                e(Partition(result["scene"]) && Js.Length(result["conflicts"]) > 0 && Eq(result.At("scene.zones"), preferDraft ? draft["zones"] : current["zones"]) && N(result, "scene.brightness") == 61);
            });
            T($"scene review: renamed group against concurrent consolidation keeps a valid partition with preferDraft={preferDraft}", e =>
            {
                var baseScene = Base();
                baseScene["zones"] = Arr(Group("left", "[1,2]"), Group("right", "[3,4]"));
                var current = Js.Clone(baseScene);
                var draft = Js.Clone(baseScene);
                current["zones"] = Arr(Group("left", "[1,2,3,4]"));
                draft["zones"]![1]!["name"] = "Camera ports";
                current["brightness"] = 57;
                var result = SceneReview.CompareScenes(baseScene, current, draft, preferDraft);
                e(Partition(result["scene"]) && Js.Length(result["conflicts"]) > 0 && Eq(result.At("scene.zones"), preferDraft ? draft["zones"] : current["zones"]) && N(result, "scene.brightness") == 57);
            });
            T($"scene review: changing effect type cannot combine parameters from different effects with preferDraft={preferDraft}", e =>
            {
                var baseScene = Base();
                var current = Js.Clone(baseScene);
                var draft = Js.Clone(baseScene);
                current["zones"]![0]!["effect"] = O("{id:'rainbow',params:{speed:1}}");
                draft["zones"]![0]!["effect"]!["params"]!["color"] = "#0000ff";
                draft["brightness"] = 39;
                var result = SceneReview.CompareScenes(baseScene, current, draft, preferDraft);
                e(Js.Length(result["conflicts"]) > 0 && Eq(result.At("scene.zones.0.effect"), (preferDraft ? draft : current).At("zones.0.effect")) && N(result, "scene.brightness") == 39 && Partition(result["scene"]));
            });
            T($"scene review: draft effect type and remote parameter edit remain one choice with preferDraft={preferDraft}", e =>
            {
                var baseScene = Base();
                var current = Js.Clone(baseScene);
                var draft = Js.Clone(baseScene);
                current["zones"]![0]!["effect"]!["params"]!["color"] = "#00ff00";
                draft["zones"]![0]!["effect"] = O("{id:'rainbow',params:{speed:1}}");
                var result = SceneReview.CompareScenes(baseScene, current, draft, preferDraft);
                e(Js.Length(result["conflicts"]) > 0 && Eq(result.At("scene.zones.0.effect"), (preferDraft ? draft : current).At("zones.0.effect")));
            });
        }
        T("scene review: same-effect disjoint parameter edits and a rename still merge without a conflict", e =>
        {
            var baseScene = Base();
            var current = Js.Clone(baseScene);
            var draft = Js.Clone(baseScene);
            current["zones"]![0]!["name"] = "Rack";
            current["zones"]![0]!["effect"]!["params"]!["color"] = "#00ff00";
            draft["zones"]![0]!["effect"]!["params"]!["brightness_color"] = 33;
            var result = SceneReview.CompareScenes(baseScene, current, draft);
            e(Js.Length(result["conflicts"]) == 0 && S(result, "scene.zones.0.name") == "Rack" && Eq(result.At("scene.zones.0.effect"), "{id:'static',params:{color:'#00ff00',brightness_color:33}}") && Partition(result["scene"]));
        });
        T("scene review: valid special group IDs remain data during a recursive merge", e =>
        {
            var baseScene = Base();
            baseScene["zones"]![0]!["id"] = "__proto__";
            var current = Js.Clone(baseScene);
            var draft = Js.Clone(baseScene);
            current["zones"]![0]!["name"] = "Renamed rack";
            draft["zones"]![0]!["effect"]!["params"]!["color"] = "#0000ff";
            var result = SceneReview.CompareScenes(baseScene, current, draft);
            e(Js.Length(result["conflicts"]) == 0 && Js.Length(result.At("scene.zones")) == 1 && S(result, "scene.zones.0.id") == "__proto__"
              && S(result, "scene.zones.0.name") == "Renamed rack" && S(result, "scene.zones.0.effect.params.color") == "#0000ff" && Partition(result["scene"]));
        });
        T("scene review: the review dialog describes both sides and offers the preview or the current settings", e =>
        {
            var baseScene = ToolsScene();
            var current = With(baseScene, "{brightness:60}");
            var draft = With(baseScene, "{brightness:25}");
            var content = SceneReview.ReviewContent(baseScene, current, draft);
            e(content.Message == "Both clients changed these settings. Choose which values to keep.", "message");
            e(content.Changes.SequenceEqual(["Default brightness: current 60; preview 25"]) && content.RemoteChanges.SequenceEqual(["Default brightness: previous 100; now 60"]), "descriptions");
            e(content.Actions.Select(action => action.Label).SequenceEqual(["Cancel", "Keep current overlapping settings", "Use my preview settings"])
              && content.Actions[0].Scene is null && N(content.Actions[1].Scene, "brightness") == 60 && N(content.Actions[2].Scene, "brightness") == 25, "actions");
        });
    }

    // ----- preview/schedule-clock.mjs as the rules read it (tests/preview-schedule-clock.test.mjs) -----

    static void ScheduleClockChecks()
    {
        JsonObject Clock(string iso, string zone = "EE host zone") => new() { ["schedule_clock"] = new JsonObject { ["local_iso"] = iso, ["zone_name"] = zone } };
        int[] Parts(DateTime? date) => date is { } value ? [(int)value.DayOfWeek, value.Hour, value.Minute, value.Second, value.Millisecond] : [];
        T("schedule clock: host civil time ignores browser offset and advances only by monotonic elapsed time", e =>
        {
            var current = Clock("2026-10-31T23:59:58.250-05:00");
            ScheduleClock.Receive(current["schedule_clock"], 1000);
            e(Parts(ScheduleClock.HostDate(current, 1000)).SequenceEqual([6, 23, 59, 58, 250]) && Parts(ScheduleClock.HostDate(current, 3250)).SequenceEqual([0, 0, 0, 0, 500])
              && Parts(ScheduleClock.HostDate(current, 500)).SequenceEqual([6, 23, 59, 58, 250]));
        });
        T("schedule clock: receiving the same clock object again does not reset its anchor", e =>
        {
            var current = Clock("2026-10-31T10:00:00+05:30");
            ScheduleClock.Receive(current["schedule_clock"], 0);
            ScheduleClock.Receive(current["schedule_clock"], 5000);
            e(ScheduleClock.HostDate(current, 5500)?.Second == 5 && ScheduleClock.HostDate(current, 5500)?.Millisecond == 500);
        });
        T("schedule clock: fresh host samples handle daylight-saving fallback and spring-forward without guessing zone rules", e =>
        {
            var fallBefore = Clock("2026-11-01T01:59:59-04:00", "Eastern Daylight Time");
            ScheduleClock.Receive(fallBefore["schedule_clock"], 0);
            var fallAfter = Clock("2026-11-01T01:00:00-05:00", "Eastern Standard Time");
            ScheduleClock.Receive(fallAfter["schedule_clock"], 1000);
            e(Parts(ScheduleClock.HostDate(fallBefore, 0)).Take(4).SequenceEqual([0, 1, 59, 59]) && Parts(ScheduleClock.HostDate(fallAfter, 1500)).Take(4).SequenceEqual([0, 1, 0, 0]), "fall back");
            e(ScheduleClock.Label(fallAfter) == "Eastern Standard Time", "label");
            var spring = Clock("2026-03-08T03:00:00-04:00");
            ScheduleClock.Receive(spring["schedule_clock"], 0);
            e(Parts(ScheduleClock.HostDate(spring, 500)).Take(4).SequenceEqual([0, 3, 0, 0]), "spring forward");
        });
        T("schedule clock: weekday and overnight conditions cross host midnight, not the browser date", e =>
        {
            var current = Clock("2026-10-31T23:59:59-05:00");
            ScheduleClock.Receive(current["schedule_clock"], 0);
            var before = ScheduleClock.HostDate(current, 0);
            var after = ScheduleClock.HostDate(current, 1500);
            e(Rules.ConditionMatches(O("{field:'weekday',op:'in',value:['sat']}"), new JsonObject(), before) && Rules.ConditionMatches(O("{field:'weekday',op:'in',value:['sun']}"), new JsonObject(), after), "weekday");
            var overnight = O("{field:'time',op:'between',value:['22:00','07:00']}");
            e(Rules.ConditionMatches(overnight, new JsonObject(), before) && Rules.ConditionMatches(overnight, new JsonObject(), after), "overnight");
        });
        T("schedule clock: missing, offset-free and malformed clocks never claim browser-local schedule time", e =>
        {
            foreach (var current in new[] { new JsonObject(), Clock("2026-10-31T12:00:00"), Clock("2026-02-30T12:00:00Z"), Clock("invalid") })
                e(ScheduleClock.HostDate(current) is null && ScheduleClock.Label(current) == "EE computer"
                  && !Rules.ConditionMatches(O("{field:'time',op:'between',value:['00:00','23:59']}"), new JsonObject(), ScheduleClock.HostDate(current)), Js.Stringify(current));
        });
    }

    // ----- preview/port-animation.mjs logic (preview/port-animation.test.mjs) -----

    static void PortAnimationChecks()
    {
        IEnumerable<KeyValuePair<string, object?>> Colors(params (string Port, object? Color)[] colors) => colors.Select(pair => new KeyValuePair<string, object?>(pair.Port, pair.Color));
        T("port animation: uneven network arrivals render an even fade between confirmed samples", e =>
        {
            var frames = new ColorFrames(120);
            foreach (var (time, value) in new[] { (0, 0), (40, 20), (104, 52), (120, 60), (190, 95), (240, 120) })
            {
                var color = "#" + string.Concat(Enumerable.Repeat(value.ToString("x2", CultureInfo.InvariantCulture), 3));
                frames.Push(Colors(("1", color), ("8", color)), time);
            }
            var samples = Enumerable.Range(0, 29).Select(i => frames.Sample(120 + i * 1000 / 120.0)).ToList();
            var steps = Enumerable.Range(1, samples.Count - 1).Select(i => samples[i]["1"][0] - samples[i - 1]["1"][0]).ToList();
            e(steps.All(step => step > 3.9 && step < 4.4), "even steps");
            e(samples.Any(sample => sample["1"][0] % 1 != 0), "intermediate colors must retain fractional intensity");
            e(samples.All(sample => sample["1"].SequenceEqual(sample["8"])), "all ports share the same playback clock");
        });
        T("port animation: a disconnected stream holds its last confirmed color without extrapolation", e =>
        {
            var frames = new ColorFrames();
            frames.Push(Colors(("1", "#000000")), 0);
            frames.Push(Colors(("1", "#804020")), 40);
            e(frames.Sample(10000)["1"].SequenceEqual([128, 64, 32]), "held");
            frames.Push(Colors(("1", "#ffffff")), 20);
            e(frames.Sample(10000)["1"].SequenceEqual([128, 64, 32]), "older frame ignored");
        });
        T("port animation: long sessions keep a bounded interpolation history", e =>
        {
            var frames = new ColorFrames();
            for (var time = 0; time < 60000; time += 40) frames.Push(Colors(("1", "#101010")), time);
            e(frames.Frames.Count <= 33 && frames.Sample(60120)["1"].SequenceEqual([16, 16, 16]));
        });
        foreach (var fps in new[] { 30, 60, 120 })
            foreach (var intervals in new[] { new[] { 40.0 }, [200], [1000 / 3.75], [40, 80, 240, 60, 180, 50] })
            {
                T($"port animation: causal {string.Join("/", intervals.Select(Js.NumberToString))}ms arrivals fade evenly at {fps}Hz", e =>
                {
                    var frames = new ColorFrames();
                    var step = 1000.0 / fps;
                    const double rate = .04;
                    double next = 0;
                    var arrival = 0;
                    double previous = 0;
                    var ok = true;
                    for (var i = 0; i < fps * 5; i++)
                    {
                        var now = i * step;
                        while (next <= now + 1e-6)
                        {
                            var channel = next * rate;
                            frames.Push(new Dictionary<int, double[]> { [1] = [channel, channel, channel], [8] = [channel, channel, channel] }, next);
                            next += intervals[arrival++ % intervals.Length];
                        }
                        var sample = frames.Sample(now);
                        ok &= sample["1"].SequenceEqual(sample["8"]) && sample["1"][0] <= frames.Latest()["1"][0];
                        if (now >= 2000)
                        {
                            var expected = step * rate;
                            ok &= sample["1"][0] - previous >= expected * .98 && sample["1"][0] - previous <= expected * 1.02;
                        }
                        previous = sample["1"][0];
                    }
                    e(ok);
                });
            }
        T("port animation: a late sample changes playback pace without rewinding or jumping ahead", e =>
        {
            var frames = new ColorFrames();
            var step = 1000.0 / 120;
            double? previous = null;
            var ok = true;
            for (var now = 0.0; now < 1600; now += step)
            {
                if (now < 500 || now > 820) frames.Push(new Dictionary<int, double[]> { [1] = [now / 10, 0, 0] }, now);
                var value = frames.Sample(now)["1"][0];
                if (previous is { } last) ok &= value >= last - 1e-9 && value - last <= step / 10 * 1.101;
                previous = value;
            }
            e(ok && frames.Delay <= 500);
        });
        T("port animation: cadence evidence expires after interruption and stale output holds exactly", e =>
        {
            var frames = new ColorFrames();
            for (var time = 0; time <= 1600; time += 400) frames.Push(new Dictionary<int, double[]> { [1] = [time / 10.0, 0, 0] }, time);
            e(frames.Delay == 450 && frames.Sample(5000)["1"].SequenceEqual([160, 0, 0]) && frames.Sample(6000)["1"].SequenceEqual([160, 0, 0]), "held");
            frames.Push(new Dictionary<int, double[]> { [1] = [0, 255, 0] }, 10000);
            e(frames.Delay == 120 && frames.Sample(11000)["1"].SequenceEqual([0, 255, 0]), "expired");
            frames.Push(new Dictionary<int, double[]> { [1] = [255, 255, 255] }, 9999);
            e(frames.Latest()["1"].SequenceEqual([0, 255, 0]), "older frame ignored");
        });
    }

}
