using System.Diagnostics;
using System.Text.Json;

namespace Quill.Windows.UI.Companion;

/// <summary>A rectangle in device-independent pixels (no WPF types, so it is unit tested).</summary>
internal readonly record struct PanelRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;

    public bool Contains(double x, double y) => x >= Left && x < Right && y >= Top && y < Bottom;
}

/// <summary>Where the panel goes on screen.</summary>
internal static class CompanionPlacement
{
    /// <summary>Gap between the panel and the work area edges.</summary>
    public const double Margin = 16;

    /// <summary>Top-right corner of <paramref name="workArea"/>, inside the margin.</summary>
    public static (double Left, double Top) DockRight(PanelRect workArea, double width)
    {
        var left = workArea.Right - width - Margin;
        return (Math.Max(workArea.Left, left), workArea.Top + Margin);
    }

    /// <summary>Keeps a panel of the given size fully inside <paramref name="workArea"/>.</summary>
    public static (double Left, double Top) Clamp(double left, double top, double width, double height, PanelRect workArea)
    {
        var maxLeft = Math.Max(workArea.Left, workArea.Right - width);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - height);
        return (Math.Clamp(left, workArea.Left, maxLeft), Math.Clamp(top, workArea.Top, maxTop));
    }

    /// <summary>New left edge that keeps the right edge in place when the width changes (pill ↔ panel).</summary>
    public static double KeepRightEdge(double left, double oldWidth, double newWidth) => left + oldWidth - newWidth;
}

/// <summary>Remembered panel position and mode.</summary>
internal sealed record CompanionPanelState(double Left, double Top, bool Collapsed);

/// <summary>
/// Persists <see cref="CompanionPanelState"/> in %LOCALAPPDATA%\quill\companion-panel.json, next to
/// config.json but separate from it (settings own that file). Failures are logged and ignored:
/// a lost position only means the panel docks to the right again.
/// </summary>
internal sealed class CompanionPlacementStore(string path)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "quill",
        "companion-panel.json");

    public string FilePath { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public CompanionPanelState? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<CompanionPanelState>(File.ReadAllText(FilePath));
            return state is not null && double.IsFinite(state.Left) && double.IsFinite(state.Top) ? state : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Trace.TraceWarning($"quill: companion panel position not read: {exception.Message}");
            return null;
        }
    }

    public void Save(CompanionPanelState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Trace.TraceWarning($"quill: companion panel position not saved: {exception.Message}");
        }
    }
}
