using System.Text.Json.Nodes;
using EtherlightingEvolved.Native.Core;

namespace EtherlightingEvolved.Native.Check.Checks;

/// <summary>
/// The app's own settings files. Preferences reads its file once per run, so these checks run before anything else
/// reads it.
/// </summary>
internal static class PreferencesChecks
{
    public static void Run(Action<bool, string> check)
    {
        Directory.CreateDirectory(Locations.AppData);
        const string damaged = "{\"ee.colors.v1\":";
        var earlier = Preferences.File + ".20260101T000000Z.unreadable";
        File.WriteAllText(earlier, "earlier");
        File.WriteAllText(Preferences.File, damaged);
        bool missing, refused = false;
        using (new FileStream(Preferences.File, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            missing = Preferences.GetItem("ee.check.v1") is null;
            try { Preferences.SetItem("ee.check.v1", "saved"); }
            catch (IOException) { refused = true; }
        }
        check(missing && refused && File.ReadAllText(Preferences.File) == damaged && SetAside().SequenceEqual([earlier]),
            "a preferences file another program holds open is neither set aside nor replaced");
        Preferences.SetItem("ee.check.v1", "saved");
        var copies = SetAside();
        check(copies.Count == 2 && File.ReadAllText(earlier) == "earlier" && File.ReadAllText(copies.Single(copy => copy != earlier)) == damaged
              && Preferences.GetItem("ee.check.v1") == "saved" && !Directory.EnumerateFiles(Locations.AppData, "*.tmp").Any(),
            "a preferences file that cannot be parsed is set aside before the next save, keeping copies set aside earlier");
        foreach (var copy in copies) File.Delete(copy);
        var normal = App.Options;
        try
        {
            App.Options = normal with { Demo = true };
            check(Preferences.File == Path.Combine(Locations.Workspace(true), "preferences.json"), "a demo copy keeps its preferences apart from the installed app");
        }
        finally { App.Options = normal; }

        File.WriteAllText(Locations.SettingsFile, "{\"workspace_url\":\"https://host.example.test:8443/\",\"window\":{\"width\":900}}");
        var saved = Locations.SavedWorkspace;
        Locations.SavedWorkspace = new Uri("https://other.example.test:8443/");
        var written = JsonNode.Parse(File.ReadAllText(Locations.SettingsFile));
        check(saved?.Host == "host.example.test" && written.Int("format") == 1 && written.Str("workspace_url") == "https://other.example.test:8443/"
              && written.Int("window.width") == 900 && !Directory.EnumerateFiles(Locations.AppData, "*.tmp").Any(),
            "native.json is read in its earlier form and saved with a format version, keeping its other settings");
        File.WriteAllText(Locations.SettingsFile, "{\"workspace_url\":\"http://example.test/\"}");
        check(Locations.SavedWorkspace is null, "a saved address the address dialog would refuse is not used");
        File.Delete(Locations.SettingsFile);
    }

    static List<string> SetAside() => Directory.EnumerateFiles(Path.GetDirectoryName(Preferences.File)!, "preferences.json.*.unreadable").Order().ToList();
}
