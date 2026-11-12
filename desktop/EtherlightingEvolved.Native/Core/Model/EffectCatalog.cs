// Port of src/catalog.mjs: the effect catalog bundled with the browser workspace. The module is embedded as it is and
// its array literal (plain JSON) is parsed once.
using System.Reflection;
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

public static class EffectCatalog
{
    static readonly Lazy<JsonArray> Bundled = new(Load);

    /// <summary>CATALOG: the bundled effect definitions. Shared; callers copy it before changing anything.</summary>
    public static JsonArray Catalog => Bundled.Value;

    static JsonArray Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("catalog.mjs")
                           ?? throw new InvalidOperationException("The catalog.mjs resource is missing.");
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();
        var start = source.IndexOf('[');
        var end = source.LastIndexOf(']');
        if (start < 0 || end < start) throw new InvalidOperationException("The bundled effect catalog could not be read.");
        return JsonNode.Parse(source[start..(end + 1)]) as JsonArray
               ?? throw new InvalidOperationException("The bundled effect catalog could not be read.");
    }
}
