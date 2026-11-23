using System.Diagnostics;
using System.Windows.Threading;

namespace Quill.Windows.UI.Companion;

/// <summary>
/// Composition entry point of the companion panel. The host calls <see cref="Show"/> when a
/// recording starts and <see cref="Close"/> (or <see cref="CloseAsync"/>) when it stops. Both
/// may be called from any thread; the work runs on the UI dispatcher. The panel never touches
/// capture: it only invokes the callbacks in <see cref="CompanionContext"/>.
/// </summary>
internal sealed class CompanionPanelModule
{
    private readonly Dispatcher _dispatcher;
    private readonly CompanionPlacementStore _placement;
    private CompanionWindow? _window;
    private CompanionViewModel? _viewModel;
    private LiveSummaryLoopFeed? _feed;

    public CompanionPanelModule(Dispatcher dispatcher, CompanionPlacementStore? placement = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _placement = placement ?? new CompanionPlacementStore(CompanionPlacementStore.DefaultPath);
    }

    /// <summary>Whether a panel is open (read on the UI thread).</summary>
    public bool IsOpen => _window is not null;

    /// <summary>Opens the panel for a recording, replacing any open one. Does not take focus.</summary>
    public void Show(CompanionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => Show(context));
            return;
        }

        if (_window is not null)
        {
            _ = ReplaceAsync(context);
            return;
        }

        Open(context);
    }

    private void Open(CompanionContext context)
    {
        try
        {
            _feed = context.LiveSummary is { } loop ? new LiveSummaryLoopFeed(loop) : null;
            _viewModel = new CompanionViewModel(
                context,
                _feed,
                action => _dispatcher.BeginInvoke(action),
                text => System.Windows.Clipboard.SetText(text));
            _window = new CompanionWindow(_viewModel, _placement);
            _window.Show();
            _ = _viewModel.InitializeAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The panel is a convenience: a failure here must never affect the recording.
            Trace.TraceWarning($"quill: companion panel not shown: {exception.Message}");
            _ = CloseAsync();
        }
    }

    private async Task ReplaceAsync(CompanionContext context)
    {
        await CloseAsync().ConfigureAwait(true);
        if (_window is null)
        {
            Open(context);
        }
    }

    /// <summary>
    /// Hides the open panel, or shows it again without taking focus (companion hotkey). Does
    /// nothing when no panel is open. May be called from any thread.
    /// </summary>
    public void ToggleVisibility()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(ToggleVisibility);
            return;
        }

        if (_window is not { } window)
        {
            return;
        }

        if (window.IsVisible)
        {
            window.Hide();
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>
    /// Shows a notification inside the open panel (the panel is excluded from screen capture,
    /// unlike OS balloons). Returns false when no panel is open. UI thread.
    /// </summary>
    public bool ShowNotice(string message, TimeSpan duration)
    {
        if (!_dispatcher.CheckAccess() || _viewModel is not { } viewModel)
        {
            return false;
        }

        viewModel.ShowNotice(message, duration);
        return true;
    }

    /// <summary>Closes the panel after pending notes have been saved.</summary>
    public void Close() => _ = CloseAsync();

    /// <summary>Closes the panel after pending notes are on disk; leaves it open on a save conflict.</summary>
    public async Task CloseAsync()
    {
        if (!_dispatcher.CheckAccess())
        {
            await _dispatcher.InvokeAsync(CloseAsync).Task.Unwrap().ConfigureAwait(false);
            return;
        }

        var window = _window;
        var viewModel = _viewModel;
        var feed = _feed;
        if (viewModel is not null && !await viewModel.ShutdownAsync().ConfigureAwait(true))
        {
            // Keep the live editor accessible; closing here would discard its only copy of the draft.
            return;
        }

        _window = null;
        _viewModel = null;
        _feed = null;

        try
        {
            if (window is not null)
            {
                window.AllowClose = true;
                window.Close();
            }

        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning($"quill: companion panel did not close cleanly: {exception.Message}");
        }
        finally
        {
            feed?.Dispose();
        }
    }
}
