using Quill.Core.Domain;
using Quill.Core.Summaries;

namespace Quill.Windows.UI.Companion;

/// <summary>
/// What the panel reads from the live summary. Events may fire on any thread; the view model
/// marshals them to the UI thread. Exists so the view model can be tested without a model.
/// </summary>
internal interface ILiveSummaryFeed
{
    LiveSummaryState State { get; }

    MeetingSummary? Current { get; }

    event EventHandler<MeetingSummary>? Updated;

    event EventHandler<LiveSummaryState>? StateChanged;
}

/// <summary>Adapts <see cref="LiveSummaryLoop"/> to <see cref="ILiveSummaryFeed"/>.</summary>
internal sealed class LiveSummaryLoopFeed : ILiveSummaryFeed, IDisposable
{
    private readonly LiveSummaryLoop _loop;

    public LiveSummaryLoopFeed(LiveSummaryLoop loop)
    {
        _loop = loop ?? throw new ArgumentNullException(nameof(loop));
        _loop.Updated += OnUpdated;
        _loop.StateChanged += OnStateChanged;
    }

    public event EventHandler<MeetingSummary>? Updated;

    public event EventHandler<LiveSummaryState>? StateChanged;

    public LiveSummaryState State => _loop.State;

    public MeetingSummary? Current => _loop.Current;

    public void Dispose()
    {
        _loop.Updated -= OnUpdated;
        _loop.StateChanged -= OnStateChanged;
    }

    private void OnUpdated(object? sender, LiveSummaryUpdatedEventArgs e) => Updated?.Invoke(this, e.Summary);

    private void OnStateChanged(object? sender, LiveSummaryState state) => StateChanged?.Invoke(this, state);
}
