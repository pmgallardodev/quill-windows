using Quill.Core.AiPack;
using Quill.Core.AiPack.Download;
using Xunit;

namespace Quill.Core.Tests.AiPack.Download;

public sealed class AiPackDestinationTests
{
    private static readonly string App = Path.Combine(Path.GetTempPath(), "quill-app");
    private static readonly string Local = Path.Combine(Path.GetTempPath(), "quill-local");

    [Fact]
    public void ConfiguredPackPathWinsAndNeedsNoPersisting()
    {
        var configured = Path.Combine(Path.GetTempPath(), "my-pack");

        var destination = AiPackDestination.Resolve(configured, App, Local, _ => false);

        Assert.Equal(Path.GetFullPath(configured), destination.Root);
        Assert.False(destination.PersistAsPackPath);
    }

    [Fact]
    public void UsesTheFolderNextToTheAppWhenWritable()
    {
        var destination = AiPackDestination.Resolve(null, App, Local, _ => true);

        Assert.Equal(Path.Combine(App, ManifestAiPackLocator.DefaultFolderName), destination.Root);
        Assert.False(destination.PersistAsPackPath);
    }

    [Fact]
    public void FallsBackToLocalAppDataAndPersistsIt()
    {
        var destination = AiPackDestination.Resolve("  ", App, Local, _ => false);

        Assert.Equal(Path.Combine(Local, "Quill", ManifestAiPackLocator.DefaultFolderName), destination.Root);
        Assert.True(destination.PersistAsPackPath);
    }
}

public sealed class AiPackHostPolicyTests
{
    [Theory]
    [InlineData("https://huggingface.co/a/resolve/b/c")]
    [InlineData("https://us.aws.cdn.hf.co/xet-bridge-us/a/b?x=1")]
    [InlineData("https://eu.aws.cdn.hf.co/xet-bridge-eu/a/b")]
    [InlineData("https://cas-bridge.xethub.hf.co/xet-bridge-us/a/b")]
    [InlineData("https://cdn-lfs.huggingface.co/repos/a/b")]
    [InlineData("https://cdn-lfs.hf.co/repos/a/b")]
    [InlineData("https://cdn-lfs-us-1.hf.co/repos/a/b")]
    [InlineData("https://cdn-lfs-eu-1.hf.co/repos/a/b")]
    [InlineData("https://HuggingFace.co/a")]
    public void AllowsHuggingFaceAndItsCdn(string url) =>
        Assert.True(AiPackHostPolicy.Default.IsAllowed(new Uri(url)));

    [Theory]
    [InlineData("http://huggingface.co/a")]
    [InlineData("https://huggingface.co:8443/a")]
    [InlineData("https://user:pw@huggingface.co/a")]
    [InlineData("https://evil.hf.co/a")]
    [InlineData("https://hf.co/a")]
    [InlineData("https://huggingface.co.evil.example/a")]
    [InlineData("https://github.com/a")]
    public void RejectsEverythingElse(string url) =>
        Assert.False(AiPackHostPolicy.Default.IsAllowed(new Uri(url)));
}
