using System.Text.Json;
using System.Text.Json.Nodes;
using Quill.Core.Domain;

namespace Quill.Core.Settings;

/// <summary>
/// Reads and writes <see cref="QuillSettings"/> inside config.json. Parsing is lenient key by
/// key: a missing or invalid value falls back to its default without discarding the rest.
/// Merging only touches the keys it owns, so v1 keys and keys added by newer versions survive.
/// </summary>
public static class SettingsDocument
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static QuillSettings Parse(string? json)
    {
        JsonObject? root;
        try
        {
            root = string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            root = null;
        }

        if (root is null)
        {
            return new QuillSettings();
        }

        var ai = root["ai"] as JsonObject;
        var summaries = root["summaries"] as JsonObject;
        var templates = root["templates"] as JsonObject;
        var export = root["export"] as JsonObject;
        var privacy = root["privacy"] as JsonObject;
        var defaults = new SummarySettings();

        return new QuillSettings
        {
            Ai = new AiSettings
            {
                PackPath = Text(ai, "pack_path"),
                LanguageModelPath = Text(ai, "llm_model"),
                LiveWhisperModelPath = Text(ai, "live_whisper_model"),
                Gpu = Text(ai, "gpu")?.ToLowerInvariant() switch
                {
                    "vulkan" => GpuPreference.Vulkan,
                    "cpu" => GpuPreference.Cpu,
                    _ => GpuPreference.Auto,
                },
                GpuDevice = Integer(ai, "gpu_device") is { } device && device >= 0 ? device : null,
            },
            Summaries = new SummarySettings
            {
                Language = Text(summaries, "language") ?? defaults.Language,
                Level = Text(summaries, "level")?.ToLowerInvariant() == "detailed" ? SummaryLevel.Detailed : SummaryLevel.Brief,
                Template = Text(summaries, "template") ?? defaults.Template,
                LiveEnabled = Boolean(summaries, "live_enabled"),
                LiveCadenceMinutes = Minutes(summaries, "live_cadence_minutes", defaults.LiveCadenceMinutes),
                CatchUpMinutes = Minutes(summaries, "catch_up_minutes", defaults.CatchUpMinutes),
            },
            Templates = new TemplateSettings { Folder = Text(templates, "folder") },
            Export = new ExportSettings
            {
                Folder = Text(export, "folder"),
                AutoExport = Boolean(export, "auto_export") ?? false,
                OverwriteEdited = Boolean(export, "overwrite_edited") ?? false,
            },
            Privacy = new PrivacySettings
            {
                DiscreetMode = Boolean(privacy, "discreet_mode") ?? true,
                NeutralTrayIcon = Boolean(privacy, "neutral_tray_icon") ?? true,
                CompanionHotkey = HotkeyGesture.TryParse(Text(privacy, "companion_hotkey"), out var gesture)
                    ? gesture!.ToString()
                    : HotkeyGesture.DefaultCompanion.ToString(),
            },
        };
    }

    /// <summary>Returns <paramref name="existingJson"/> with the settings keys replaced. Null values are removed.</summary>
    /// <exception cref="FormatException">The existing file is not a JSON object; it is never overwritten blindly.</exception>
    public static string Merge(string? existingJson, QuillSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        JsonObject root;
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            root = [];
        }
        else
        {
            try
            {
                root = JsonNode.Parse(existingJson) as JsonObject
                    ?? throw new FormatException("config.json is not a JSON object");
            }
            catch (JsonException exception)
            {
                throw new FormatException("config.json is not valid JSON", exception);
            }
        }

        var ai = Section(root, "ai");
        Set(ai, "pack_path", settings.Ai.PackPath);
        Set(ai, "llm_model", settings.Ai.LanguageModelPath);
        Set(ai, "live_whisper_model", settings.Ai.LiveWhisperModelPath);
        Set(ai, "gpu", settings.Ai.Gpu.ToString().ToLowerInvariant());
        Set(ai, "gpu_device", settings.Ai.GpuDevice is { } device ? JsonValue.Create(device) : null);

        var summaries = Section(root, "summaries");
        Set(summaries, "language", settings.Summaries.Language);
        Set(summaries, "level", settings.Summaries.Level == SummaryLevel.Detailed ? "detailed" : "brief");
        Set(summaries, "template", settings.Summaries.Template);
        Set(summaries, "live_enabled", settings.Summaries.LiveEnabled is { } live ? JsonValue.Create(live) : null);
        Set(summaries, "live_cadence_minutes", JsonValue.Create(settings.Summaries.LiveCadenceMinutes));
        Set(summaries, "catch_up_minutes", JsonValue.Create(settings.Summaries.CatchUpMinutes));

        Set(Section(root, "templates"), "folder", settings.Templates.Folder);

        var export = Section(root, "export");
        Set(export, "folder", settings.Export.Folder);
        Set(export, "auto_export", JsonValue.Create(settings.Export.AutoExport));
        Set(export, "overwrite_edited", JsonValue.Create(settings.Export.OverwriteEdited));

        var privacy = Section(root, "privacy");
        Set(privacy, "discreet_mode", JsonValue.Create(settings.Privacy.DiscreetMode));
        Set(privacy, "neutral_tray_icon", JsonValue.Create(settings.Privacy.NeutralTrayIcon));
        Set(privacy, "companion_hotkey", settings.Privacy.CompanionGesture.ToString());

        return root.ToJsonString(WriteOptions);
    }

    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject section)
        {
            return section;
        }

        section = [];
        root[name] = section;
        return section;
    }

    private static void Set(JsonObject section, string key, string? value) =>
        Set(section, key, string.IsNullOrWhiteSpace(value) ? null : JsonValue.Create(value));

    private static void Set(JsonObject section, string key, JsonNode? value)
    {
        if (value is null)
        {
            section.Remove(key);
        }
        else
        {
            section[key] = value;
        }
    }

    private static string? Text(JsonObject? section, string key) =>
        section?[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static bool? Boolean(JsonObject? section, string key) =>
        section?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static int? Integer(JsonObject? section, string key) =>
        section?[key] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var number)
            ? number
            : null;

    private static int Minutes(JsonObject? section, string key, int fallback) =>
        Integer(section, key) is { } minutes
            ? Math.Clamp(minutes, SummarySettings.MinMinutes, SummarySettings.MaxMinutes)
            : fallback;
}
