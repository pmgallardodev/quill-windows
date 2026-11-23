using Quill.Core.Api;
using Quill.Core.Settings;
using Quill.Windows.UI.Settings;
using Xunit;

namespace Quill.Windows.Tests.LocalApi;

public sealed class ApiSettingsViewModelTests
{
    [Fact]
    public void TurningOnGeneratesATokenAndMasksIt()
    {
        var viewModel = new ApiSettingsViewModel(new ApiSettings());
        Assert.False(viewModel.HasToken);

        viewModel.Enabled = true;

        Assert.True(viewModel.HasToken);
        Assert.DoesNotContain(viewModel.Token![..10], viewModel.TokenDisplay);
        Assert.EndsWith(viewModel.Token[^4..], viewModel.TokenDisplay);
        viewModel.ShowToken = true;
        Assert.Equal(viewModel.Token, viewModel.TokenDisplay);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData(" 48123 ", 48123)]
    public void BuildsWithAValidOrEmptyPort(string text, int port)
    {
        var viewModel = new ApiSettingsViewModel(new ApiSettings()) { PortText = text };

        Assert.True(viewModel.TryBuild(out var settings, out var error));
        Assert.Null(error);
        Assert.Equal(port, settings.Port);
    }

    [Theory]
    [InlineData("80")]
    [InlineData("70000")]
    [InlineData("abc")]
    [InlineData("-5")]
    public void RefusesAnInvalidPort(string text)
    {
        var viewModel = new ApiSettingsViewModel(new ApiSettings()) { PortText = text };

        Assert.False(viewModel.TryBuild(out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void AddsOnlyLoopbackWebhooksOnce()
    {
        var viewModel = new ApiSettingsViewModel(new ApiSettings());

        viewModel.NewWebhook = "http://example.com/hook";
        Assert.False(viewModel.AddWebhook());
        Assert.True(viewModel.HasWebhookError);

        viewModel.NewWebhook = " http://127.0.0.1:9000/hook ";
        Assert.True(viewModel.AddWebhook());
        Assert.False(viewModel.HasWebhookError);
        Assert.Equal(string.Empty, viewModel.NewWebhook);

        viewModel.NewWebhook = "http://127.0.0.1:9000/hook";
        Assert.False(viewModel.AddWebhook());

        Assert.Equal(["http://127.0.0.1:9000/hook"], viewModel.Webhooks);
        viewModel.RemoveWebhook("http://127.0.0.1:9000/hook");
        Assert.Empty(viewModel.Webhooks);
    }

    [Fact]
    public void DetectsChangesIncludingARegeneratedToken()
    {
        var original = new ApiSettings { Enabled = true, Port = 48000, Token = ApiToken.Generate() };
        var viewModel = new ApiSettingsViewModel(original);

        Assert.True(viewModel.TryBuild(out var unchanged, out _));
        Assert.False(viewModel.HasChanges(unchanged));

        viewModel.RegenerateToken();
        Assert.True(viewModel.TryBuild(out var changed, out _));
        Assert.True(viewModel.HasChanges(changed));
        Assert.NotEqual(original.Token, changed.Token);
    }
}
