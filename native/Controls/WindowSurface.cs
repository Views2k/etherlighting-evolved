using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EtherlightingEvolved.Controls;

internal static class WindowSurface
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var dark = 1;
            var rounded = 2;
            var border = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
            DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        };
    }
}
