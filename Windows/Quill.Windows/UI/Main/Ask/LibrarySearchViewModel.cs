using System.Collections.ObjectModel;
using System.Windows.Input;
using Quill.Core.Search;
using Quill.Core.Summaries;
using Quill.Windows.Ask;
using Quill.Windows.Resources;

namespace Quill.Windows.UI;

/// <summary>One search hit: meeting, date, moment and a snippet around the match.</summary>
internal sealed class SearchResultViewModel
{
    public SearchResultViewModel(SearchResult result, string query, IAskService service)
    {
        Result = result;
        Title = result.SessionTitle;
        DateText = result.Started == default ? string.Empty : UiFormat.Date(result.Started, "Format_MeetingDate");
        TimeText = result.Passage.Start is { } start ? TimestampJsonConverter.Format(start) : string.Empty;
        KindText = result.Passage.Kind switch
        {
            PassageKinds.Summary => Loc.Get("Search_LibraryKindSummary"),
            PassageKinds.Note => Loc.Get("Search_LibraryKindNote"),
            _ => Loc.Get("Search_LibraryKindTranscript"),
        };
        Snippet = SearchSnippet.Make(result.Passage.Text, query);
        var at = result.Passage.Start ?? TimeSpan.Zero;
        OpenCommand = new RelayCommand(_ => service.OpenAt(result.Passage.SessionId, at));
    }

    public SearchResult Result { get; }

    public string Title { get; }

    public string DateText { get; }

    public string TimeText { get; }

    public string KindText { get; }

    public string Snippet { get; }

    public ICommand OpenCommand { get; }
}

/// <summary>
/// "Buscar en todas las reuniones": hybrid search over every meeting, plus a library-wide
/// <see cref="AskViewModel"/> ("Preguntar a todas") on the same page.
/// </summary>
internal sealed class LibrarySearchViewModel : ObservableObject
{
    public const int MaxResults = 30;

    private readonly IAskService _service;
    private CancellationTokenSource? _running;
    private string _query = string.Empty;
    private string _status = string.Empty;
    private bool _isSearching;

    public LibrarySearchViewModel(IAskService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        Ask = new AskViewModel(service, sessionId: null);
        SearchCommand = new RelayCommand(_ => _ = SearchAsync(), _ => !string.IsNullOrWhiteSpace(_query));
    }

    public AskViewModel Ask { get; }

    public string Query
    {
        get => _query;
        set => Set(ref _query, value ?? string.Empty);
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
            {
                Raise(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => _status.Length > 0;

    public bool IsSearching
    {
        get => _isSearching;
        private set => Set(ref _isSearching, value);
    }

    public ObservableCollection<SearchResultViewModel> Results { get; } = new();

    public ICommand SearchCommand { get; }

    /// <summary>Runs the search; a newer search cancels the previous one.</summary>
    public async Task SearchAsync()
    {
        var query = _query.Trim();
        if (query.Length == 0)
        {
            return;
        }

        _running?.Cancel();
        using var running = new CancellationTokenSource();
        _running = running;
        IsSearching = true;
        Status = Loc.Get("Search_Searching");
        try
        {
            var response = await _service.SearchAsync(new SearchQuery(query, MaxResults), running.Token);
            if (!ReferenceEquals(_running, running))
            {
                return;
            }

            Results.Clear();
            foreach (var result in response.Results)
            {
                Results.Add(new SearchResultViewModel(result, query, _service));
            }

            var count = response.Results.Count switch
            {
                0 => Loc.Get("Search_NoResults"),
                1 => Loc.Get("Search_OneResult"),
                var many => Loc.Format("Search_Results", many),
            };
            Status = response.UsedVectors ? count : $"{count} · {Loc.Get("Search_LibraryLexicalOnly")}";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer search.
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Status = Loc.Format("Search_LibraryError", exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_running, running))
            {
                _running = null;
                IsSearching = false;
            }
        }
    }
}
