using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EtherlightingEvolved.Native.Core;

/// <summary>
/// The access key this computer received when it paired with EE at each LAN address, kept in workspace-keys.json in the
/// application data folder: one key per origin, at most 16, and pairing at a 17th address replaces the oldest. A key is
/// replaced only by a new pairing for the same origin. An answer that says this computer is not paired never removes
/// it, because over plain HTTP anyone on the network can send that answer. A file that cannot be read counts as empty
/// and stays as it is until the next pairing replaces it. A file in a newer format also counts as empty, and a pairing
/// never replaces it.
/// </summary>
public static class DeviceKeys
{
    /// <summary>The layout this version writes, following native.json: a file without "format" is the same layout.</summary>
    const int Format = 1;
    const int MaxKeys = 16;
    static readonly Regex KeyPattern = new("^[0-9a-f]{64}\\z");
    static readonly object Gate = new();

    internal static string File => Path.Combine(Locations.AppData, "workspace-keys.json");

    readonly record struct Entry(string Origin, string Key, DateTime SavedAt);

    static string Origin(Uri address) => address.GetLeftPart(UriPartial.Authority);

    public static string? Get(Uri address)
    {
        if (!WorkspaceAddress.IsLan(address)) return null;
        lock (Gate) return Read().Where(entry => entry.Origin == Origin(address)).Select(entry => entry.Key).FirstOrDefault();
    }

    public static void Set(Uri address, string key)
    {
        if (!WorkspaceAddress.IsLan(address) || !KeyPattern.IsMatch(key)) throw new ArgumentException("A device key belongs to a LAN address and has 64 lowercase hexadecimal characters.");
        lock (Gate)
        {
            var origin = Origin(address);
            var saved = Read(out var newer);
            if (newer) throw new NewerDeviceKeysException();
            var entries = saved.Where(entry => entry.Origin != origin).OrderBy(entry => entry.SavedAt).ToList();
            if (entries.Count >= MaxKeys) entries.RemoveRange(0, entries.Count - MaxKeys + 1);
            entries.Add(new Entry(origin, key, DateTime.UtcNow));
            Write(entries);
        }
    }

    static List<Entry> Read() => Read(out _);

    static List<Entry> Read(out bool newer)
    {
        newer = false;
        string text;
        try { text = System.IO.File.ReadAllText(File); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
        if (Json.Parse(text) is not JsonObject root) return [];
        if (root["format"] is { } format && format.Int() != Format)
        {
            newer = format.Int() > Format;
            return [];
        }
        if (root["keys"] is not JsonArray keys || keys.Count > MaxKeys) return [];
        var entries = new List<Entry>();
        foreach (var item in keys)
        {
            if (item.Str("origin") is not { } origin || WorkspaceAddress.Parse(origin) is not { } address || !WorkspaceAddress.IsLan(address) || Origin(address) != origin
                || entries.Any(entry => entry.Origin == origin) || item.Str("key") is not { } key || !KeyPattern.IsMatch(key)
                || !DateTime.TryParseExact(item.Str("saved_at"), "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var saved))
                return [];
            entries.Add(new Entry(origin, key, saved));
        }
        return entries;
    }

    static void Write(List<Entry> entries)
    {
        var json = new JsonObject
        {
            ["format"] = Format,
            ["keys"] = new JsonArray(entries.Select(entry => (JsonNode)new JsonObject
            {
                ["origin"] = entry.Origin, ["key"] = entry.Key, ["saved_at"] = entry.SavedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            }).ToArray()),
        };
        Directory.CreateDirectory(Locations.AppData);
        var temporary = File + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        // Only this user can read a key that lets its holder control EE.
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var writer = new StreamWriter(new FileStream(temporary, options))) writer.Write(json.ToJsonString());
            System.IO.File.Move(temporary, File, overwrite: true);
        }
        finally { System.IO.File.Delete(temporary); }
    }
}

/// <summary>workspace-keys.json was saved by a newer version of EE, which this version neither reads nor replaces.</summary>
public sealed class NewerDeviceKeysException() : IOException("The LAN access keys were saved by a newer version of EE.");
