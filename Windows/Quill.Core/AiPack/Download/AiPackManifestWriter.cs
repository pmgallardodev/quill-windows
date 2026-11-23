using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quill.Core.AiPack.Download;

/// <summary>
/// Adds downloaded components to "&lt;root&gt;/ai-pack.json" in the layout
/// <see cref="ManifestAiPackLocator"/> reads: <c>files.&lt;camelCaseComponent&gt; = relative path</c>.
/// Every other key (including "schema" and other components' "splitFiles" written by packaging)
/// is kept; a registered component's own "splitFiles" entry is dropped because the whole file
/// replaces its parts. A fresh manifest holds only "version" and "files". The file is replaced
/// atomically through a temporary file.
/// </summary>
public static class AiPackManifestWriter
{
    /// <summary>"version" written when the manifest does not have one yet.</summary>
    public const string DownloadedVersion = "huggingface";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static void Register(string root, IEnumerable<AiPackCatalogEntry> entries)
    {
        var path = Path.Combine(root, ManifestAiPackLocator.ManifestFileName);
        var manifest = Read(path);
        if (manifest["version"] is null)
        {
            manifest["version"] = DownloadedVersion;
        }

        if (manifest["files"] is not JsonObject files)
        {
            files = new JsonObject();
            manifest["files"] = files;
        }

        var splitFiles = manifest["splitFiles"] as JsonObject;
        foreach (var entry in entries)
        {
            var key = JsonNamingPolicy.CamelCase.ConvertName(entry.Component.ToString());
            files[key] = entry.Path;

            // The verified whole file supersedes split parts of the same component (schema 2).
            splitFiles?.Remove(key);
        }

        Directory.CreateDirectory(root);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, manifest.ToJsonString(WriteOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static JsonObject Read(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            // A broken manifest makes the locator report "not installed" anyway; start over.
            return new JsonObject();
        }
    }
}
