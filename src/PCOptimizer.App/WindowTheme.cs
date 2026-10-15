using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PCOptimizer.App;

/// <summary>Barra de título escura (Windows 10 20H1+ / Windows 11) para combinar com a app.</summary>
public static class WindowTheme
{
    const int DwmwaUseImmersiveDarkMode = 20;
    const int DwmwaCaptionColor = 35;   // só Windows 11
    const int DwmwaBorderColor = 34;    // só Windows 11
    const int PanelColor = 0x00131111;  // #111113 em formato COLORREF (0x00BBGGRR)

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var on = 1;
            var color = PanelColor;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref color, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref color, sizeof(int));
        };
    }
}
