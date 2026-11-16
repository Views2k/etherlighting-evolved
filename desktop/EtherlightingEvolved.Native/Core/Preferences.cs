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

    static Dictionary<string, string> Local
    {
        get
        {
            if (local is not null) return local;
            local = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (Json.Parse(System.IO.File.ReadAllText(File)) is JsonObject saved)
                {
                    foreach (var (key, value) in saved)
                        if (value?.Str() is { } text) local[key] = text;
                }
                else unreadable = true;
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { unreadable = true; }
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
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        if (unreadable && System.IO.File.Exists(File))
        {
            // A file that could not be read may still hold saved palettes, so it is set aside instead of replaced.
            System.IO.File.Move(File, File + ".unreadable", overwrite: true);
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
