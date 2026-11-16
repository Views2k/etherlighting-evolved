using System.Globalization;
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core;

/// <summary>
/// The browser workspace's localStorage (kept across restarts) and sessionStorage (kept until EE quits), with the
/// same keys, so ported code reads and writes the same values. Local values are saved in preferences.json in the
/// application data folder. A demo copy keeps its own file in the demo data folder, as the browser keeps the demo's
/// storage apart through its separate address, so a demo session never changes the installed app's theme or palettes.
/// </summary>
public static class Preferences
{
    static readonly object Gate = new();
    static readonly Dictionary<string, string> SessionValues = new(StringComparer.Ordinal);
    static Dictionary<string, string>? local;
    static bool unreadable;

    internal static string File => Path.Combine(App.Options.Demo ? Locations.Workspace(true) : Locations.AppData, "preferences.json");

    /// <summary>
    /// The saved values, read once. A file that cannot be opened now, for example while another program holds it, is
    /// not taken as empty: the error is thrown and the next use reads it again.
    /// </summary>
    static Dictionary<string, string> Local
    {
        get
        {
            if (local is not null) return local;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            string text;
            try { text = System.IO.File.ReadAllText(File); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return local = values; }
            if (Json.Parse(text) is JsonObject saved)
            {
                foreach (var (key, value) in saved)
                    if (value?.Str() is { } item) values[key] = item;
            }
            else unreadable = true;
            return local = values;
        }
    }

    public static string? GetItem(string key)
    {
        lock (Gate)
        {
            try { return Local.TryGetValue(key, out var value) ? value : null; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>
    /// Saves a value. Throws when it cannot be written, as localStorage.setItem does, and when the saved file cannot be
    /// opened now, so its other values are never replaced by this one alone.
    /// </summary>
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
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        if (unreadable && System.IO.File.Exists(File))
        {
            // A file that could not be parsed may still hold saved palettes, so it is set aside under the time it was
            // found instead of replaced, next to any copy set aside earlier.
            System.IO.File.Move(File, $"{File}.{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}.unreadable");
        }
        unreadable = false;
        var json = new JsonObject();
        foreach (var (key, value) in Local) json[key] = value;
        var temporary = File + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            System.IO.File.WriteAllText(temporary, json.ToJsonString());
            System.IO.File.Move(temporary, File, overwrite: true);
        }
        finally { System.IO.File.Delete(temporary); }
    }

    public static string? GetSession(string key)
    {
        lock (Gate) return SessionValues.TryGetValue(key, out var value) ? value : null;
    }

    public static void SetSession(string key, string value)
    {
        lock (Gate) SessionValues[key] = value;
    }
}
