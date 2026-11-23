using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Quill.Windows.Privacy;

/// <summary>What <see cref="ScreenCaptureExclusion.Apply"/> achieved for one window.</summary>
internal enum CaptureExclusionResult
{
    /// <summary>No affinity: the window is captured normally.</summary>
    Visible,

    /// <summary>WDA_EXCLUDEFROMCAPTURE: the window is absent from captures (Windows 10 2004+).</summary>
    Excluded,

    /// <summary>WDA_MONITOR fallback: the window shows as a black rectangle in captures.</summary>
    Blacked,

    /// <summary>Both calls failed (invalid handle, or a child window).</summary>
    Failed,
}

/// <summary>
/// Keeps a top-level window out of screen capture (Teams/Zoom/Meet sharing, Win+Shift+S,
/// PrintScreen, OBS) with SetWindowDisplayAffinity. Only top-level windows are affected; the
/// tray icon, its balloons and the taskbar are drawn by the shell and cannot be excluded.
/// </summary>
internal static class ScreenCaptureExclusion
{
    private const uint WdaNone = 0x0;
    private const uint WdaMonitor = 0x1;
    private const uint WdaExcludeFromCapture = 0x11;

    private static bool _fallbackLogged;

    public static CaptureExclusionResult Apply(IntPtr hwnd, bool exclude)
    {
        if (hwnd == IntPtr.Zero)
        {
            return CaptureExclusionResult.Failed;
        }

        if (!exclude)
        {
            return SetWindowDisplayAffinity(hwnd, WdaNone) ? CaptureExclusionResult.Visible : CaptureExclusionResult.Failed;
        }

        if (SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture))
        {
            return CaptureExclusionResult.Excluded;
        }

        // Before Windows 10 2004 the exclude flag is rejected (ERROR_INVALID_PARAMETER).
        var error = Marshal.GetLastWin32Error();
        if (SetWindowDisplayAffinity(hwnd, WdaMonitor))
        {
            if (!_fallbackLogged)
            {
                _fallbackLogged = true;
                Trace.TraceWarning($"quill: WDA_EXCLUDEFROMCAPTURE unavailable (error {error}); windows show black in captures instead.");
            }

            return CaptureExclusionResult.Blacked;
        }

        Trace.TraceWarning($"quill: SetWindowDisplayAffinity failed (error {Marshal.GetLastWin32Error()}).");
        return CaptureExclusionResult.Failed;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
