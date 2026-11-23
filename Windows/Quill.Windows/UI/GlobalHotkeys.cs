using System.Runtime.InteropServices;
using System.Windows.Interop;
using Quill.Core.Settings;
using Quill.Windows.Privacy;

namespace Quill.Windows.UI;

/// <summary>
/// Atajos que funcionan sin que quill tenga el foco. Durante una videollamada,
/// ir al icono de la bandeja es justo lo que no puedes hacer: marcar un momento
/// tiene que costar una combinacion de teclas.
/// </summary>
internal sealed class GlobalHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    private const int ToggleId = 0x5101;
    private const int MarkerId = 0x5102;
    private const int CompanionId = 0x5103;

    private readonly HwndSource _source;
    private readonly Action _toggle;
    private readonly Action _marker;
    private bool _disposed;

    private GlobalHotkeys(HwndSource source, Action toggle, Action marker)
    {
        _source = source;
        _toggle = toggle;
        _marker = marker;
        _source.AddHook(OnMessage);
    }

    public bool ToggleRegistered { get; private set; }

    public bool MarkerRegistered { get; private set; }

    public bool CompanionRegistered { get; private set; }

    /// <summary>The companion shortcut currently registered, or null when it is not.</summary>
    public HotkeyGesture? CompanionGesture { get; private set; }

    /// <summary>The show/hide companion panel shortcut was pressed (raised on the UI thread).</summary>
    public event Action? CompanionToggleRequested;

    /// <summary>
    /// Devuelve null si no se pudo crear la ventana oculta que recibe los mensajes.
    /// Los atajos son una comodidad: que fallen no puede impedir arrancar.
    /// </summary>
    public static GlobalHotkeys? TryCreate(Action toggle, Action marker, HotkeyGesture? companion = null)
    {
        HwndSource? source = null;
        try
        {
            source = new HwndSource(new HwndSourceParameters("quill-hotkeys")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
            });

            var hotkeys = new GlobalHotkeys(source, toggle, marker);
            hotkeys.Register();
            if (companion is not null)
            {
                hotkeys.SetCompanionGesture(companion);
            }

            return hotkeys;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            source?.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.RemoveHook(OnMessage);
        if (ToggleRegistered)
        {
            UnregisterHotKey(_source.Handle, ToggleId);
        }

        if (MarkerRegistered)
        {
            UnregisterHotKey(_source.Handle, MarkerId);
        }

        if (CompanionRegistered)
        {
            UnregisterHotKey(_source.Handle, CompanionId);
        }

        _source.Dispose();
    }

    /// <summary>
    /// (Re)registers the show/hide companion panel shortcut. Returns false when another
    /// application owns it; the shortcut then does not exist until the next change.
    /// </summary>
    public bool SetCompanionGesture(HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        if (_disposed)
        {
            return false;
        }

        if (CompanionRegistered && gesture == CompanionGesture)
        {
            return true;
        }

        if (CompanionRegistered)
        {
            UnregisterHotKey(_source.Handle, CompanionId);
            CompanionRegistered = false;
            CompanionGesture = null;
        }

        var (modifiers, key) = HotkeyInterop.ToNative(gesture);
        CompanionRegistered = RegisterHotKey(_source.Handle, CompanionId, modifiers, key);
        CompanionGesture = CompanionRegistered ? gesture : null;
        return CompanionRegistered;
    }

    private void Register()
    {
        // Ctrl+Shift+R para grabar, Ctrl+Shift+M para marcar. Si otra aplicacion ya
        // los tiene, RegisterHotKey falla y el atajo simplemente no existe: el menu
        // de la bandeja sigue haciendo lo mismo.
        ToggleRegistered = RegisterHotKey(_source.Handle, ToggleId, ModControl | ModShift | ModNoRepeat, (uint)'R');
        MarkerRegistered = RegisterHotKey(_source.Handle, MarkerId, ModControl | ModShift | ModNoRepeat, (uint)'M');
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmHotkey)
        {
            return IntPtr.Zero;
        }

        switch (wParam.ToInt32())
        {
            case ToggleId:
                handled = true;
                _toggle();
                break;

            case MarkerId:
                handled = true;
                _marker();
                break;

            case CompanionId:
                handled = true;
                CompanionToggleRequested?.Invoke();
                break;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
