using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Quill.Core.Ports;

namespace Quill.Core.AiPack;

/// <summary>
/// Locates the AI pack through its manifest. Layout (owned by packaging, tools/build-ai-pack.ps1):
/// <code>
/// &lt;root&gt;/ai-pack.json
/// {
///   "version": "1.0.0",
///   "files": { "languageModel": "llm/x.gguf", ... },
///   "splitFiles": {                               // optional, schema 2
///     "languageModel": {
///       "size": 2497281120, "sha256": "&lt;whole file&gt;",
///       "parts": [ { "file": "llm/x.gguf.part1", "size": 1248640560, "sha256": "&lt;part&gt;" }, ... ]
///     }
///   }
/// }
/// </code>
/// <c>files</c> keys are the camelCase <see cref="AiPackComponent"/> names; values
/// are paths relative to the root and must stay inside it. Root is the override
/// path (Settings) when given, else "&lt;appDirectory&gt;/ai-pack".
/// <para>
/// A component listed in <c>splitFiles</c> ships as byte parts (GitHub release assets stop at
/// 2 GiB). When its file is absent but every part is present with its exact size, the
/// component is reported missing with a <see cref="ModelPreparation"/> state and
/// <see cref="SplitModelPreparation"/> joins it in the background into the pack folder, or into
/// "&lt;fallbackRoot&gt;/&lt;sha256&gt;/&lt;file name&gt;" when the pack folder is read-only. Manifests
/// without <c>splitFiles</c> behave exactly as before.
/// </para>
/// </summary>
public sealed partial class ManifestAiPackLocator : IAiPackLocator
{
    public const string DefaultFolderName = "ai-pack";
    public const string ManifestFileName = "ai-pack.json";

    private readonly string _root;
    private readonly string? _fallbackRoot;
    private readonly SplitModelPreparation _preparation;

    /// <param name="fallbackRoot">
    /// Writable folder for split models joined while the pack folder is read-only; null uses
    /// "%LOCALAPPDATA%\Quill\ai-pack".
    /// </param>
    /// <param name="preparation">Joins split models; null uses <see cref="SplitModelPreparation.Shared"/>.</param>
    public ManifestAiPackLocator(
        string appDirectory,
        string? overrideRoot = null,
        string? fallbackRoot = null,
        SplitModelPreparation? preparation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        _root = string.IsNullOrWhiteSpace(overrideRoot)
            ? Path.Combine(appDirectory, DefaultFolderName)
            : overrideRoot;
        _fallbackRoot = string.IsNullOrWhiteSpace(fallbackRoot) ? DefaultFallbackRoot() : fallbackRoot;
        _preparation = preparation ?? SplitModelPreparation.Shared;
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
        var preparing = new List<ModelPreparation>();
        foreach (var component in Enum.GetValues<AiPackComponent>())
        {
            var key = JsonNamingPolicy.CamelCase.ConvertName(component.ToString());
            manifest.Files.TryGetValue(key, out var relative);
            var resolved = Resolve(root, relative);
            if (resolved is not null)
            {
                paths[component] = resolved;
                continue;
            }

            missing.Add(component);
            if (manifest.SplitFiles is not null &&
                manifest.SplitFiles.TryGetValue(key, out var split) &&
                SplitJob(component, root, relative, split) is { } job)
            {
                var (path, preparation) = Prepare(job);
                if (path is not null)
                {
                    missing.Remove(component);
                    paths[component] = path;
                }
                else if (preparation is not null)
                {
                    preparing.Add(preparation);
                }
            }
        }

        return new AiPackStatus(true, root, manifest.Version, paths, missing) { Preparing = preparing };
    }

    private (string? Path, ModelPreparation? Preparation) Prepare(SplitModelJob job)
    {
        try
        {
            if (File.Exists(job.FallbackPath))
            {
                return (job.FallbackPath, null);
            }

            if (job.Parts.Any(part => !HasLength(part.Path, part.Size)))
            {
                return (null, null);
            }

            var state = _preparation.Ensure(job);
            if (state.State == ModelPreparationState.Ready)
            {
                // Joined since this Locate started; pick up whichever folder received it.
                var joined = File.Exists(job.TargetPath) ? job.TargetPath : File.Exists(job.FallbackPath) ? job.FallbackPath : null;
                return (joined, null);
            }

            return (null, state);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }

    private SplitModelJob? SplitJob(AiPackComponent component, string root, string? relative, SplitEntry? split)
    {
        if (split?.Parts is not { Count: > 0 } parts ||
            split.Size <= 0 ||
            split.Sha256 is null ||
            !Sha256Pattern().IsMatch(split.Sha256) ||
            Inside(root, relative) is not { } target)
        {
            return null;
        }

        var resolvedParts = new List<SplitModelPart>();
        foreach (var part in parts)
        {
            if (part is null || part.Size <= 0 || Inside(root, part.File) is not { } partPath ||
                (part.Sha256 is not null && !Sha256Pattern().IsMatch(part.Sha256)))
            {
                return null;
            }

            resolvedParts.Add(new SplitModelPart(partPath, part.Size, part.Sha256?.ToLowerInvariant()));
        }

        if (resolvedParts.Sum(part => part.Size) != split.Size)
        {
            return null;
        }

        var sha256 = split.Sha256.ToLowerInvariant();
        var fallback = _fallbackRoot is null
            ? target
            : Path.Combine(_fallbackRoot, sha256, Path.GetFileName(target));
        return new SplitModelJob(component, target, fallback, split.Size, sha256, resolvedParts);
    }

    private static bool HasLength(string path, long length)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == length;
    }

    private static string? Resolve(string root, string? relative) =>
        Inside(root, relative) is { } full && File.Exists(full) ? full : null;

    /// <summary>The full path of <paramref name="relative"/> when it stays inside <paramref name="root"/>.</summary>
    private static string? Inside(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(Path.Combine(root, relative));
            var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? DefaultFallbackRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(local) ? null : Path.Combine(local, "Quill", DefaultFolderName);
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();

    private sealed class Manifest
    {
        [JsonPropertyName("version")]
        public string? Version { get; init; }

        [JsonPropertyName("files")]
        public Dictionary<string, string?>? Files { get; init; }

        [JsonPropertyName("splitFiles")]
        public Dictionary<string, SplitEntry?>? SplitFiles { get; init; }
    }

    private sealed class SplitEntry
    {
        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }

        [JsonPropertyName("parts")]
        public List<SplitPartEntry?>? Parts { get; init; }
    }

    private sealed class SplitPartEntry
    {
        [JsonPropertyName("file")]
        public string? File { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }
    }
}
