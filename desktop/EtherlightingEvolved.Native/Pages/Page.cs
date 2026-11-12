using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Shell;

namespace EtherlightingEvolved.Native.Pages;

/// <summary>
/// One workspace page (Lighting, Effects, Automation, Profiles, Settings, Help) or the switch connection view.
/// A port of the browser workspace's page renderers: the shell creates a page when it is shown, calls Update when
/// state changes (the renderer's update), asks CanLeave before navigating away, and calls Leave when it goes.
/// </summary>
public abstract class Page : UserControl
{
    protected Page(AppShell shell)
    {
        Shell = shell;
        Focusable = false;
    }

    public AppShell Shell { get; }

    public Workspace Workspace => Shell.Workspace;

    public IStudio Studio => Shell.Studio;

    /// <summary>The page fills the view without the 24px padding and scrolling secondary pages use.</summary>
    public virtual bool FullBleed => false;

    /// <summary>False keeps the user on the page (unsaved edits the user chose to keep).</summary>
    public virtual Task<bool> CanLeave() => Task.FromResult(true);

    /// <summary>True when leaving would lose unsaved edits (the renderer's hasChanges).</summary>
    public virtual bool HasChanges => false;

    /// <summary>State, session or the connection changed.</summary>
    public virtual void Update() { }

    /// <summary>A live frame arrived. Pages that show live values override this; it runs often.</summary>
    public virtual void Frame() { }

    /// <summary>The page is being removed (the renderer's cleanup).</summary>
    public virtual void Leave() { }
}

/// <summary>Page factories. Each page file registers itself with a module initializer.</summary>
public static class PageRegistry
{
    static readonly Dictionary<string, Func<AppShell, Page>> Factories = new(StringComparer.Ordinal);

    public static void Register(string name, Func<AppShell, Page> factory) => Factories[name] = factory;

    public static bool Has(string name) => Factories.ContainsKey(name);

    public static Page? Create(string name, AppShell shell) => Factories.TryGetValue(name, out var factory) ? factory(shell) : null;

    /// <summary>The Lighting page registers its lighting editor here so other pages and the shell can reach it.</summary>
    public static Func<AppShell, IStudio>? StudioFactory { get; set; }

    /// <summary>
    /// The connection view's scene choice (showSceneChoice): when a session must choose between the saved and the
    /// switch's current setup, it asks and returns the session to continue with, or null to stay.
    /// </summary>
    public static Func<AppShell, JsonObject, Task<JsonObject?>>? SceneChoice { get; set; }

    /// <summary>The connection view's handoff message for a signed-out session (handoffStatus).</summary>
    public static Func<JsonObject, (string Message, bool Error)?>? HandoffStatus { get; set; }
}

/// <summary>Shown for a page whose port is not built into this copy.</summary>
public sealed class MissingPage : Page
{
    public MissingPage(AppShell shell, string name) : base(shell) =>
        Content = Ui.Ui.EmptyState(name, "This page is not available in this build.");
}
