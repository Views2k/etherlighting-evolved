using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Theme;
using EtherlightingEvolved.Native.Ui;
using static EtherlightingEvolved.Native.Pages.Connect.Connections;

namespace EtherlightingEvolved.Native.Pages.Connect;

/// <summary>
/// The lighting choice for a new switch connection, a port of showSceneChoice (preview/connections.mjs). When a
/// session waits for a choice between the saved lighting and a new setup, a dialog asks; the task returns the
/// session to continue with, or null to stay. Only one dialog is open at a time: asking again updates it.
/// </summary>
public static class SceneChoice
{
    sealed record OpenDialog(Task<JsonObject?> Promise, Action<JsonObject?> Update, Func<bool> Alive, Action Abort);

    static OpenDialog? open;

    /// <summary>A lighting choice dialog is open.</summary>
    public static bool IsOpen => open is not null;

    public static Task<JsonObject?> Show(Overlay overlay, Func<string, string, JsonNode?, Task<JsonNode?>> request, JsonObject? session, CancellationToken signal = default)
    {
        if (open is { } existing)
        {
            if (existing.Alive())
            {
                existing.Update(session);
                return existing.Promise;
            }
            // Its dialog was closed with every other dialog; end it before asking again.
            existing.Abort();
        }
        if (HandoffBlocked(session)) return Task.FromResult(session);
        if (!session.Truthy("pending_scene_choice")) return Task.FromResult(session);
        if (signal.IsCancellationRequested) return Task.FromResult<JsonObject?>(null);
        var previousFocus = TopLevel.GetTopLevel(overlay)?.FocusManager?.GetFocusedElement() as Control;
        var hadSession = session.Bool("logged_in") == true;
        var previousWorkspace = session.Str("workspace_id");
        JsonObject? current = session;
        bool busy = false, disposed = false, expired = false, invalidSaved = false;
        var recoveryRequired = session.Str("error.code") == "output_recovery_required";
        var done = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var heading = new TextBlock { Text = "Choose lighting for this switch", Classes = { "h2" }, FontSize = 18, LineHeight = 26, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        var intro = ConnectionUi.HelpText("Choose which lighting to start on this switch.");
        intro.Margin = default;
        var useSaved = ConnectionUi.Button("Use saved lighting", "primary");
        var startNew = ConnectionUi.Button("Start new setup");
        useSaved.HorizontalAlignment = startNew.HorizontalAlignment = HorizontalAlignment.Stretch;
        var savedSection = new StackPanel { Spacing = 7, Children = { useSaved, ConnectionUi.Small("Start with the saved colors, effects, and automations.") } };
        var newSection = new StackPanel { Spacing = 7, Children = { startNew, ConnectionUi.Small("Start with the default lighting for this switch.") } };
        var choices = new StackPanel { Spacing = 18, Margin = new Thickness(0, 22, 0, 0), Children = { savedSection, newSection } };
        var message = new Notice();
        var cancel = ConnectionUi.Button("Cancel", "quiet");
        var footer = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 15, 0, 0), Margin = new Thickness(0, 20, 0, 0),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel } },
        }.Bind(Border.BorderBrushProperty, "line");
        var content = new StackPanel { Children = { heading, intro, choices, message, footer } };
        Avalonia.Automation.AutomationProperties.SetName(content, "Choose lighting for this switch");
        Dialog? dialog = null;
        CancellationTokenRegistration abortRegistration = default;

        void Status(string text = "", bool error = false) => message.Set(text, error);

        void Render()
        {
            var choice = current.At("pending_scene_choice");
            savedSection.IsVisible = choice.Bool("legacy_available") == true;
            useSaved.IsEnabled = !(busy || expired || invalidSaved || recoveryRequired || !choice.Truthy("legacy_available"));
            startNew.IsEnabled = !(busy || expired || recoveryRequired);
            cancel.IsEnabled = !busy;
            cancel.Content = expired ? "Back to connection" : "Cancel";
        }

        void Finish(JsonObject? value)
        {
            if (disposed) return;
            disposed = true;
            abortRegistration.Dispose();
            if (open?.Promise == done.Task) open = null;
            dialog?.Close("done");
            if (previousFocus is not null && TopLevel.GetTopLevel(previousFocus) is not null) previousFocus.Focus();
            done.TrySetResult(value);
        }

        void Update(JsonObject? value)
        {
            if (disposed || value is null) return;
            var old = current.At("pending_scene_choice");
            current = value;
            if (HandoffBlocked(current))
            {
                Finish(current);
                return;
            }
            var next = current.At("pending_scene_choice");
            if (!next.Truthy())
            {
                if (current.Truthy("logged_in") && (!hadSession || current.Str("workspace_id") != previousWorkspace))
                {
                    Finish(current);
                    return;
                }
                expired = true;
                Status("This connection is no longer waiting for a lighting choice. Return to the connection screen to continue.", true);
            }
            else if (!Json.Same(old.At("connection_id"), next.At("connection_id")) || !Json.Same(old.At("legacy_revision"), next.At("legacy_revision")))
            {
                expired = false;
                invalidSaved = false;
                recoveryRequired = false;
                Status("The connection or saved lighting changed. Review these choices before continuing.");
            }
            if (current.Str("error.code") == "output_recovery_required")
            {
                recoveryRequired = true;
                Status(Messages["output_recovery_required"], true);
            }
            Render();
        }

        async Task Choose(string choice)
        {
            if (disposed || busy) return;
            if (expired)
            {
                if (choice == "cancel") Finish(current);
                return;
            }
            var pending = current.At("pending_scene_choice");
            if (!pending.Truthy()) return;
            if (choice == "use_saved" && (!pending.Truthy("legacy_available") || invalidSaved || recoveryRequired)) return;
            if (choice == "start_new" && recoveryRequired) return;
            var payload = new JsonObject
            {
                ["connection_id"] = pending.At("connection_id")?.DeepClone(), ["choice"] = choice, ["legacy_revision"] = pending.At("legacy_revision")?.DeepClone(),
            };
            bool ConnectionChanged() => current.Truthy("pending_scene_choice") && !Json.Same(current.At("pending_scene_choice.connection_id"), payload["connection_id"]);
            busy = true;
            Render();
            Status(choice == "cancel" ? "Canceling lighting setup…" : "Starting selected lighting…");
            try
            {
                var result = await request("/api/session/scene-choice", "POST", payload) as JsonObject;
                if (disposed) return;
                if (ConnectionChanged())
                {
                    Status("The connection changed. Review these choices before continuing.");
                    return;
                }
                if (result is not null && !result.Truthy("pending_scene_choice") && (choice == "cancel" || result.Truthy("logged_in")))
                {
                    Finish(result);
                    return;
                }
                Update(result);
                Status("The lighting choice could not be confirmed. Review the connection and try again.", true);
            }
            catch (Exception error)
            {
                if (disposed) return;
                if (ConnectionChanged())
                {
                    Status("The connection changed. Review these choices before continuing.");
                    return;
                }
                var text = new Dictionary<string, string>
                {
                    ["scene_choice_expired"] = "This connection expired. Connect again to choose its lighting.",
                    ["scene_choice_stale"] = "Saved lighting changed. Review the updated choices before continuing.",
                    ["saved_scene_unavailable"] = "Saved lighting is no longer available. Start a new setup or cancel.",
                    ["invalid_saved_scene"] = "Saved lighting could not be used. Start a new setup or cancel.",
                    ["output_recovery_required"] = Messages["output_recovery_required"],
                };
                var code = (error as ApiException)?.Code ?? (error as CodedException)?.Code ?? "";
                if (code == "invalid_saved_scene") invalidSaved = true;
                if (code == "output_recovery_required") recoveryRequired = true;
                try
                {
                    var latest = await request("/api/session", "GET", null) as JsonObject;
                    if (disposed) return;
                    if (latest.Truthy("logged_in") && !latest.Truthy("pending_scene_choice") && choice != "cancel" &&
                        (!hadSession || latest.Str("workspace_id") != previousWorkspace))
                    {
                        Finish(latest);
                        return;
                    }
                    Update(latest);
                }
                catch (Exception) { }
                if (disposed) return;
                if (code == "scene_choice_expired" && (!current.Truthy("pending_scene_choice") ||
                                                       Json.Same(current.At("pending_scene_choice.connection_id"), payload["connection_id"])))
                    expired = true;
                if (code == "output_recovery_required") recoveryRequired = true;
                Status(text.TryGetValue(code, out var known) ? known : "The lighting choice could not be confirmed. Check the connection and try again.", true);
            }
            finally
            {
                busy = false;
                if (!disposed)
                {
                    Render();
                    cancel.Focus();
                }
            }
        }

        useSaved.Click += (_, _) => _ = Choose("use_saved");
        startNew.Click += (_, _) => _ = Choose("start_new");
        cancel.Click += (_, _) => _ = Choose("cancel");
        open = new OpenDialog(done.Task, Update, () => dialog?.IsOpen != false, () => Finish(null));
        Render();
        dialog = overlay.Show(content);
        // Escape asks to cancel the choice instead of closing the dialog.
        dialog.Canceling = () =>
        {
            _ = Choose("cancel");
            return false;
        };
        // Closing every dialog (a workspace reset) ends the choice without a session, as an abort does.
        Watch(dialog, () => Finish(null));
        Dispatcher.UIThread.Post(() => cancel.Focus(), DispatcherPriority.Loaded);
        if (recoveryRequired) Status(Messages["output_recovery_required"], true);
        abortRegistration = signal.Register(() => Dispatcher.UIThread.Post(() => Finish(null)));
        return done.Task;
    }

    static async void Watch(Dialog dialog, Action closed)
    {
        await dialog.Result;
        closed();
    }
}
