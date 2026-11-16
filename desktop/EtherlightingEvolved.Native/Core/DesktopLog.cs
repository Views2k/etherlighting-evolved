using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EtherlightingEvolved.Native.Core;

/// <summary>
/// desktop.log in the application data folder, one JSON line per event: errors the window could not report in full
/// and why the bundled service failed to start or stopped. Like the service's own diagnostics it records error types
/// and code locations, not messages, because messages can carry switch addresses, user names and device names. The
/// service's last output lines are kept with address credentials removed. Past 256 KB the file becomes desktop.log.1.
/// </summary>
internal static class DesktopLog
{
    const long Limit = 256 * 1024;
    const int TextLimit = 500;
    static readonly object Gate = new();
    static readonly Regex Credentials = new(@"://[^/\s@]+@");

    public static string File => Path.Combine(Locations.AppData, "desktop.log");

    public static void Error(string kind, Exception error)
    {
        var errors = new JsonArray();
        for (var cause = error; cause is not null && errors.Count < 4; cause = cause.InnerException)
        {
            var frames = new StackTrace(cause).GetFrames().Take(12)
                .Select(frame => (JsonNode?)Clean(frame.GetMethod() is { } method ? $"{method.DeclaringType?.FullName}.{method.Name}" : "unknown"));
            errors.Add(new JsonObject { ["type"] = cause.GetType().FullName, ["frames"] = new JsonArray(frames.ToArray()) });
        }
        var entry = Entry(kind);
        entry["errors"] = errors;
        Append(entry);
    }

    public static void Service(string kind, int? exitCode, IEnumerable<string> output)
    {
        var entry = Entry(kind);
        entry["exit_code"] = exitCode;
        entry["output"] = new JsonArray(output.Select(line => (JsonNode?)Clean(line)).ToArray());
        Append(entry);
    }

    /// <summary>The text cut to 500 characters, without the user name and password of any address in it.</summary>
    public static string Clean(string text)
    {
        var cleaned = Credentials.Replace(text, "://");
        return cleaned.Length > TextLimit ? cleaned[..TextLimit] : cleaned;
    }

    static JsonObject Entry(string kind) =>
        new() { ["time"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), ["event"] = kind, ["build"] = App.Version };

    static void Append(JsonObject entry)
    {
        var line = entry.ToJsonString() + "\n";
        // Logging must never end EE, so an entry that cannot be written is dropped.
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Locations.AppData);
                if (System.IO.File.Exists(File) && new FileInfo(File).Length + line.Length > Limit) System.IO.File.Move(File, File + ".1", overwrite: true);
                System.IO.File.AppendAllText(File, line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
