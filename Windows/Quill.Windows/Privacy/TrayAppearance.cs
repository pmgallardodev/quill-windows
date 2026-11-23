namespace Quill.Windows.Privacy;

/// <summary>
/// Tray icon rules. The shell draws the tray icon, so it stays visible in a full-screen share;
/// with a neutral tray icon (discreet mode) nothing in it reveals that a recording is running.
/// </summary>
internal static class TrayAppearance
{
    public const string NeutralTooltip = "Quill";

    /// <summary>Whether to swap in the icon with the red recording dot.</summary>
    public static bool ShowsRecordingIcon(bool recording, bool neutral) => recording && !neutral;

    /// <summary>The tooltip to show for <paramref name="text"/>: neutral while recording in neutral mode.</summary>
    public static string Tooltip(string text, bool recordingActive, bool neutral) =>
        recordingActive && neutral ? NeutralTooltip : text;
}
