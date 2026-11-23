using Quill.Windows.UI;
using Xunit;

namespace Quill.Windows.Tests.UI;

internal sealed class FakeVoiceActions : IVoiceActions
{
    public SpeakerPanelData Data { get; set; } = new(
        [new SpeakerPanelItem("speaker-1", null, "Juan"), new SpeakerPanelItem("speaker-2", "Ana", null)],
        false);

    public List<string> Calls { get; } = new();

    public Exception? InPersonFailure { get; set; }

    public Task<SpeakerPanelData> LoadAsync(string sessionId, CancellationToken cancellationToken) => Task.FromResult(Data);

    public Task RenameSpeakerAsync(string sessionId, string speakerId, string? name, CancellationToken cancellationToken)
    {
        Calls.Add($"rename {speakerId} {name ?? "<none>"}");
        return Task.CompletedTask;
    }

    public Task ConfirmSuggestionAsync(string sessionId, string speakerId, CancellationToken cancellationToken)
    {
        Calls.Add($"confirm {speakerId}");
        return Task.CompletedTask;
    }

    public Task RejectSuggestionAsync(string sessionId, string speakerId, CancellationToken cancellationToken)
    {
        Calls.Add($"reject {speakerId}");
        return Task.CompletedTask;
    }

    public Task SetInPersonAsync(string sessionId, bool inPerson, CancellationToken cancellationToken)
    {
        Calls.Add($"in-person {inPerson}");
        return InPersonFailure is null ? Task.CompletedTask : Task.FromException(InPersonFailure);
    }

    public Task<SavedVoices> LoadVoicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SavedVoices([], false));

    public Task RenameVoiceAsync(string voiceId, string name, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ForgetVoiceAsync(string voiceId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SetAutoApplyAsync(bool autoApply, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class SpeakersPanelViewModelTests
{
    private static async Task<(SpeakersPanelViewModel Panel, FakeVoiceActions Voices)> LoadedAsync(bool aiAvailable = true)
    {
        var voices = new FakeVoiceActions();
        var panel = new SpeakersPanelViewModel(voices, () => aiAvailable);
        await panel.LoadAsync("s1", CancellationToken.None);
        return (panel, voices);
    }

    [Fact]
    public async Task ShowsSpeakersWithTheirSuggestion()
    {
        var (panel, _) = await LoadedAsync();

        Assert.True(panel.HasSpeakers);
        Assert.True(panel.HasSuggestions);
        var first = panel.Speakers[0];
        Assert.True(first.HasSuggestion);
        Assert.Contains("Juan", first.SuggestionText, StringComparison.Ordinal);
        Assert.Equal("Ana", panel.Speakers[1].Label);
        Assert.Equal("Ana", panel.Speakers[1].NameDraft);
    }

    [Fact]
    public async Task ConfirmRejectAndRenameGoThroughTheVoiceActions()
    {
        var (panel, voices) = await LoadedAsync();

        await panel.ConfirmAsync(panel.Speakers[0]);
        await panel.RejectAsync(panel.Speakers[0]);
        panel.Speakers[1].NameDraft = "  ";
        await panel.RenameAsync(panel.Speakers[1]);

        Assert.Equal(["confirm speaker-1", "reject speaker-1", "rename speaker-2 <none>"], voices.Calls);
    }

    [Fact]
    public async Task InPersonSwitchIsRevertedWhenItFails()
    {
        var (panel, voices) = await LoadedAsync();
        voices.InPersonFailure = new AiPackMissingException();

        panel.IsInPerson = true;
        await Task.Yield();

        Assert.Equal(["in-person True"], voices.Calls);
        Assert.False(panel.IsInPerson);
        Assert.NotEmpty(panel.StatusText);
    }

    [Fact]
    public async Task InPersonNeedsTheAiPack()
    {
        var (panel, _) = await LoadedAsync(aiAvailable: false);

        Assert.False(panel.CanChangeInPerson);
    }
}
