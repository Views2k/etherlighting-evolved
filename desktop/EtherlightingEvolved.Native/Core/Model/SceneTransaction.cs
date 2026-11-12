// Port of preview/scene-transaction.mjs: applying a whole scene as one versioned request, and reconciling a reply that
// was lost after the engine may have accepted the change.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

public static class SceneTransaction
{
    static readonly ConditionalWeakTable<Exception, JsonNode> Accepted = new();

    /// <summary>error.acceptedScene: the current scene when a failed commit turned out to be accepted, otherwise null.</summary>
    public static JsonNode? AcceptedScene(Exception error) => Accepted.TryGetValue(error, out var scene) ? scene : null;

    static bool Uncertain(Exception error) =>
        error is ApiException api ? api.Uncertain : error.Data["uncertain"] is true;

    static string? Code(Exception error) =>
        error is ApiException api ? api.Code : error.Data["code"] as string;

    /// <summary>
    /// commitScene(before, scene, send, {preserveUnchanged = true}): PUT /api/scene with the expected revision. When the
    /// outcome is uncertain, or the engine reports output_unconfirmed or scene_changed, the current scene is read; if it
    /// matches the requested one it is attached to the error, which is rethrown either way.
    /// </summary>
    public static async Task<JsonNode?> CommitScene(JsonNode? before, JsonNode? scene, Func<string, string, JsonNode?, Task<JsonNode?>> send, bool preserveUnchanged = true)
    {
        try
        {
            return await send("/api/scene", "PUT", new JsonObject
            {
                ["scene"] = Js.Clone(scene), ["expected_revision"] = Js.Clone(Js.Get(before, "revision")),
                ["preserve_unchanged"] = preserveUnchanged, ["confirm_output"] = true,
            });
        }
        catch (Exception error)
        {
            // A lost reply does not mean the accepted configuration was rolled back.
            if (Uncertain(error) || Code(error) is "output_unconfirmed" or "scene_changed")
            {
                try
                {
                    var current = await send("/api/scene", "GET", null);
                    if (SecondaryModel.SceneFingerprint(Js.Get(current, "scene")) == SecondaryModel.SceneFingerprint(scene))
                        Accepted.AddOrUpdate(error, current!);
                }
                catch (Exception)
                {
                    // Keep the original confirmation error.
                }
            }
            throw;
        }
    }
}
