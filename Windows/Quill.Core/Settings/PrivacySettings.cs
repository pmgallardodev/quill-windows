namespace Quill.Core.Settings;

/// <summary>
/// config.json "privacy" section: discreet mode keeps Quill out of screen shares (window capture
/// exclusion, no taskbar buttons while recording, no OS notifications, neutral tray icon).
/// </summary>
public sealed record PrivacySettings
{
    /// <summary><c>discreet_mode</c>: on by default.</summary>
    public bool DiscreetMode { get; init; } = true;

    /// <summary><c>neutral_tray_icon</c>: the tray icon and tooltip do not reveal a recording.
    /// Only applies while <see cref="DiscreetMode"/> is on.</summary>
    public bool NeutralTrayIcon { get; init; } = true;

    /// <summary><c>companion_hotkey</c>: global shortcut that shows or hides the companion panel,
    /// always stored in the canonical form of <see cref="HotkeyGesture.ToString"/>.</summary>
    public string CompanionHotkey { get; init; } = HotkeyGesture.DefaultCompanion.ToString();

    public bool UsesNeutralTrayIcon => DiscreetMode && NeutralTrayIcon;

    /// <summary>The parsed <see cref="CompanionHotkey"/>, or the default when it is not valid.</summary>
    public HotkeyGesture CompanionGesture =>
        HotkeyGesture.TryParse(CompanionHotkey, out var gesture) ? gesture! : HotkeyGesture.DefaultCompanion;
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// A global shortcut such as "Ctrl+Shift+Q": at least one of Ctrl, Alt or Win, plus one key
/// (A-Z, 0-9 or F1-F24). Shortcuts Quill already uses for recording are rejected.
/// </summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    public static readonly HotkeyGesture DefaultCompanion = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, "Q");

    /// <summary>Start/stop recording and add a marker (GlobalHotkeys).</summary>
    public static readonly IReadOnlyList<HotkeyGesture> Reserved =
    [
        new(HotkeyModifiers.Control | HotkeyModifiers.Shift, "R"),
        new(HotkeyModifiers.Control | HotkeyModifiers.Shift, "M"),
    ];

    public static bool TryParse(string? text, out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Control,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" => HotkeyModifiers.Windows,
                _ => HotkeyModifiers.None,
            };
            if (modifier == HotkeyModifiers.None || modifiers.HasFlag(modifier))
            {
                return false;
            }

            modifiers |= modifier;
        }

        var key = parts[^1].ToUpperInvariant();
        if ((modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows)) == 0 && !IsFunctionKey(key))
        {
            return false;
        }

        if (modifiers == HotkeyModifiers.None || !IsKey(key))
        {
            return false;
        }

        var candidate = new HotkeyGesture(modifiers, key);
        if (Reserved.Contains(candidate))
        {
            return false;
        }

        gesture = candidate;
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(Key);
        return string.Join('+', parts);
    }

    private static bool IsKey(string key) =>
        (key.Length == 1 && key[0] is (>= 'A' and <= 'Z') or (>= '0' and <= '9')) || IsFunctionKey(key);

    private static bool IsFunctionKey(string key) =>
        key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key[1..], out var number) && number is >= 1 and <= 24
        && key[1] != '0';
}
