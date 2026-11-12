using Avalonia.Controls;
using EtherlightingEvolved.Native.Shell;

namespace EtherlightingEvolved.Native.Ui;

internal class DialogForm
{
    public StackPanel Body { get; } = Ui.Column(12);
    public WrapPanel Buttons { get; } = Ui.Wrap(8);
    public TextBlock Error { get; } = Ui.P("", "error");
    public Dialog Dialog { get; }
    readonly StackPanel root;
    public bool Busy { get; private set; }

    public DialogForm(AppShell shell, string title, double width = 560)
    {
        Error.IsVisible = false;
        root = Ui.Column(12, Ui.H2(title), Body, Error, Buttons);
        Dialog = shell.Overlay.Show(root, width);
        Dialog.Canceling = () => !Busy;
    }

    public void Done(string caption = "Done") => Buttons.Children.Add(Ui.Button(caption, "quiet", () => Dialog.Close()));
    public Button Btn(string label, Func<Task> action, string style = "quiet") => Ui.Button(label, style, () => Do(action));
    public async Task Do(Func<Task> action)
    {
        if (Busy) return;
        Busy = true;
        root.IsEnabled = false;
        Error.IsVisible = false;
        try { await action(); }
        catch (Exception error) { Error.Text = error.Message; Error.IsVisible = true; }
        finally { Busy = false; root.IsEnabled = true; }
    }
}
