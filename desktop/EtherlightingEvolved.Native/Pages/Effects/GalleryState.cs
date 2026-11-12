using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;

namespace EtherlightingEvolved.Native.Pages.Effects;

internal static partial class GalleryState
{
    const string PreferenceKey = "ee.ui-preferences.v1";
    internal static readonly string[] Categories = ["All", "Favorites", "Recent", "Ambient", "Motion", "Activity"];

    internal static int[] Scope(PortLayout layout, IEnumerable<int> selected, bool optical, bool includeSfp)
    {
        var selectedIds = selected.ToHashSet();
        var copper = layout.Copper.Where(selectedIds.Contains).ToArray();
        var sfp = layout.Sfp.Where(selectedIds.Contains).ToArray();
        if (optical || copper.Length == 0) return sfp;
        if (!includeSfp) return copper;
        return [.. copper, .. (sfp.Length == 0 ? layout.Sfp.ToArray() : sfp)];
    }

    [GeneratedRegex(@"^(?:sfp:)?[a-z][a-z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    static JsonObject Read() => Json.Parse(Preferences.GetItem(PreferenceKey) ?? "{}") as JsonObject ?? [];

    static List<string> ReadList(JsonObject state, string property, int limit) =>
        state.Arr(property).Select(value => value.Str()).OfType<string>()
            .Where(id => IdPattern().IsMatch(id)).Distinct(StringComparer.Ordinal).Take(limit).ToList();

    internal static List<string> Favorites() => ReadList(Read(), "favoriteEffects", 64);
    internal static List<string> Recent() => ReadList(Read(), "recentEffects", 12);

    internal static List<string> Change(string key, bool favorite)
    {
        if (!IdPattern().IsMatch(key)) throw new InvalidOperationException("Invalid effect.");
        var state = Read();
        var property = favorite ? "favoriteEffects" : "recentEffects";
        var ids = ReadList(state, property, favorite ? 64 : 12);
        var existed = ids.Remove(key);
        if (!favorite)
        {
            ids.Insert(0, key);
            if (ids.Count > 12) ids.RemoveAt(12);
        }
        else if (!existed)
        {
            if (ids.Count == 64) throw new InvalidOperationException("Remove a favorite before adding another.");
            ids.Add(key);
        }
        state["version"] = 1;
        state[property] = new JsonArray(ids.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        try { Preferences.SetItem(PreferenceKey, state.ToJsonString()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Could not save preferences on this device.", ex);
        }
        return ids;
    }
}
