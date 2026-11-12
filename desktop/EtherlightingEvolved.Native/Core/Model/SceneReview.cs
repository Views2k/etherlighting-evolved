// Port of the logic of preview/scene-review.mjs: the scene a state represents, a three-way merge of the base, current
// and preview scenes, and the text of the review dialog. reviewSceneConflict shows that dialog with DOM elements; a
// page port builds the dialog from ReviewContent and returns the scene of the chosen action (null for Cancel).
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EtherlightingEvolved.Native.Core.Model;

/// <summary>The text and choices of the review dialog (heading, message, both change lists and the actions in order).</summary>
public sealed record SceneReviewContent(string Heading, string Message, string RemoteHeading, List<string> RemoteChanges, List<string> Changes,
    List<(string Label, JsonObject? Scene)> Actions);

public static class SceneReview
{
    /// <summary>A value that may be undefined (absent), as merge receives it.</summary>
    readonly record struct Slot(bool Present, JsonNode? Value)
    {
        public static readonly Slot Undefined = new(false, null);

        public static Slot Of(JsonNode? node, string key) =>
            node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? new Slot(true, value) : Undefined;
    }

    static JsonNode? Canonical(JsonNode? value)
    {
        switch (value)
        {
            case JsonArray array:
                return Js.ArrayOf(array.Select(Canonical));
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var key in Js.Keys(obj).OrderBy(key => key, StringComparer.Ordinal)) result[key] = Canonical(obj[key]);
                return result;
        }
        return Js.Clone(value);
    }

    static bool Same(Slot a, Slot b)
    {
        if (!a.Present || !b.Present) return !a.Present && !b.Present;
        return Js.Stringify(Canonical(a.Value)) == Js.Stringify(Canonical(b.Value));
    }

    /// <summary>sceneFromState(state): the scene of the current state, without telemetry.</summary>
    public static JsonObject SceneFromState(JsonNode? state)
    {
        var scene = new JsonObject { ["version"] = 3 };
        if (Js.Has(state, "topology")) scene["topology"] = Js.Clone(Js.Get(state, "topology"));
        scene["zones"] = Js.Truthy(Js.Get(state, "zones")) ? Js.Clone(Js.Get(state, "zones")) : new JsonArray();
        scene["rules"] = Js.Truthy(Js.Get(state, "rules")) ? Js.Clone(Js.Get(state, "rules")) : new JsonArray();
        scene["overrides"] = Js.Truthy(Js.Get(state, "overrides")) ? Js.Clone(Js.Get(state, "overrides")) : new JsonObject();
        if (Js.Has(state, "brightness")) scene["brightness"] = Js.Clone(Js.Get(state, "brightness"));
        var sfp = new JsonObject();
        foreach (var light in Js.Items(Js.Truthy(Js.Get(state, "sfp")) ? Js.Get(state, "sfp") : null)) sfp[Js.ToStr(Js.Get(light, "port"))] = LightingState.SfpSettings(light);
        scene["sfp"] = sfp;
        if (Js.Has(state, "sfp_sync")) scene["sfp_sync"] = Js.Clone(Js.Get(state, "sfp_sync"));
        return LightingState.Snapshot(scene, state, new JsonObject(), false);
    }

    static JsonObject Fields(JsonNode? scene)
    {
        var result = Js.Spread(scene);
        var zones = new JsonObject();
        foreach (var zone in Js.Items(Js.Truthy(Js.Get(scene, "zones")) ? Js.Get(scene, "zones") : null)) zones[Js.ToStr(Js.Get(zone, "id"))] = Js.Clone(zone);
        result["zones"] = zones;
        return result;
    }

    static JsonObject Membership(Slot value)
    {
        var result = new JsonObject();
        foreach (var (id, zone) in Js.Entries(Js.Truthy(value.Value) ? value.Value : null))
            if (Js.Has(zone, "ports")) result[id] = Js.Clone(Js.Get(zone, "ports"));
        return result;
    }

    static JsonObject Change(string path, Slot a, Slot b, Slot c)
    {
        var change = new JsonObject { ["path"] = path };
        if (a.Present) change["before"] = Js.Clone(a.Value);
        if (b.Present) change["current"] = Js.Clone(b.Value);
        if (c.Present) change["draft"] = Js.Clone(c.Value);
        return change;
    }

    /// <summary>
    /// compareScenes(base, current, desired, {preferDraft}): {scene, changes, conflicts, changed}. Settings only the
    /// preview changed are applied over the current scene; settings both changed are conflicts, resolved to the
    /// current value unless preferDraft. Group membership and effect types are merged as single decisions.
    /// </summary>
    public static JsonObject CompareScenes(JsonNode? baseScene, JsonNode? current, JsonNode? desired, bool preferDraft = false)
    {
        var conflicts = new JsonArray();
        var changes = new JsonArray();
        Slot Merge(Slot a, Slot b, Slot c, List<string> path)
        {
            if (Same(a, c)) return b.Present ? new Slot(true, Js.Clone(b.Value)) : Slot.Undefined;
            if (Same(b, c)) return c.Present ? new Slot(true, Js.Clone(c.Value)) : Slot.Undefined;
            var groupConflict = path.Count == 1 && path[0] == "zones"
                                && !Same(new Slot(true, Membership(a)), new Slot(true, Membership(b)))
                                && !Same(new Slot(true, Membership(a)), new Slot(true, Membership(c)));
            var effectConflict = path.Count > 0 && path[^1] == "effect" && Js.Truthy(a.Value) && Js.Truthy(b.Value) && Js.Truthy(c.Value)
                                 && (!Js.StrictEquals(Js.Get(a.Value, "id"), Js.Get(b.Value, "id")) || !Js.StrictEquals(Js.Get(a.Value, "id"), Js.Get(c.Value, "id")));
            var objects = !groupConflict && !effectConflict && a.Value is JsonObject && b.Value is JsonObject && c.Value is JsonObject;
            if (objects)
            {
                var result = new JsonObject();
                var keys = Js.Keys(a.Value).Concat(Js.Keys(b.Value)).Concat(Js.Keys(c.Value)).Distinct().ToList();
                foreach (var key in keys)
                {
                    var value = Merge(Slot.Of(a.Value, key), Slot.Of(b.Value, key), Slot.Of(c.Value, key), [.. path, key]);
                    if (value.Present) result[key] = value.Value;
                }
                return new Slot(true, result);
            }
            var change = Change(string.Join(".", path), a, b, c);
            changes.Add(change);
            if (!Same(a, b))
            {
                conflicts.Add(change.DeepClone());
                var winner = preferDraft ? c : b;
                return winner.Present ? new Slot(true, Js.Clone(winner.Value)) : Slot.Undefined;
            }
            return c.Present ? new Slot(true, Js.Clone(c.Value)) : Slot.Undefined;
        }
        var merged = Merge(new Slot(true, Fields(baseScene)), new Slot(true, Fields(current)), new Slot(true, Fields(desired)), []);
        var scene = merged.Value as JsonObject ?? new JsonObject();
        var zones = Js.ArrayOf(Js.Values(Js.Truthy(scene["zones"]) ? scene["zones"] : null));
        scene["zones"] = zones;
        var ports = zones.SelectMany(zone => Js.Truthy(Js.Get(zone, "ports")) ? Js.Items(Js.Get(zone, "ports")) : []).ToList();
        if (Js.Unique(ports).Count != ports.Count)
        {
            scene["zones"] = Js.Clone(Js.Get(preferDraft ? desired : current, "zones"));
            for (var index = conflicts.Count - 1; index >= 0; index--)
                if (Js.ToStr(Js.Get(conflicts[index], "path")).StartsWith("zones", StringComparison.Ordinal)) conflicts.RemoveAt(index);
            var conflict = new JsonObject { ["path"] = "zones" };
            if (Js.Has(baseScene, "zones")) conflict["before"] = Js.Clone(Js.Get(baseScene, "zones"));
            if (Js.Has(current, "zones")) conflict["current"] = Js.Clone(Js.Get(current, "zones"));
            if (Js.Has(desired, "zones")) conflict["draft"] = Js.Clone(Js.Get(desired, "zones"));
            conflicts.Add(conflict);
        }
        return new JsonObject
        {
            ["scene"] = scene, ["changes"] = changes, ["conflicts"] = conflicts,
            ["changed"] = SecondaryModel.SceneFingerprint(scene) != SecondaryModel.SceneFingerprint(current),
        };
    }

    static string Show(Slot value)
    {
        if (!value.Present) return "removed";
        var node = value.Value;
        switch (node)
        {
            case null:
                return "inherited";
            case JsonArray array:
                var items = string.Join(", ", array.Select(item => Show(new Slot(true, item))));
                return items.Length > 0 ? items : "none";
            case JsonObject obj:
                if (Js.Truthy(obj["ports"]))
                    return (Js.Truthy(obj["name"]) ? Js.ToStr(obj["name"]) : "Port group") + " (" + string.Join(", ", Js.Items(obj["ports"]).Select(port => port is null ? "" : Js.ToStr(port))) + ")"
                           + (Js.Truthy(obj["effect"]) ? " · " + Show(new Slot(true, obj["effect"])) : "");
                if (Js.Truthy(obj["id"]) && Js.Truthy(obj["params"])) return Js.ToStr(obj["id"]).Replace('_', ' ') + " · " + Show(new Slot(true, obj["params"]));
                if (Js.Truthy(obj["name"])) return Js.ToStr(obj["name"]);
                return string.Join(", ", Js.Entries(obj).Select(entry => entry.Key.Replace('_', ' ') + " " + Show(new Slot(true, entry.Value))));
        }
        return Js.ToStr(node);
    }

    /// <summary>The module's describe(change): "Path: current value; preview value".</summary>
    public static string Describe(JsonNode? change)
    {
        var path = Js.ToStr(Js.Get(change, "path"));
        path = Regex.Replace(path, "^zones$", "Port groups");
        path = Regex.Replace(path, "^brightness$", "Default brightness");
        path = Regex.Replace(path, "^overrides\\.([0-9]+)", "Port $1 fixed color");
        path = Regex.Replace(path, "^sfp\\.([0-9]+)", "SFP $1");
        path = path.Replace(".", " · ").Replace("_", " ");
        return $"{path}: current {Show(Slot.Of(change, "current"))}; preview {Show(Slot.Of(change, "draft"))}";
    }

    static string ReplaceFirst(string text, string find, string replace)
    {
        var at = text.IndexOf(find, StringComparison.Ordinal);
        return at < 0 ? text : text[..at] + replace + text[(at + find.Length)..];
    }

    /// <summary>The non-DOM part of reviewSceneConflict(base, current, desired): the dialog's text and its actions.</summary>
    public static SceneReviewContent ReviewContent(JsonNode? baseScene, JsonNode? current, JsonNode? desired)
    {
        var review = CompareScenes(baseScene, current, desired);
        var remote = CompareScenes(baseScene, baseScene, current)["changes"]!.AsArray();
        var conflicts = review["conflicts"]!.AsArray().Count;
        var remoteText = remote.Select(change =>
        {
            var shown = Js.Spread(change);
            Js.SetOrRemove(shown, "current", Js.Clone(Js.Get(change, "before")), Js.Has(change, "before"));
            return ReplaceFirst(ReplaceFirst(Describe(shown), "current ", "previous "), "preview ", "now ");
        }).ToList();
        var changeText = review["changes"]!.AsArray().Select(Describe).ToList();
        var actions = new List<(string, JsonObject?)> { ("Cancel", null) };
        if (conflicts > 0) actions.Add(("Keep current overlapping settings", (JsonObject)review["scene"]!.DeepClone()));
        actions.Add((conflicts > 0 ? "Use my preview settings" : "Apply my preview", CompareScenes(baseScene, current, desired, true)["scene"]!.AsObject()));
        return new SceneReviewContent("Review lighting changes",
            conflicts > 0 ? "Both clients changed these settings. Choose which values to keep." : "Another client changed the setup. Your preview can be applied while keeping its other changes.",
            "Changes from another client", remoteText, changeText, actions);
    }
}
