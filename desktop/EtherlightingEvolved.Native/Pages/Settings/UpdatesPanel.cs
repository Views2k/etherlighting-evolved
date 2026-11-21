using Avalonia.Controls;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Theme;
using EtherlightingEvolved.Native.Ui;
using UiKit = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Settings;

/// <summary>
/// Updates under About, in the desktop program installed by setup on Windows: the automatic update setting, what
/// the updater is doing, Check for updates, and Install now once a newer release is found.
/// </summary>
public sealed class UpdatesPanel : StackPanel
{
    public const string Help = "EE checks the GitHub releases every hour. A new version installs after 10 minutes without input in this window, then EE opens again where it was.";
    readonly Updates updates;
    readonly CheckRow automatic = new("Install updates automatically");
    readonly TextBlock status = UiKit.HelpText();
    readonly Button check, install;

    public UpdatesPanel(Updates updates)
    {
        this.updates = updates;
        Spacing = 8;
        Children.Add(SettingsKit.SectionHeading("Updates"));
        automatic.Box.IsChecked = updates.Automatic;
        automatic.Box.IsCheckedChanged += (_, _) =>
        {
            var wanted = automatic.Box.IsChecked == true;
            if (wanted == updates.Automatic) return;
            try { updates.Automatic = wanted; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                automatic.Box.IsChecked = updates.Automatic;
                status.Text = "The update setting could not be saved. Try again.";
            }
        };
        Children.Add(automatic);
        Children.Add(UiKit.HelpText(Help));
        Children.Add(status);
        check = SettingsKit.ActionButton("Check for updates", updates.CheckNow, "quiet", () => !Busy);
        install = SettingsKit.ActionButton("Install now", updates.InstallNow, enabled: () => !Busy);
        Children.Add(new FlowRow(10, check, install));
        Render();
        AttachedToVisualTree += (_, _) =>
        {
            updates.Changed += Changed;
            Render();
        };
        DetachedFromVisualTree += (_, _) => updates.Changed -= Changed;
    }

    bool Busy => updates.Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Installing;

    internal string StatusText => status.Text ?? "";

    internal Button CheckButton => check;

    internal Button InstallButton => install;

    internal CheckBox AutomaticBox => automatic.Box;

    void Changed()
    {
        if (Dispatcher.UIThread.CheckAccess()) Render();
        else Dispatcher.UIThread.Post(Render);
    }

    void Render()
    {
        status.Text = updates.Describe();
        status.Bind(TextBlock.ForegroundProperty, updates.Stage == UpdateStage.Failed ? "danger" : "muted");
        check.IsEnabled = !Busy;
        install.IsVisible = updates.Release is not null && updates.Stage is UpdateStage.Available or UpdateStage.Ready or UpdateStage.Failed;
        install.SetLabel($"Install EE {updates.Release?.Label} now");
        install.IsEnabled = !Busy;
    }
}
