using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core;

/// <summary>
/// The lighting editor state shared across pages, as in preview/app.mjs: the port selection, the unapplied preview
/// (draft), remembered effect settings, and the actions other pages call (choose an effect, apply a profile, undo).
/// The Lighting page provides the implementation; until it is registered, <see cref="Studio.Empty"/> stands in.
/// </summary>
public interface IStudio
{
    /// <summary>Selected port numbers.</summary>
    IReadOnlyCollection<int> Selected { get; }

    /// <summary>Last settings used per effect id ("rememberedEffects").</summary>
    IReadOnlyDictionary<string, JsonObject> RememberedEffects { get; }

    /// <summary>The unapplied preview ("draft").</summary>
    JsonObject Draft { get; }

    /// <summary>Effects page filters and search kept across visits ("galleryState").</summary>
    JsonObject GalleryState { get; }

    /// <summary>A rule to open when Automation is shown next ("requestedRuleId").</summary>
    string? RequestedRuleId { get; set; }

    bool HasEdits { get; }

    bool HasPendingPlan { get; }

    bool CanUndoApply { get; }

    string DraftSummary();

    /// <summary>Asks to keep, discard or apply an unapplied preview. True when it is safe to continue.</summary>
    Task<bool> ConfirmDraft();

    /// <summary>Stages an effect for the given (or selected) ports and opens Lighting.</summary>
    Task ChooseEffect(JsonObject config);

    Task ChooseSfpEffect(JsonObject effect, IReadOnlyList<int> ports);

    /// <summary>Opens Lighting to change the selection.</summary>
    Task ChangeSelection();

    Task ApplyProfile(JsonNode reference);

    /// <summary>The scene the preview would apply ("previewScene").</summary>
    JsonObject? PreviewScene();

    Task SavePreviewProfile(string name);

    Task UndoLastApply();

    /// <summary>Restores saved selection and preview after a switch opens. Returns the page to show, if saved.</summary>
    Task<string?> Restore();

    /// <summary>Clears everything for a different switch ("resetWorkspace").</summary>
    void Reset();

    void SaveUiState(string page);

    /// <summary>State was read again while Lighting is shown.</summary>
    void Refreshed(bool rebuild);
}

public static class Studio
{
    /// <summary>A stand-in with no lighting editor: nothing is selected and nothing is previewed.</summary>
    public static readonly IStudio Empty = new None();

    sealed class None : IStudio
    {
        public IReadOnlyCollection<int> Selected => [];
        public IReadOnlyDictionary<string, JsonObject> RememberedEffects { get; } = new Dictionary<string, JsonObject>();
        public JsonObject Draft { get; } = [];
        public JsonObject GalleryState { get; } = [];
        public string? RequestedRuleId { get; set; }
        public bool HasEdits => false;
        public bool HasPendingPlan => false;
        public bool CanUndoApply => false;
        public string DraftSummary() => "";
        public Task<bool> ConfirmDraft() => Task.FromResult(true);
        public Task ChooseEffect(JsonObject config) => Task.CompletedTask;
        public Task ChooseSfpEffect(JsonObject effect, IReadOnlyList<int> ports) => Task.CompletedTask;
        public Task ChangeSelection() => Task.CompletedTask;
        public Task ApplyProfile(JsonNode reference) => Task.CompletedTask;
        public JsonObject? PreviewScene() => null;
        public Task SavePreviewProfile(string name) => Task.CompletedTask;
        public Task UndoLastApply() => Task.CompletedTask;
        public Task<string?> Restore() => Task.FromResult<string?>(null);
        public void Reset() { }
        public void SaveUiState(string page) { }
        public void Refreshed(bool rebuild) { }
    }
}
