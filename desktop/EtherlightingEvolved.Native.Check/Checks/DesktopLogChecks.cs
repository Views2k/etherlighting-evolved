using System.Text.Json.Nodes;
using EtherlightingEvolved.Native.Core;

namespace EtherlightingEvolved.Native.Check.Checks;

internal static class DesktopLogChecks
{
    public static void Run(Action<bool, string> check)
    {
        Directory.CreateDirectory(Locations.AppData);
        File.Delete(DesktopLog.File);
        File.Delete(DesktopLog.File + ".1");
        try { throw new InvalidOperationException("Could not reach 10.0.0.2 as admin."); }
        catch (InvalidOperationException error) { DesktopLog.Error("check_error", error); }
        var entry = JsonNode.Parse(File.ReadLines(DesktopLog.File).Last());
        check(entry.Str("event") == "check_error" && entry.Str("build") == App.Version && entry.Str("errors.0.type") == "System.InvalidOperationException"
              && entry.Arr("errors.0.frames").Count > 0 && !File.ReadAllText(DesktopLog.File).Contains("10.0.0.2"),
            "desktop.log records an error's type and code locations without its message");
        check(DesktopLog.Clean("GET https://name:secret@host.example.test/api failed") == "GET https://host.example.test/api failed"
              && DesktopLog.Clean(new string('x', 900)).Length == 500,
            "service output in desktop.log loses address credentials and is cut to 500 characters");
        File.WriteAllText(DesktopLog.File, new string('x', 256 * 1024));
        DesktopLog.Service("check_service", 1, ["line"]);
        check(new FileInfo(DesktopLog.File + ".1").Length == 256 * 1024 && File.ReadAllLines(DesktopLog.File) is [var line] && line.Contains("check_service"),
            "desktop.log moves to desktop.log.1 when it passes 256 KB");
    }
}
