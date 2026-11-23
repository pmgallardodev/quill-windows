namespace Quill.Core.AiPack.Download;

/// <summary>
/// Where the in-app download installs the pack: the "ai.pack_path" setting when set; otherwise
/// "&lt;app folder&gt;/ai-pack" when Quill can write there (the locator's default, nothing to
/// save); otherwise "%LOCALAPPDATA%\Quill\ai-pack", which must then be saved as "ai.pack_path"
/// so the locator finds it (<see cref="PersistAsPackPath"/>).
/// </summary>
public sealed record AiPackDestination(string Root, bool PersistAsPackPath)
{
    public const string LocalAppDataFolder = "Quill";

    /// <param name="configuredPackPath">"ai.pack_path" with environment variables already expanded.</param>
    /// <param name="isWritable">Probe for a candidate folder (may create it).</param>
    public static AiPackDestination Resolve(
        string? configuredPackPath,
        string appDirectory,
        string localAppData,
        Func<string, bool> isWritable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);
        ArgumentNullException.ThrowIfNull(isWritable);

        if (!string.IsNullOrWhiteSpace(configuredPackPath))
        {
            return new AiPackDestination(Path.GetFullPath(configuredPackPath.Trim()), false);
        }

        var nextToApp = Path.Combine(appDirectory, ManifestAiPackLocator.DefaultFolderName);
        return isWritable(nextToApp)
            ? new AiPackDestination(nextToApp, false)
            : new AiPackDestination(Path.Combine(localAppData, LocalAppDataFolder, ManifestAiPackLocator.DefaultFolderName), true);
    }
}
