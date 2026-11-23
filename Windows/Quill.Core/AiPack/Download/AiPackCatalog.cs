using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Quill.Core.Ports;

namespace Quill.Core.AiPack.Download;

/// <summary>One downloadable model: where it comes from, where it goes and what it must hash to.</summary>
/// <param name="Path">Location inside the pack folder, written to ai-pack.json "files".</param>
/// <param name="Url">Pinned HTTPS URL (Hugging Face "resolve/&lt;commit&gt;").</param>
/// <param name="Size">Exact size in bytes; the download aborts as soon as it is exceeded.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the whole file.</param>
public sealed record AiPackCatalogEntry(
    AiPackComponent Component,
    string Name,
    string Path,
    Uri Url,
    long Size,
    string Sha256,
    string? License = null,
    string? Source = null,
    string? Attribution = null);

/// <summary>
/// The models the in-app "Descargar pack de IA" button installs. The default catalog is the
/// embedded ai-pack.catalog.json, a copy of tools/ai-pack.models.json kept in sync by a test.
/// Entries are validated on construction: one per component, HTTPS, a 64-hex SHA-256, a
/// positive size and a relative path that stays inside the pack folder.
/// </summary>
public sealed partial class AiPackCatalog
{
    public const string ResourceName = "Quill.Core.AiPack.Download.ai-pack.catalog.json";

    private static readonly Lazy<AiPackCatalog> Embedded = new(LoadEmbedded);

    public AiPackCatalog(IEnumerable<AiPackCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.ToList();
        foreach (var entry in list)
        {
            Validate(entry);
        }

        if (list.GroupBy(e => e.Component).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new ArgumentException($"component {duplicate.Key} listed twice", nameof(entries));
        }

        Entries = list;
    }

    public static AiPackCatalog Default => Embedded.Value;

    public IReadOnlyList<AiPackCatalogEntry> Entries { get; }

    public long TotalBytes => Entries.Sum(e => e.Size);

    public static AiPackCatalog Parse(string json)
    {
        var document = JsonSerializer.Deserialize<CatalogDocument>(json)
            ?? throw new ArgumentException("empty AI pack catalog", nameof(json));
        return new AiPackCatalog((document.Components ?? []).Select(ToEntry));
    }

    private static AiPackCatalog LoadEmbedded()
    {
        using var stream = typeof(AiPackCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"missing embedded resource {ResourceName}");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private static AiPackCatalogEntry ToEntry(ComponentDocument document)
    {
        if (!Enum.TryParse<AiPackComponent>(document.Component, ignoreCase: true, out var component))
        {
            throw new ArgumentException($"unknown AI pack component '{document.Component}'");
        }

        return new AiPackCatalogEntry(
            component,
            document.Name ?? component.ToString(),
            document.Path ?? string.Empty,
            new Uri(document.Url ?? string.Empty, UriKind.Absolute),
            document.Size,
            document.Sha256 ?? string.Empty,
            document.License,
            document.Source,
            document.Attribution);
    }

    private static void Validate(AiPackCatalogEntry entry)
    {
        if (entry.Url.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"{entry.Component}: URL must be HTTPS");
        }

        if (!Sha256Pattern().IsMatch(entry.Sha256))
        {
            throw new ArgumentException($"{entry.Component}: SHA-256 must be 64 lowercase hex characters");
        }

        if (entry.Size <= 0)
        {
            throw new ArgumentException($"{entry.Component}: size must be positive");
        }

        var segments = entry.Path.Split('/', '\\');
        if (string.IsNullOrWhiteSpace(entry.Path) ||
            System.IO.Path.IsPathRooted(entry.Path) ||
            entry.Path.StartsWith('/') ||
            segments.Any(s => s is "" or "." or ".."))
        {
            throw new ArgumentException($"{entry.Component}: path '{entry.Path}' must stay inside the pack folder");
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    private sealed class CatalogDocument
    {
        [JsonPropertyName("components")]
        public List<ComponentDocument>? Components { get; init; }
    }

    private sealed class ComponentDocument
    {
        [JsonPropertyName("component")]
        public string? Component { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("path")]
        public string? Path { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }

        [JsonPropertyName("license")]
        public string? License { get; init; }

        [JsonPropertyName("source")]
        public string? Source { get; init; }

        [JsonPropertyName("attribution")]
        public string? Attribution { get; init; }
    }
}
