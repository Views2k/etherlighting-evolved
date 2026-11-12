using EtherlightingEvolved.Native.Ui;

namespace EtherlightingEvolved.Native.Pages.Lighting;

public sealed partial class LightingPage
{
    const string ColorPreferences = ColorLibraryDialog.ColorPreferences;

    void ColorPicker(string label, string start, Action<string> onChange) =>
        ColorLibraryDialog.Open(Shell, label, start, onChange);
}
