using System.Collections.ObjectModel;
using System.Windows.Input;
using Quill.Core.Ask;
using Quill.Core.Summaries;
using Quill.Windows.Ask;
using Quill.Windows.Resources;

namespace Quill.Windows.UI;

/// <summary>A clickable source under an answer: "00:01:05" (or "Comité · 00:01:05" across meetings).</summary>
internal sealed class AskCitationViewModel
{
    public AskCitationViewModel(AskCitation citation, string? meetingTitle, IAskService service)
    {
        Citation = citation;
        var time = TimestampJsonConverter.Format(citation.At);
        Label = meetingTitle is null ? time : $"{meetingTitle} · {time}";
        Quote = citation.Quote;
        OpenCommand = new RelayCommand(_ => service.OpenAt(citation.SessionId, citation.At));
    }

    public AskCitation Citation { get; }

    public string Label { get; }

    public string Quote { get; }

    public ICommand OpenCommand { get; }
}

/// <summary>
/// "Preguntar": one question at a time about one meeting (<c>sessionId</c> set) or about every
/// meeting (null). The answer streams in as the model writes it, then is replaced by the checked
/// answer: invented citations removed, real ones shown as chips that play the moment.
/// </summary>
internal sealed class AskViewModel : ObservableObject
{
    private readonly IAskService _service;
    private readonly string? _sessionId;
    private CancellationTokenSource? _running;
    private int _generation;
    private string _question = string.Empty;
    private string _answer = string.Empty;
    private string _status = string.Empty;
    private bool _isBusy;
    private bool _isUngrounded;
    private bool _isNotFound;

    public AskViewModel(IAskService service, string? sessionId)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _sessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
        AskCommand = new RelayCommand(_ => _ = AskAsync(), _ => CanSubmit);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => _isBusy);
        if (!_service.CanAsk)
        {
            _status = Loc.Get("Ask_AiMissing");
        }
    }

    public bool IsLibrary => _sessionId is null;

    public bool CanAsk => _service.CanAsk;

    public string Placeholder => Loc.Get(IsLibrary ? "Ask_LibraryPlaceholder" : "Ask_Placeholder");

    public string Question
    {
        get => _question;
        set => Set(ref _question, value ?? string.Empty);
    }

    public string Answer
    {
        get => _answer;
        private set
        {
            if (Set(ref _answer, value))
            {
                Raise(nameof(HasAnswer));
            }
        }
    }

    public bool HasAnswer => _answer.Length > 0;

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

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    /// <summary>The answer cites nothing real: shown with a warning (design §4).</summary>
    public bool IsUngrounded
    {
        get => _isUngrounded;
        private set => Set(ref _isUngrounded, value);
    }

    public bool IsNotFound
    {
        get => _isNotFound;
        private set => Set(ref _isNotFound, value);
    }

    public ObservableCollection<AskCitationViewModel> Citations { get; } = new();

    public bool HasCitations => Citations.Count > 0;

    public ICommand AskCommand { get; }

    public ICommand CancelCommand { get; }

    private bool CanSubmit => _service.CanAsk && !_isBusy && !string.IsNullOrWhiteSpace(_question);

    public async Task AskAsync()
    {
        if (!CanSubmit)
        {
            return;
        }

        var generation = ++_generation;
        var question = _question.Trim();
        using var running = new CancellationTokenSource();
        _running = running;
        IsBusy = true;
        Answer = string.Empty;
        IsUngrounded = false;
        IsNotFound = false;
        Citations.Clear();
        Raise(nameof(HasCitations));
        Status = Loc.Get(IsLibrary ? "Ask_ThinkingLibrary" : "Ask_Thinking");

        // Late progress callbacks (posted to the dispatcher) must not overwrite the final answer.
        var streaming = true;
        var tokens = new Progress<string>(piece =>
        {
            if (streaming && generation == _generation)
            {
                Answer += piece;
            }
        });

        try
        {
            var answer = IsLibrary
                ? await _service.AskLibraryAsync(question, tokens, running.Token)
                : await _service.AskMeetingAsync(_sessionId!, question, tokens, running.Token);
            streaming = false;
            Show(answer);
            Status = string.Empty;
        }
        catch (OperationCanceledException)
        {
            streaming = false;
            Status = Loc.Get("Ask_Cancelled");
        }
        catch (Exception exception) when (exception is AiPackMissingException or LanguageModelUnavailableException)
        {
            streaming = false;
            Answer = string.Empty;
            Status = Loc.Get("Ask_AiMissing");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            streaming = false;
            Status = Loc.Format("Ask_Error", exception.Message);
        }
        finally
        {
            _running = null;
            IsBusy = false;
        }
    }

    public void Cancel() => _running?.Cancel();

    private void Show(AskAnswer answer)
    {
        Answer = answer.PlainText;
        IsNotFound = answer.NotFound;
        IsUngrounded = !answer.NotFound && !answer.IsGrounded;
        var titles = answer.Sources
            .GroupBy(source => source.Passage.SessionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().SessionTitle, StringComparer.Ordinal);
        foreach (var citation in answer.Citations)
        {
            var title = IsLibrary ? titles.GetValueOrDefault(citation.SessionId, citation.SessionId) : null;
            Citations.Add(new AskCitationViewModel(citation, title, _service));
        }

        Raise(nameof(HasCitations));
    }
}
