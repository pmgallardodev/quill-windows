using Quill.Core.Domain;
using Quill.Core.Summaries;

namespace Quill.Core.Settings;

/// <summary>Which backend runs local inference (LLM and live Whisper).</summary>
public enum GpuPreference
{
    /// <summary>Vulkan when it loads, CPU otherwise.</summary>
    Auto,

    /// <summary>Prefer Vulkan (still falls back to CPU if the driver is missing).</summary>
    Vulkan,

    /// <summary>Never use the GPU.</summary>
    Cpu,
}

/// <summary>
/// User settings added by Quill Next, stored in config.json next to the v1 keys
/// (<c>recordings_dir</c>, <c>recording</c>, <c>transcription</c>), which stay owned by the
/// Windows ConfigStore. Read and written by <see cref="SettingsDocument"/>; every key is
/// optional and falls back to the defaults below, so old config files keep working.
/// </summary>
public sealed record QuillSettings
{
    public AiSettings Ai { get; init; } = new();

    public SummarySettings Summaries { get; init; } = new();

    public TemplateSettings Templates { get; init; } = new();

    public ExportSettings Export { get; init; } = new();

    public PrivacySettings Privacy { get; init; } = new();
}

/// <summary>config.json "ai" section.</summary>
public sealed record AiSettings
{
    /// <summary><c>pack_path</c>: AI pack root. Null: "&lt;app&gt;/ai-pack" (auto-detect).</summary>
    public string? PackPath { get; init; }

    /// <summary><c>llm_model</c>: GGUF used instead of the pack's language model. Null: the pack's.</summary>
    public string? LanguageModelPath { get; init; }

    /// <summary><c>live_whisper_model</c>: ggml .bin used for live transcription. Null: the pack's.</summary>
    public string? LiveWhisperModelPath { get; init; }

    /// <summary><c>gpu</c>: "auto" | "vulkan" | "cpu".</summary>
    public GpuPreference Gpu { get; init; } = GpuPreference.Auto;

    /// <summary><c>gpu_device</c>: GPU index for hybrid machines (iGPU + dGPU). Null: the backend's default.</summary>
    public int? GpuDevice { get; init; }
}

/// <summary>config.json "summaries" section.</summary>
public sealed record SummarySettings
{
    public const int DefaultLiveCadenceMinutes = 3;
    public const int DefaultCatchUpMinutes = 5;
    public const int MinMinutes = 1;
    public const int MaxMinutes = 60;

    /// <summary><c>language</c>: output language of summaries, independent of the audio language.</summary>
    public string Language { get; init; } = SummaryOptions.DefaultLanguage;

    /// <summary><c>level</c>: "brief" | "detailed".</summary>
    public SummaryLevel Level { get; init; } = SummaryLevel.Brief;

    /// <summary><c>template</c>: default template name.</summary>
    public string Template { get; init; } = SummaryOptions.DefaultTemplateName;

    /// <summary><c>live_enabled</c>: null means automatic (on only when a GPU is usable, design §7.2).</summary>
    public bool? LiveEnabled { get; init; }

    /// <summary><c>live_cadence_minutes</c>: live summary refresh, in minutes of new speech.</summary>
    public int LiveCadenceMinutes { get; init; } = DefaultLiveCadenceMinutes;

    /// <summary><c>catch_up_minutes</c>: window of "What did I miss?".</summary>
    public int CatchUpMinutes { get; init; } = DefaultCatchUpMinutes;

    public TimeSpan LiveCadence => TimeSpan.FromMinutes(LiveCadenceMinutes);

    public TimeSpan CatchUpWindow => TimeSpan.FromMinutes(CatchUpMinutes);

    public bool IsLiveSummaryEnabled(bool gpuAvailable) => LiveEnabled ?? gpuAvailable;

    /// <summary>
    /// Options for a final summary with <paramref name="template"/>. A language declared by the
    /// template wins (a template written for English output stays English); otherwise this
    /// language applies. A template declaring "detailed" wins; otherwise this level applies
    /// ("brief" is the parser default and cannot be told apart from "not declared").
    /// </summary>
    public SummaryOptions ToOptions(Template? template) => new()
    {
        Template = template,
        Language = string.IsNullOrWhiteSpace(template?.Language) ? Language : null,
        Level = template?.Level == SummaryLevel.Detailed ? SummaryLevel.Detailed : Level,
    };
}

/// <summary>config.json "templates" section.</summary>
public sealed record TemplateSettings
{
    /// <summary><c>folder</c>: user templates folder. Null: "%APPDATA%/Quill/templates".</summary>
    public string? Folder { get; init; }

    /// <param name="roamingAppData">The platform's roaming application data folder (%APPDATA%).</param>
    public string ResolveFolder(string roamingAppData) =>
        string.IsNullOrWhiteSpace(Folder) ? Path.Combine(roamingAppData, "Quill", "templates") : Folder;
}

/// <summary>config.json "export" section (Markdown / Obsidian export).</summary>
public sealed record ExportSettings
{
    /// <summary><c>folder</c>: where meeting .md files are written. Null: export is not configured.</summary>
    public string? Folder { get; init; }

    /// <summary><c>auto_export</c>: export after every final summary. Off by default.</summary>
    public bool AutoExport { get; init; }

    /// <summary><c>overwrite_edited</c>: replace exports the user edited by hand. Off by default.</summary>
    public bool OverwriteEdited { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Folder);
}
