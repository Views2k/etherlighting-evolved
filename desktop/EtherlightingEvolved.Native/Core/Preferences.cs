using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core;

/// <summary>
/// The browser workspace's localStorage (kept across restarts) and sessionStorage (kept until EE quits), with the
/// same keys, so ported code reads and writes the same values. Local values are saved in preferences.json in the
/// application data folder.
/// </summary>
public static class Preferences
{
    static readonly object Gate = new();
    static readonly Dictionary<string, string> SessionValues = new(StringComparer.Ordinal);
    static Dictionary<string, string>? local;

    static string File => Path.Combine(Locations.AppData, "preferences.json");

    static Dictionary<string, string> Local
    {
        get
        {
            if (local is not null) return local;
            local = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (Json.Parse(System.IO.File.ReadAllText(File)) is JsonObject saved)
                    foreach (var (key, value) in saved)
                        if (value?.Str() is { } text) local[key] = text;
            }
            catch (Exception) { }
            return local;
        }
    }

    public static string? GetItem(string key)
    {
        lock (Gate) return Local.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>Saves a value. Throws when it cannot be written, as localStorage.setItem does.</summary>
    public static void SetItem(string key, string value)
    {
        lock (Gate)
        {
            Local[key] = value;
            Save();
        }
    }

    public static void RemoveItem(string key)
    {
        lock (Gate)
        {
            if (Local.Remove(key)) Save();
        }
    }

    static void Save()
    {
        Directory.CreateDirectory(Locations.AppData);
        var json = new JsonObject();
        foreach (var (key, value) in Local) json[key] = value;
        var temporary = File + ".tmp";
        System.IO.File.WriteAllText(temporary, json.ToJsonString());
        System.IO.File.Move(temporary, File, overwrite: true);
    }

    public static string? GetSession(string key)
    {
        lock (Gate) return SessionValues.TryGetValue(key, out var value) ? value : null;
    }

    public static void SetSession(string key, string value)
    {
        lock (Gate) SessionValues[key] = value;
    }

    public static void RemoveSession(string key)
    {
        lock (Gate) SessionValues.Remove(key);
    }
}
