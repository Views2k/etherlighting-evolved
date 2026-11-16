// Port of src/rules.mjs: automation rule fields, condition matching, rule priority and rule looks.
// A date is the clock the conditions read (local time for new Date(), or the EE host's time from ScheduleClock.HostDate).
// A null date stands for an invalid date: time and weekday conditions never match it.
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

public sealed record RuleRateLimit(double Min, double Max, double Default);

public static class Rules
{
    /// <summary>RULE_RATE_LIMITS: animation rates allowed for each rule style.</summary>
    public static readonly IReadOnlyDictionary<string, RuleRateLimit> RuleRateLimits = new Dictionary<string, RuleRateLimit>(StringComparer.Ordinal)
    {
        ["pulse"] = new(.05, .33, .25),
        ["blink"] = new(.05, 1, .5),
        ["flash"] = new(.05, .5, .5),
    };

    static readonly RuleRateLimit DefaultLimit = new(.05, 1, .5);

    /// <summary>ruleRate(style, value): the value within the style's range, or the style's default.</summary>
    public static double RuleRate(string? style, double? value = null)
    {
        var limits = style is not null && RuleRateLimits.TryGetValue(style, out var found) ? found : DefaultLimit;
        return ColorMath.Clamp(value is { } number && double.IsFinite(number) ? number : limits.Default, limits.Min, limits.Max);
    }

    public static double RuleRate(JsonNode? style, JsonNode? value) => RuleRate(Js.Str(style) ?? (style is null ? null : Js.ToStr(style)), Js.Num(value));

    /// <summary>FIELDS: the facts a condition can test.</summary>
    public static readonly JsonArray Fields = (JsonArray)JsonNode.Parse("""
        [{"key":"link","label":"Link status","type":"bool","trueLabel":"Online","falseLabel":"Offline"},
         {"key":"anomaly","label":"Port anomaly","type":"bool","trueLabel":"Detected","falseLabel":"Clear"},
         {"key":"poe","label":"PoE class","type":"choice","choices":["off","poe","poe+","poe++","poe+++"]},
         {"key":"speed","label":"Link speed","type":"number","unit":"Mbps"},
         {"key":"poe_power","label":"PoE power","type":"number","unit":"W"},
         {"key":"traffic","label":"Traffic","type":"number","unit":"Mbps"},
         {"key":"time","label":"Time of day","type":"time"},
         {"key":"weekday","label":"Day of week","type":"weekday","choices":["mon","tue","wed","thu","fri","sat","sun"]},
         {"key":"uplink","label":"Uplink","type":"bool"},{"key":"full_duplex","label":"Full duplex","type":"bool"},
         {"key":"lag","label":"Link aggregation","type":"bool"},
         {"key":"stp_state","label":"Spanning tree","type":"choice","choices":["forwarding","blocking","learning","listening","discarding","disabled"]}]
        """)!;

    /// <summary>OPERATORS: condition operator labels in display order.</summary>
    public static readonly IReadOnlyList<KeyValuePair<string, string>> Operators =
    [
        new("gt", "is above"), new("ge", "is at least"), new("lt", "is below"), new("le", "is at most"), new("eq", "equals"),
        new("ne", "does not equal"), new("between", "is between"), new("is", "is"), new("is_not", "is not"), new("in", "is one of"),
        new("not_in", "is not one of"),
    ];

    static readonly string[] Weekdays = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    static double Minutes(JsonNode? value)
    {
        var parts = Js.ToStr(value).Split(':');
        var h = Js.StringToNumber(parts[0]);
        var m = parts.Length > 1 ? Js.StringToNumber(parts[1]) : double.NaN;
        return h * 60 + m;
    }

    /// <summary>conditionMatches(condition, facts, date).</summary>
    public static bool ConditionMatches(JsonNode? condition, JsonNode? facts, DateTime? date) =>
        ConditionMatches(condition, key => Js.Get(facts, key), date);

    internal static bool ConditionMatches(JsonNode? condition, Func<string, JsonNode?> facts, DateTime? date)
    {
        var field = Js.Get(condition, "field");
        var op = Js.Str(Js.Get(condition, "op"));
        var value = Js.Get(condition, "value");
        var fieldName = Js.Str(field);
        JsonNode? actual = field is null ? facts("undefined") : facts(fieldName ?? Js.ToStr(field));
        if (fieldName == "time") actual = date is { } now ? JsonValue.Create(now.Hour * 60 + now.Minute) : JsonValue.Create(double.NaN);
        if (fieldName == "weekday") actual = date is { } day ? JsonValue.Create(Weekdays[(int)day.DayOfWeek]) : null;
        if (actual is null) return false;
        if (fieldName == "time")
        {
            var range = Js.Items(value).ToList();
            double a = range.Count > 0 ? Minutes(range[0]) : double.NaN, b = range.Count > 1 ? Minutes(range[1]) : double.NaN;
            var minutes = Js.ToNumber(actual);
            return a <= b ? minutes >= a && minutes < b : minutes >= a || minutes < b;
        }
        if (op == "in") return Js.Includes(value, actual);
        if (op == "not_in") return !Js.Includes(value, actual);
        if (op == "between") return Compare(actual, Js.Get(value, 0)) is >= 0 and not 2 && Compare(actual, Js.Get(value, 1)) is <= 0 and not -2;
        return op switch
        {
            "is" or "eq" => Js.StrictEquals(actual, value),
            "is_not" or "ne" => !Js.StrictEquals(actual, value),
            "gt" => Compare(actual, value) == 1,
            "ge" => Compare(actual, value) is 0 or 1,
            "lt" => Compare(actual, value) == -1,
            "le" => Compare(actual, value) is 0 or -1,
            _ => false,
        };
    }

    /// <summary>
    /// The relational comparison of two values: -1, 0 or 1, or 2 / -2 when the comparison is undefined (NaN).
    /// Strings compare by code unit when both sides are strings; everything else compares as numbers.
    /// </summary>
    static int Compare(JsonNode? a, JsonNode? b)
    {
        if (Js.Str(a) is { } s && Js.Str(b) is { } t)
        {
            var order = string.CompareOrdinal(s, t);
            return order < 0 ? -1 : order > 0 ? 1 : 0;
        }
        if (a is JsonObject || b is JsonObject) return 2;
        double x = a is null ? double.NaN : Js.ToNumber(a), y = b is null ? double.NaN : Js.ToNumber(b);
        if (double.IsNaN(x) || double.IsNaN(y)) return 2;
        return x < y ? -1 : x > y ? 1 : 0;
    }

    /// <summary>matchingRule(rules, port, date): the first enabled rule in scope whose conditions hold; port is {id, facts}.</summary>
    public static JsonObject? MatchingRule(JsonNode? rules, JsonNode? port, DateTime? date) =>
        MatchingRule(rules, Js.Get(port, "id"), key => Js.Get(Js.Get(port, "facts"), key), date);

    /// <summary>matchingRule for a port ID and its facts without building the {id, facts} object.</summary>
    public static JsonObject? MatchingRule(JsonNode? rules, double id, JsonNode? facts, DateTime? date) =>
        MatchingRule(rules, JsonValue.Create(id), key => Js.Get(facts, key), date);

    internal static JsonObject? MatchingRule(JsonNode? rules, JsonNode? id, Func<string, JsonNode?> facts, DateTime? date) =>
        MatchingRule(Js.Items(rules), id, facts, date);

    static JsonObject? MatchingRule(IEnumerable<JsonNode?> rules, JsonNode? id, Func<string, JsonNode?> facts, DateTime? date)
    {
        foreach (var rule in rules)
        {
            if (rule is not JsonObject r) continue;
            if (!Js.Truthy(r["enabled"])) continue;
            var ports = r["ports"];
            if (Js.Length(ports) > 0 && !Js.Includes(ports, id)) continue;
            var conditions = Js.Items(r["conditions"]).ToList();
            if (conditions.Count == 0) return r;
            var matches = Js.StrictEquals(r["match"], "any")
                ? conditions.Any(condition => ConditionMatches(condition, facts, date))
                : conditions.All(condition => ConditionMatches(condition, facts, date));
            if (matches) return r;
        }
        return null;
    }

    /// <summary>matchingSfpRule(rules, item, date = new Date()): a rule that explicitly targets the SFP port and applies to its mode.</summary>
    public static JsonObject? MatchingSfpRule(JsonNode? rules, JsonNode? item) => MatchingSfpRule(rules, item, DateTime.Now);

    public static JsonObject? MatchingSfpRule(JsonNode? rules, JsonNode? item, DateTime? date) =>
        MatchingSfpRule(rules, item, Js.Get(item, "mode"), date);

    /// <summary>matchingSfpRule with item.mode replaced (as for {...item, mode}).</summary>
    public static JsonObject? MatchingSfpRule(JsonNode? rules, JsonNode? item, JsonNode? mode, DateTime? date)
    {
        if (Js.StrictEquals(mode, "off")) return null;
        var itemFacts = Js.Get(item, "facts");
        JsonNode? Facts(string key) => key switch
        {
            "link" => Js.Get(item, "link"),
            "anomaly" => Js.Get(item, "anomaly"),
            _ => Js.Get(itemFacts, key),
        };
        var port = Js.Get(item, "port");
        var scoped = Js.Items(rules).Where(rule => Js.Includes(Js.Get(rule, "ports"), port));
        var rule = MatchingRule(scoped, port, Facts, date);
        if (rule is null) return null;
        return Js.StrictEquals(mode, "color") || !Js.StrictEquals(Js.Get(rule["look"], "style"), "effect") ? rule : null;
    }

    /// <summary>rulePortLabel(ports, sfpPorts): "All copper ports" or a list such as "Port 1, SFP 9".</summary>
    public static string RulePortLabel(JsonNode? ports, JsonNode? sfpPorts = null)
    {
        if (Js.Length(ports) == 0) return "All copper ports";
        return string.Join(", ", Js.Items(ports).Select(port => Js.Includes(sfpPorts, port) ? $"SFP {Js.ToStr(port)}" : $"Port {Js.ToStr(port)}"));
    }

    /// <summary>renderLook(look, underneath, t): the rule's color at time t; the effect style shows what is underneath.</summary>
    public static object? RenderLook(JsonNode? look, object? underneath, double t)
    {
        var style = Js.Str(Js.Get(look, "style"));
        var phase = t * RuleRate(Js.Get(look, "style"), Js.Get(look, "rate")) % 1;
        object? color = Js.Truthy(Js.Get(look, "color")) ? Effects.Paint(Js.Get(look, "color")) : "#FFFFFF";
        if (style == "effect") return underneath;
        if (style == "blink") return phase < .5 ? color : "#000000";
        if (style == "flash") return phase < .2 ? color : "#000000";
        if (style == "pulse") return ColorMath.Dim(Effects.ColorText(color), .15 + .85 * (.5 - .5 * Math.Cos(phase * Math.PI * 2)));
        return color;
    }

    /// <summary>makeRule(type = 'link'): a new rule from one of the example templates.</summary>
    public static JsonObject MakeRule(string type = "link")
    {
        var (name, conditions, color, style, brightness) = type switch
        {
            "anomaly" => ("Anomaly detected", """[{"field":"anomaly","op":"is","value":true}]""", "#F2B769", "pulse", (double?)null),
            "poe" => ("PoE+ devices", """[{"field":"poe","op":"is","value":"poe+"}]""", "#A490E4", null, null),
            "time" => ("After hours", """[{"field":"time","op":"between","value":["22:00","07:00"]}]""", "#C6A0E5", null, 25),
            _ => ("Offline", """[{"field":"link","op":"is","value":false}]""", "#F25D68", null, null),
        };
        return new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N")[..24],
            ["name"] = name,
            ["conditions"] = JsonNode.Parse(conditions),
            ["match"] = "all",
            ["ports"] = null,
            ["enabled"] = true,
            ["look"] = new JsonObject
            {
                ["color"] = color, ["style"] = style ?? "solid", ["brightness"] = brightness is { } level ? JsonValue.Create(level) : null,
                ["rate"] = RuleRate(style),
            },
        };
    }
}
