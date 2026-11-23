using Quill.Core.Settings;

namespace Quill.Windows.Privacy;

/// <summary>Maps a <see cref="HotkeyGesture"/> to RegisterHotKey's modifiers and virtual key.</summary>
internal static class HotkeyInterop
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    private const uint VkF1 = 0x70;

    public static (uint Modifiers, uint VirtualKey) ToNative(HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        var modifiers = ModNoRepeat;
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            modifiers |= ModControl;
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            modifiers |= ModAlt;
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            modifiers |= ModShift;
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            modifiers |= ModWin;
        }

        // Letters and digits share their ASCII code with VK_A..VK_Z and VK_0..VK_9.
        var key = gesture.Key.Length == 1
            ? gesture.Key[0]
            : VkF1 + uint.Parse(gesture.Key[1..], System.Globalization.CultureInfo.InvariantCulture) - 1;
        return (modifiers, key);
    }
}
