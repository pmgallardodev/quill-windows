using System.Text.Json;
using System.Text.Json.Serialization;
using Quill.Core.Ports;

namespace Quill.Core.AiPack;

/// <summary>
/// Locates the AI pack through its manifest. Layout (owned by packaging, P1):
/// <code>
/// &lt;root&gt;/ai-pack.json   { "version": "1.0.0", "files": { "languageModel": "llm/x.gguf", ... } }
/// </code>
/// <c>files</c> keys are the camelCase <see cref="AiPackComponent"/> names; values
/// are paths relative to the root and must stay inside it. Root is the override
/// path (Settings) when given, else "&lt;appDirectory&gt;/ai-pack".
/// </summary>
public sealed class ManifestAiPackLocator : IAiPackLocator
{
    public const string DefaultFolderName = "ai-pack";
    public const string ManifestFileName = "ai-pack.json";

    private readonly string _root;

    public ManifestAiPackLocator(string appDirectory, string? overrideRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        _root = string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(appDirectory, DefaultFolderName)
            : overrideRoot;
    }

    public AiPackStatus Locate()
    {
        Manifest? manifest;
        string root;
        try
        {
            root = Path.GetFullPath(_root);
            var manifestPath = Path.Combine(root, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                return AiPackStatus.NotInstalled with { Root = root };
            }

            manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath));
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            return AiPackStatus.NotInstalled with { Root = _root };
        }

        if (manifest?.Files is null)
        {
            return AiPackStatus.NotInstalled with { Root = root };
        }

        var paths = new Dictionary<AiPackComponent, string>();
        var missing = new List<AiPackComponent>();
        foreach (var component in Enum.GetValues<AiPackComponent>())
        {
            var key = JsonNamingPolicy.CamelCase.ConvertName(component.ToString());
            var resolved = manifest.Files.TryGetValue(key, out var relative) ? Resolve(root, relative) : null;
            if (resolved is not null)
            {
                paths[component] = resolved;
            }
            else
            {
                missing.Add(component);
            }
        }

        return new AiPackStatus(true, root, manifest.Version, paths, missing);
    }

    private static string? Resolve(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(Path.Combine(root, relative));
            var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private sealed class Manifest
    {
        [JsonPropertyName("version")]
        public string? Version { get; init; }

        [JsonPropertyName("files")]
        public Dictionary<string, string?>? Files { get; init; }
    }
}
