using System.Text.Json.Nodes;
using Quill.Core.AiPack.Download;
using Quill.Core.Ports;
using Xunit;

namespace Quill.Core.Tests.AiPack.Download;

public sealed class AiPackCatalogTests
{
    [Fact]
    public void EmbeddedCatalogCoversEveryComponentOnceFromPinnedHuggingFaceUrls()
    {
        var catalog = AiPackCatalog.Default;

        Assert.Equal(
            Enum.GetValues<AiPackComponent>().Order(),
            catalog.Entries.Select(e => e.Component).Order());
        Assert.All(catalog.Entries, entry =>
        {
            Assert.True(AiPackHostPolicy.Default.IsAllowed(entry.Url), entry.Url.ToString());
            Assert.Equal("huggingface.co", entry.Url.Host);
            Assert.Contains("/resolve/", entry.Url.AbsolutePath);
            Assert.Matches("^[0-9a-f]{64}$", entry.Sha256);
            Assert.True(entry.Size > 0);
        });
    }

    [Fact]
    public void DownloadsTheWholeQ4KMLanguageModel()
    {
        var llm = AiPackCatalog.Default.Entries.Single(e => e.Component == AiPackComponent.LanguageModel);

        Assert.EndsWith("Q4_K_M.gguf", llm.Url.AbsolutePath, StringComparison.Ordinal);
        Assert.True(llm.Size > 2L * 1024 * 1024 * 1024, "the in-app download is not bound by the 2 GiB asset cap");
    }

    /// <summary>
    /// The embedded catalog is a copy of tools/ai-pack.models.json (the packaging source of truth).
    /// Every component shared by URL must agree on path, size and SHA-256; components other than
    /// the language model must use the same URL. The language model differs on purpose while the
    /// tools catalog packs another file for the 2 GiB asset cap: it is compared once the tools
    /// entry points at the same file (same URL or path) or declares split parts of it.
    /// </summary>
    [Fact]
    public void MatchesTheToolsCatalog()
    {
        var tools = JsonNode.Parse(File.ReadAllText(ToolsCatalogPath()))!["components"]!.AsArray();
        var embedded = AiPackCatalog.Default.Entries.ToDictionary(e => e.Component);

        foreach (var node in tools)
        {
            var component = Enum.Parse<AiPackComponent>((string)node!["component"]!, ignoreCase: true);
            var mine = embedded[component];
            var url = (string)node["url"]!;
            var path = (string)node["path"]!;
            var sameFile = url == mine.Url.AbsoluteUri || path == mine.Path || node["parts"] is not null;

            if (component != AiPackComponent.LanguageModel)
            {
                Assert.Equal(url, mine.Url.AbsoluteUri);
            }

            if (sameFile)
            {
                Assert.Equal(path, mine.Path);
                Assert.Equal((long)node["size"]!, mine.Size);
                Assert.Equal((string)node["sha256"]!, mine.Sha256);
            }
        }

        Assert.Equal(embedded.Count, tools.Count);
    }

    [Theory]
    [InlineData("../escape.gguf")]
    [InlineData("/abs/model.gguf")]
    [InlineData("llm/../../escape.gguf")]
    public void RejectsPathsOutsideThePack(string path)
    {
        var entry = new AiPackCatalogEntry(
            AiPackComponent.LanguageModel, "x", path, new Uri("https://huggingface.co/a/resolve/b/c"), 1, new string('a', 64));

        Assert.Throws<ArgumentException>(() => new AiPackCatalog([entry]));
    }

    [Fact]
    public void RejectsNonHttpsUrlsAndBadHashes()
    {
        Assert.Throws<ArgumentException>(() => new AiPackCatalog([
            new AiPackCatalogEntry(AiPackComponent.LanguageModel, "x", "a.gguf", new Uri("http://huggingface.co/a"), 1, new string('a', 64)),
        ]));
        Assert.Throws<ArgumentException>(() => new AiPackCatalog([
            new AiPackCatalogEntry(AiPackComponent.LanguageModel, "x", "a.gguf", new Uri("https://huggingface.co/a"), 1, "TO_FILL"),
        ]));
    }

    private static string ToolsCatalogPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tools", "ai-pack.models.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("tools/ai-pack.models.json not found above the test output folder");
    }
}
