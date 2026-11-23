using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Quill.Core.Settings;
using Quill.Windows.UI.Settings;
using WpfApplication = System.Windows.Application;
using WpfWindow = System.Windows.Window;

namespace Quill.Windows.Privacy;

/// <summary>
/// Discreet mode ("Modo discreto", <c>privacy.discreet_mode</c>, on by default) keeps Quill
/// out of screen shares:
/// <list type="bullet">
/// <item>Every WPF window and popup (menus, tooltips, dropdowns) is excluded from capture.</item>
/// <item>While a recording is active, every Quill window also leaves the taskbar and Alt+Tab.
/// When no recording is active each window keeps its own ShowInTaskbar, so the sessions
/// window still has its taskbar button between meetings.</item>
/// </list>
/// Windows are picked up from <see cref="UI.Theme.ThemeBootstrapper.Attach"/> (before the
/// first frame) and, defensively, from an app-wide Loaded class handler. Everything here runs
/// on the UI thread.
/// </summary>
internal static class DiscreetMode
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const int WmStyleChanging = 0x007C;
    private const uint SwpFlags = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020; // NOSIZE NOMOVE NOZORDER NOACTIVATE FRAMECHANGED

    private static readonly ConditionalWeakTable<WpfWindow, TrackedWindow> Tracked = new();
    private static PrivacySettings _settings = new();
    private static bool _recording;
    private static bool _initialized;

    /// <summary>Raised on the UI thread after the privacy settings changed.</summary>
    public static event EventHandler? Changed;

    public static PrivacySettings Settings => _settings;

    public static bool IsOn => _settings.DiscreetMode;

    /// <summary>The rule for taskbar buttons and Alt+Tab: hidden only while discreet and recording.</summary>
    public static bool HidesFromTaskbar => IsOn && _recording;

    /// <summary>Call once on the UI thread after the Application exists. Idempotent.</summary>
    public static void Initialize(PrivacySettings settings)
    {
        Update(settings);
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnElementLoaded),
            handledEventsToo: true);
        SettingsModule.Saved += (_, saved) => Update(saved.Privacy);
    }

    /// <summary>Applies new settings to every open window (runtime toggle).</summary>
    public static void Update(PrivacySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings == _settings)
        {
            return;
        }

        _settings = settings;
        ApplyToOpenWindows();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Called by the tray when a recording starts or stops.</summary>
    public static void SetRecording(bool recording)
    {
        if (_recording == recording)
        {
            return;
        }

        _recording = recording;
        ApplyToOpenWindows();
    }

    /// <summary>Starts following <paramref name="window"/>; safe to call more than once.</summary>
    public static void Track(WpfWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        Tracked.GetValue(window, static w => new TrackedWindow(w)).Attach();
    }

    /// <summary>Capture exclusion for a surface that is not a WPF window (popup, tray menu).</summary>
    public static void ProtectSurface(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            ScreenCaptureExclusion.Apply(hwnd, IsOn);
        }
    }

    private static void ApplyToOpenWindows()
    {
        var application = WpfApplication.Current;
        if (application is null)
        {
            return;
        }

        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.BeginInvoke(ApplyToOpenWindows);
            return;
        }

        foreach (WpfWindow window in application.Windows)
        {
            Track(window);
        }
    }

    private static void OnElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is WpfWindow window)
        {
            Track(window);
            return;
        }

        // Popups (menus, tooltips, ComboBox dropdowns) are separate top-level HWNDs whose
        // root element has no visual parent.
        if (sender is FrameworkElement element
            && VisualTreeHelper.GetParent(element) is null
            && PresentationSource.FromVisual(element) is HwndSource source
            && ReferenceEquals(source.RootVisual, element))
        {
            ProtectSurface(source.Handle);
        }
    }

    private sealed class TrackedWindow
    {
        private readonly WpfWindow _window;
        private bool _hooked;
        private bool _hidden;
        private bool _originalShowInTaskbar;
        private int _originalExStyleBits;

        public TrackedWindow(WpfWindow window) => _window = window;

        public void Attach()
        {
            if (new WindowInteropHelper(_window).Handle == IntPtr.Zero)
            {
                // Before the HWND exists: apply as soon as it does, before the first frame.
                _window.SourceInitialized -= OnSourceInitialized;
                _window.SourceInitialized += OnSourceInitialized;
                return;
            }

            Apply();
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _window.SourceInitialized -= OnSourceInitialized;
            Apply();
        }

        private void Apply()
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            ScreenCaptureExclusion.Apply(hwnd, IsOn);
            SetHiddenFromTaskbar(hwnd, HidesFromTaskbar);
        }

        private void SetHiddenFromTaskbar(IntPtr hwnd, bool hide)
        {
            if (hide == _hidden)
            {
                return;
            }

            if (hide)
            {
                if (!_hooked && HwndSource.FromHwnd(hwnd) is { } source)
                {
                    // WPF rewrites the extended style when some properties change; keep the
                    // tool-window bit while hidden so the window stays out of Alt+Tab.
                    source.AddHook(OnMessage);
                    _hooked = true;
                }

                _originalShowInTaskbar = _window.ShowInTaskbar;
                _originalExStyleBits = (int)GetWindowLongPtr(hwnd, GwlExStyle) & (WsExToolWindow | WsExAppWindow);
                _hidden = true;
                _window.ShowInTaskbar = false;
                SetExStyle(hwnd, Hidden((int)GetWindowLongPtr(hwnd, GwlExStyle)));
            }
            else
            {
                _hidden = false;
                var style = (int)GetWindowLongPtr(hwnd, GwlExStyle) & ~(WsExToolWindow | WsExAppWindow);
                SetExStyle(hwnd, style | _originalExStyleBits);
                _window.ShowInTaskbar = _originalShowInTaskbar;
            }
        }

        private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_hidden && message == WmStyleChanging && wParam.ToInt32() == GwlExStyle && lParam != IntPtr.Zero)
            {
                var change = Marshal.PtrToStructure<StyleStruct>(lParam);
                var desired = (uint)Hidden((int)change.StyleNew);
                if (desired != change.StyleNew)
                {
                    change.StyleNew = desired;
                    Marshal.StructureToPtr(change, lParam, fDeleteOld: false);
                }
            }

            return IntPtr.Zero;
        }

        private static int Hidden(int style) => (style | WsExToolWindow) & ~WsExAppWindow;

        private static void SetExStyle(IntPtr hwnd, int style)
        {
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpFlags);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StyleStruct
    {
        public uint StyleOld;
        public uint StyleNew;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
