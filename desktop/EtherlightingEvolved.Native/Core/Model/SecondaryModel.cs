// Port of preview/secondary-model.mjs: profile names and fingerprints, profile SFP summaries, rule draft helpers and
// the operation gate the secondary pages use to block duplicate writes.
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

public static class SecondaryModel
{
    static readonly string[] SfpKeys = ["mode", "color", "brightness", "effect", "period", "secondary_color", "color_brightness", "secondary_brightness"];

    static bool IsHexColor(string text) => text.Length == 7 && text[0] == '#' && text.Skip(1).All(char.IsAsciiHexDigit);

    /// <summary>Sorted keys, with six-digit hex colors lowercased (normalize in the module).</summary>
    static JsonNode? Normalize(JsonNode? value)
    {
        switch (value)
        {
            case JsonArray array:
                return Js.ArrayOf(array.Select(Normalize));
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var key in Js.Keys(obj).OrderBy(key => key, StringComparer.Ordinal)) result[key] = Normalize(obj[key]);
                return result;
        }
        if (Js.Str(value) is { } text && IsHexColor(text)) return text.ToLowerInvariant();
        return Js.Clone(value);
    }

    /// <summary>profileNameError(value): '' for a valid name, otherwise the message.</summary>
    public static string ProfileNameError(JsonNode? value)
    {
        var name = Js.Trim(Js.ToStr(value));
        if (name is "." or "..") return "Use a profile name other than a single or double period.";
        var count = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            count++;
            var category = Rune.GetUnicodeCategory(rune);
            var letter = category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;
            var number = category is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
            if (!letter && !number && rune.Value is not ('_' or '.' or ' ' or '-'))
                return "Use 1–64 letters, numbers, spaces, periods, underscores, or hyphens.";
        }
        return count is >= 1 and <= 64 ? "" : "Use 1–64 letters, numbers, spaces, periods, underscores, or hyphens.";
    }

    public static string ProfileNameError(string? value) => ProfileNameError(value is null ? null : JsonValue.Create(value));

    /// <summary>sceneFingerprint(scene): a comparable text form of a scene's lighting settings, ignoring telemetry.</summary>
    public static string SceneFingerprint(JsonNode? scene = null)
    {
        var sfpNode = Js.Get(scene, "sfp");
        var sfp = new JsonObject();
        if (sfpNode is JsonArray lights)
        {
            foreach (var light in lights) sfp[Js.ToStr(Js.Get(light, "port"))] = Js.Clone(light);
        }
        else if (Js.Truthy(sfpNode)) Js.Assign(sfp, sfpNode);
        var zones = Js.Items(Js.Truthy(Js.Get(scene, "zones")) ? Js.Get(scene, "zones") : null).Select(zone =>
        {
            var item = new JsonObject();
            if (Js.Has(zone, "id")) item["id"] = Js.Clone(Js.Get(zone, "id"));
            if (Js.Has(zone, "name")) item["name"] = Js.Clone(Js.Get(zone, "name"));
            item["ports"] = Js.ArrayOf(Js.SortNumeric(Js.Items(Js.Get(zone, "ports"))));
            var effect = Js.Get(zone, "effect");
            var effectItem = new JsonObject();
            if (Js.Has(effect, "id")) effectItem["id"] = Js.Clone(Js.Get(effect, "id"));
            effectItem["params"] = Js.Truthy(Js.Get(effect, "params")) ? Js.Clone(Js.Get(effect, "params")) : new JsonObject();
            item["effect"] = effectItem;
            return item;
        }).OrderBy(item => Js.ToStr(item["id"]), Comparer<string>.Create(Js.CompareText)).ToList();
        var sync = Js.Get(scene, "sfp_sync");
        var lightsByPort = new JsonObject();
        foreach (var (port, light) in Js.Entries(sfp))
        {
            var settings = new JsonObject();
            foreach (var key in SfpKeys) if (Js.Has(light, key)) settings[key] = Js.Clone(Js.Get(light, key));
            lightsByPort[port] = settings;
        }
        var value = new JsonObject
        {
            ["brightness"] = Js.Clone(Js.Get(scene, "brightness")) ?? 100,
            ["zones"] = Js.ArrayOf(zones),
            ["rules"] = Js.Truthy(Js.Get(scene, "rules")) ? Js.Clone(Js.Get(scene, "rules")) : new JsonArray(),
            ["overrides"] = Js.Truthy(Js.Get(scene, "overrides")) ? Js.Clone(Js.Get(scene, "overrides")) : new JsonObject(),
            ["sfp_sync"] = new JsonObject
            {
                ["mode"] = Js.Clone(Js.Get(sync, "mode")) ?? "independent",
                ["source_port"] = Js.Clone(Js.Get(sync, "source_port")) ?? 1,
            },
            ["sfp"] = lightsByPort,
        };
        return Js.Stringify(Normalize(value));
    }

    /// <summary>matchingProfile(profiles, state): the name of a saved profile with the same settings, or null.</summary>
    public static string? MatchingProfile(JsonNode? profiles, JsonNode? state)
    {
        var current = SceneFingerprint(state);
        return Js.Keys(profiles).FirstOrDefault(name => SceneFingerprint(Js.Get(profiles, name)) == current);
    }

    /// <summary>profileSfpSummary(scene): [{port, label}] describing what each saved SFP light does.</summary>
    public static JsonArray ProfileSfpSummary(JsonNode? scene)
    {
        IEnumerable<double> ports;
        if (PortTopology.SceneTopology(scene) is { } topology) ports = topology.Sfp.Select(port => (double)port);
        else
        {
            var sfp = Js.Get(scene, "sfp");
            var list = sfp is JsonArray lights ? lights.Select(light => Js.Get(light, "port")).ToList()
                : Js.Keys(Js.Truthy(sfp) ? sfp : null).Select(key => (JsonNode?)JsonValue.Create(Js.StringToNumber(key))).ToList();
            ports = Js.SortNumeric(list).Select(Js.ToNumber);
        }
        var sync = SfpSyncModel.SfpSync(scene);
        var mode = Js.Str(sync["mode"]);
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pulse"] = "Gentle pulse", ["color_cycle"] = "Color cycle", ["two_color"] = "Two-color fade", ["steady"] = "Steady color",
        };
        var result = new JsonArray();
        foreach (var port in ports)
        {
            var zone = Js.Objects(Js.Get(scene, "zones")).FirstOrDefault(item => Js.Includes(item["ports"], port));
            var effect = Js.Get(zone, "effect");
            if (Js.Truthy(effect))
            {
                var label = Js.Truthy(Js.Get(effect, "name")) ? Js.ToStr(Js.Get(effect, "name"))
                    : Js.Objects(EffectRenderer.EffectList).FirstOrDefault(item => Js.StrictEquals(item["id"], Js.Get(effect, "id")))?["name"] is { } named && Js.Truthy(named) ? Js.ToStr(named)
                    : "Group effect";
                result.Add(new JsonObject { ["port"] = port, ["label"] = label });
                continue;
            }
            if (mode == "ethernet")
            {
                result.Add(new JsonObject { ["port"] = port, ["label"] = $"Following port {Js.ToStr(sync["source_port"])}" });
                continue;
            }
            var light = SfpSyncModel.EffectiveSfpLight(scene, port);
            var lightMode = light["mode"];
            var text = Js.StrictEquals(lightMode, "off") ? "Off" : Js.StrictEquals(lightMode, "unmanaged") ? "Link status"
                : Js.Str(light["effect"]) is { } key && names.TryGetValue(key, out var name) ? name : "Steady color";
            result.Add(new JsonObject { ["port"] = port, ["label"] = mode == "together" ? $"{text} · Together" : text });
        }
        return result;
    }

    /// <summary>templateForRule(template, draft, defaultPorts): the draft with the template's conditions and look.</summary>
    public static JsonObject TemplateForRule(JsonNode? template, JsonNode? draft, IEnumerable<int> defaultPorts)
    {
        var result = Js.Spread(draft);
        var name = Js.Get(draft, "name");
        var useTemplate = Js.Str(name) is not { } text || Js.Trim(text).Length == 0 || text == "New rule";
        if (useTemplate)
        {
            if (Js.Has(template, "name")) result["name"] = Js.Clone(Js.Get(template, "name"));
            else result.Remove("name");
        }
        var ports = Js.Get(draft, "ports");
        result["ports"] = ports is null ? Js.NumberArray(defaultPorts) : Js.ArrayOf(Js.Items(ports));
        result["conditions"] = Js.Truthy(Js.Get(template, "conditions")) ? Js.Clone(Js.Get(template, "conditions")) : new JsonArray();
        result["look"] = Js.Truthy(Js.Get(template, "look")) ? Js.Clone(Js.Get(template, "look")) : new JsonObject();
        result["match"] = "all";
        return result;
    }

    /// <summary>scopeSummary(ports, copperPorts, sfpPorts): "All ports", "All Ethernet ports", "All SFP ports" or a list.</summary>
    public static string ScopeSummary(JsonNode? ports, IReadOnlyList<int> copperPorts, IReadOnlyList<int> sfpPorts)
    {
        var chosen = ports is null ? copperPorts.Select(port => (JsonNode?)JsonValue.Create(port)).ToList() : Js.Items(ports).ToList();
        var all = copperPorts.Concat(sfpPorts).ToList();
        bool Same(IReadOnlyList<int> list) => list.Count == chosen.Count && list.All(port => chosen.Any(item => Js.StrictEquals(item, port)));
        if (chosen.Count == 0) return "No ports selected";
        if (Same(all)) return "All ports";
        if (Same(copperPorts)) return "All Ethernet ports";
        if (sfpPorts.Count > 0 && Same(sfpPorts)) return "All SFP ports";
        return string.Join(", ", chosen.Select(port => Js.Num(port) is { } n && sfpPorts.Contains((int)n) && n == Math.Floor(n) ? $"SFP {Js.ToStr(port)}" : $"Port {Js.ToStr(port)}"));
    }

    /// <summary>conditionAvailable(condition, specs): the condition's field and operator are offered by the specs.</summary>
    public static bool ConditionAvailable(JsonNode? condition, JsonNode? specs) =>
        Js.Truthy(condition) && Js.Objects(specs).Any(spec => Js.StrictEquals(spec["key"], Js.Get(condition, "field")) && Js.Includes(spec["operators"], Js.Get(condition, "op")));

    /// <summary>ruleRateFromCycle(rate, originalCycle, currentCycle): the saved rate unless the cycle length changed.</summary>
    public static double RuleRateFromCycle(double rate, JsonNode? originalCycle, JsonNode? currentCycle) =>
        Js.ToNumber(currentCycle) == Js.ToNumber(originalCycle) ? rate : 1 / Js.ToNumber(currentCycle);

    /// <summary>rulesSnapshot(baseScene, rules): the loaded scene with the rule list replaced.</summary>
    public static JsonObject RulesSnapshot(JsonNode? baseScene, JsonNode? rules)
    {
        if (!Js.Truthy(baseScene) || Js.Get(baseScene, "rules") is not JsonArray || rules is not JsonArray list
            || list.Any(rule => !Js.Truthy(rule) || Js.Str(Js.Get(rule, "id")) is not { } id || Js.Trim(id).Length == 0)
            || Js.Unique(list.Select(rule => Js.Get(rule, "id"))).Count != list.Count)
            throw new InvalidOperationException("Refresh the rules before applying changes.");
        var result = Js.Spread(baseScene);
        result["rules"] = Js.Clone(rules);
        return result;
    }

    /// <summary>validateRuleDraft(draft, specs): '' for a complete rule, otherwise the message.</summary>
    public static string ValidateRuleDraft(JsonNode? draft, JsonNode? specs = null)
    {
        if (Js.Trim(Js.Str(Js.Get(draft, "name")) ?? "").Length == 0) return "Enter a rule name.";
        if (Js.Length(Js.Get(draft, "ports")) == 0) return "Choose at least one port.";
        var conditions = Js.Items(Js.Get(draft, "conditions")).ToList();
        if (conditions.Count == 0) return "Add at least one condition.";
        if (Js.Truthy(specs) && conditions.Any(condition => !ConditionAvailable(condition, specs))) return "Replace or remove unavailable conditions before applying.";
        if (conditions.Any(condition => Js.Get(condition, "value") is JsonArray { Count: 0 })) return "Choose a value for every condition.";
        return "";
    }

    /// <summary>createOperationGate(onBusy, onError): runs one write at a time and reports failures while active.</summary>
    public static OperationGate CreateOperationGate(Action<bool> onBusy, Action<Exception> onError) => new(onBusy, onError);
}

/// <summary>The gate createOperationGate returns.</summary>
public sealed class OperationGate(Action<bool> onBusy, Action<Exception> onError)
{
    Task? pending;
    bool active = true;

    public Task? Pending => pending;

    public bool Active => active;

    /// <summary>run(action): starts the action unless one is pending (then returns that one) or the gate is closed.</summary>
    public Task Run(Func<Task> action)
    {
        if (!active) return Task.CompletedTask;
        if (pending is not null) return pending;
        onBusy(true);
        pending = Execute(action);
        return pending;
    }

    public Task Run(Action action) => Run(() =>
    {
        action();
        return Task.CompletedTask;
    });

    async Task Execute(Func<Task> action)
    {
        // The action starts after run() returns, as Promise.resolve().then(action) does.
        await Task.Yield();
        try { await action(); }
        catch (Exception error)
        {
            if (active) onError(error);
        }
        finally
        {
            pending = null;
            if (active) onBusy(false);
        }
    }

    /// <summary>settle(): waits for the pending action and returns whether the gate is still active.</summary>
    public async Task<bool> Settle()
    {
        if (pending is { } running) await running;
        return active;
    }

    /// <summary>close(): later runs do nothing and callbacks stop.</summary>
    public void Close() => active = false;
}
