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
            _ = CloseAsync();
        }

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

    /// <summary>Closes the panel; pending notes are written in the background.</summary>
    public void Close() => _ = CloseAsync();

    /// <summary>Closes the panel and waits until pending notes are on disk. Never throws.</summary>
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

            if (viewModel is not null)
            {
                await viewModel.ShutdownAsync();
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
