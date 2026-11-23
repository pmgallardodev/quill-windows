using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quill.Core.Export;

public enum MarkdownExportStatus
{
    Created,
    Updated,

    /// <summary>The file already had exactly this content.</summary>
    Unchanged,

    /// <summary>The user edited the export since Quill wrote it, and overwriting edited files is off.</summary>
    SkippedEdited,

    /// <summary>The meeting could not be exported; see <see cref="MarkdownExportResult.Error"/>.</summary>
    Failed,
}

public sealed record MarkdownExportResult(string SessionId, MarkdownExportStatus Status, string? Path, string? Error = null);

/// <summary>
/// Writes exports into a user folder (typically an Obsidian vault) without ever destroying the
/// user's work. An index at <see cref="IndexRelativePath"/> remembers, per session, the file
/// name and the SHA-256 of what Quill last wrote. A file whose current hash differs was edited
/// by the user and is only replaced when <c>overwriteEdited</c> is true. A file Quill did not
/// write (or does not remember, e.g. after the index was lost) is never touched: the export
/// takes a "-2", "-3"... name instead. A session keeps its first file name even if the title
/// changes, so renaming a meeting does not duplicate its note.
/// </summary>
public sealed class MarkdownExportWriter
{
    /// <summary>Dot-folder: Obsidian does not index it.</summary>
    public static readonly string IndexRelativePath = Path.Combine(".quill", "exports.json");

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object WriteLock = new();

    private readonly string _folder;

    public MarkdownExportWriter(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        _folder = Path.GetFullPath(folder);
    }

    private string IndexPath => Path.Combine(_folder, IndexRelativePath);

    /// <param name="preferredFileName">Bare "*.md" file name, used the first time this session is exported.</param>
    public MarkdownExportResult Write(string sessionId, string preferredFileName, string content, bool overwriteEdited)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(content);
        ValidateFileName(preferredFileName);

        lock (WriteLock)
        {
            return WriteLocked(sessionId, preferredFileName, content, overwriteEdited);
        }
    }

    private MarkdownExportResult WriteLocked(string sessionId, string preferredFileName, string content, bool overwriteEdited)
    {
        var index = LoadIndex();
        var bytes = Utf8.GetBytes(content);
        var hash = Hash(bytes);
        MarkdownExportStatus status;
        string fileName;

        if (index.Files.TryGetValue(sessionId, out var entry) && IsValidFileName(entry.File))
        {
            fileName = entry.File!;
            var path = Path.Combine(_folder, fileName);
            if (File.Exists(path))
            {
                var current = Hash(File.ReadAllBytes(path));
                if (current == hash)
                {
                    status = MarkdownExportStatus.Unchanged;
                }
                else if (current != entry.Sha256 && !overwriteEdited)
                {
                    return new MarkdownExportResult(sessionId, MarkdownExportStatus.SkippedEdited, path);
                }
                else
                {
                    status = MarkdownExportStatus.Updated;
                }
            }
            else
            {
                status = MarkdownExportStatus.Created;
            }
        }
        else
        {
            fileName = UniqueName(preferredFileName, sessionId, index);
            status = MarkdownExportStatus.Created;
        }

        var target = Path.Combine(_folder, fileName);
        if (status != MarkdownExportStatus.Unchanged)
        {
            if (status == MarkdownExportStatus.Created)
            {
                while (!TryWriteAtomicallyCreateOnly(target, bytes))
                {
                    fileName = UniqueName($"{Path.GetFileNameWithoutExtension(fileName)}.md", sessionId, index);
                    target = Path.Combine(_folder, fileName);
                }
            }
            else
            {
                WriteAtomically(target, bytes);
            }
        }

        index.Files[sessionId] = new IndexEntry { File = fileName, Sha256 = hash };
        WriteAtomically(IndexPath, JsonSerializer.SerializeToUtf8Bytes(index, JsonOptions));
        return new MarkdownExportResult(sessionId, status, target);
    }

    private string UniqueName(string preferred, string sessionId, Index index)
    {
        var stem = Path.GetFileNameWithoutExtension(preferred);
        var candidate = preferred;
        for (var number = 2; IsTaken(candidate, sessionId, index); number++)
        {
            candidate = $"{stem}-{number}.md";
        }

        return candidate;
    }

    private bool IsTaken(string fileName, string sessionId, Index index) =>
        File.Exists(Path.Combine(_folder, fileName)) ||
        index.Files.Any(pair => pair.Key != sessionId && string.Equals(pair.Value.File, fileName, StringComparison.OrdinalIgnoreCase));

    private Index LoadIndex()
    {
        try
        {
            if (File.Exists(IndexPath))
            {
                var index = JsonSerializer.Deserialize<Index>(File.ReadAllBytes(IndexPath));
                if (index?.Files is not null)
                {
                    return index;
                }
            }
        }
        catch (JsonException)
        {
            // A broken index forgets every export: their files become "unknown" and are left alone.
        }

        return new Index();
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static bool TryWriteAtomicallyCreateOnly(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            try
            {
                File.Move(temporary, path);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void ValidateFileName(string fileName)
    {
        if (!IsValidFileName(fileName))
        {
            throw new ArgumentException("the export file name must be a bare *.md file name", nameof(fileName));
        }
    }

    private static bool IsValidFileName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        fileName == Path.GetFileName(fileName) &&
        !fileName.Contains('/') && !fileName.Contains('\\') &&
        fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
        fileName.Length > ".md".Length;

    private sealed class Index
    {
        [JsonPropertyName("version")]
        public int Version { get; init; } = 1;

        [JsonPropertyName("files")]
        public Dictionary<string, IndexEntry> Files { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed class IndexEntry
    {
        [JsonPropertyName("file")]
        public string? File { get; init; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; init; }
    }
}
