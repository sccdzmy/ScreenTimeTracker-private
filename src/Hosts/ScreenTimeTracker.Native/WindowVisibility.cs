using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
namespace ScreenTimeTracker.Native;
internal static partial class WindowVisibility
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out NativeRect rect);
    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromRect(ref NativeRect rect, uint flags);
    public static void EnsureVisible(Window window)
    {
        if (!window.IsLoaded || window.WindowState != WindowState.Normal) return;
        if (!GetWindowRect(new WindowInteropHelper(window).Handle, out var rect)) return;
        rect.Bottom = rect.Top + 32; // Require a reachable title bar, including gaps between monitors.
        if (MonitorFromRect(ref rect, 0) != 0) return;
        window.Left = SystemParameters.WorkArea.Left + Math.Max(0, (SystemParameters.WorkArea.Width - window.ActualWidth) / 2);
        window.Top = SystemParameters.WorkArea.Top + Math.Max(0, (SystemParameters.WorkArea.Height - window.ActualHeight) / 2);
    }
}
