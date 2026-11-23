using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Quill.Core.Domain;
using Quill.Core.Export;
using Quill.Core.Ports;
using Quill.Core.Settings;
using Quill.Core.Summaries;
using Quill.Core.Templates;
using Quill.Windows.Resources;

namespace Quill.Windows.UI.Settings;

/// <summary>A labelled option for a ComboBox (DisplayMemberPath="Label", SelectedValuePath="Value").</summary>
internal sealed record Choice<T>(T Value, string Label);

/// <summary>One template in the Plantillas list, with its preview text already localized.</summary>
internal sealed record TemplateRow(
    string Name,
    string Description,
    string SourceLabel,
    bool IsBuiltInOrOverride,
    string? FilePath,
    string SectionsLabel,
    string LevelLabel,
    string LanguageLabel,
    string Body);

/// <summary>
/// State of the Settings window. Holds editable text (numbers are validated on save, not on
/// every keystroke) and builds a <see cref="QuillSettings"/> with <see cref="TryBuild"/>.
/// No WPF types: dialogs and shell calls stay in <see cref="SettingsWindow"/>.
/// </summary>
internal sealed class SettingsViewModel : INotifyPropertyChanged
{
    private static readonly string[] KnownLanguages = ["es", "en", "pt", "fr", "de", "it", "ca"];

    private readonly Func<QuillSettings, IAiPackLocator> _locatorFactory;
    private readonly Func<QuillSettings, string> _templatesFolderResolver;
    private readonly MeetingExportService? _exporter;
    private readonly QuillSettings _original;

    private string _packPath;
    private string _languageModelPath;
    private string _liveWhisperModelPath;
    private GpuPreference _gpu;
    private string _gpuDeviceText;
    private string _language;
    private SummaryLevel _level;
    private string _defaultTemplate;
    private int _liveMode;
    private string _cadenceText;
    private string _catchUpText;
    private string _templatesFolder;
    private string _exportFolder;
    private bool _autoExport;
    private bool _overwriteEdited;
    private string _packStatusText = string.Empty;
    private bool _showInstallGuidance;
    private TemplateRow? _selectedTemplate;
    private string _templatesMessage = string.Empty;
    private string _exportMessage = string.Empty;
    private bool _isExporting;
    private string? _error;

    public SettingsViewModel(
        QuillSettings settings,
        Func<QuillSettings, IAiPackLocator> locatorFactory,
        Func<QuillSettings, string> templatesFolderResolver,
        MeetingExportService? exporter = null,
        string configPath = "",
        string recordingsFolder = "")
    {
        _original = settings ?? throw new ArgumentNullException(nameof(settings));
        _locatorFactory = locatorFactory ?? throw new ArgumentNullException(nameof(locatorFactory));
        _templatesFolderResolver = templatesFolderResolver ?? throw new ArgumentNullException(nameof(templatesFolderResolver));
        _exporter = exporter;
        ConfigPath = configPath;
        RecordingsFolder = recordingsFolder;

        _packPath = settings.Ai.PackPath ?? string.Empty;
        _languageModelPath = settings.Ai.LanguageModelPath ?? string.Empty;
        _liveWhisperModelPath = settings.Ai.LiveWhisperModelPath ?? string.Empty;
        _gpu = settings.Ai.Gpu;
        _gpuDeviceText = settings.Ai.GpuDevice?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _language = settings.Summaries.Language;
        _level = settings.Summaries.Level;
        _defaultTemplate = settings.Summaries.Template;
        _liveMode = settings.Summaries.LiveEnabled switch { null => 0, true => 1, false => 2 };
        _cadenceText = settings.Summaries.LiveCadenceMinutes.ToString(CultureInfo.InvariantCulture);
        _catchUpText = settings.Summaries.CatchUpMinutes.ToString(CultureInfo.InvariantCulture);
        _templatesFolder = settings.Templates.Folder ?? string.Empty;
        _exportFolder = settings.Export.Folder ?? string.Empty;
        _autoExport = settings.Export.AutoExport;
        _overwriteEdited = settings.Export.OverwriteEdited;

        GpuChoices =
        [
            new(GpuPreference.Auto, Loc.Get("Settings_Ai_Gpu_Auto")),
            new(GpuPreference.Vulkan, Loc.Get("Settings_Ai_Gpu_Vulkan")),
            new(GpuPreference.Cpu, Loc.Get("Settings_Ai_Gpu_Cpu")),
        ];
        LevelChoices = [new(SummaryLevel.Brief, LevelLabel(SummaryLevel.Brief)), new(SummaryLevel.Detailed, LevelLabel(SummaryLevel.Detailed))];
        LiveChoices = [Loc.Get("Settings_Live_Auto"), Loc.Get("Settings_Live_On"), Loc.Get("Settings_Live_Off")];
        LanguageChoices = KnownLanguages.Select(code => new Choice<string>(code, Loc.Get("Settings_Language_" + code))).ToList();
        if (!KnownLanguages.Contains(_language, StringComparer.OrdinalIgnoreCase))
        {
            LanguageChoices.Add(new Choice<string>(_language, _language));
        }

        RefreshPackStatus();
        ReloadTemplates();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ConfigPath { get; }

    public string RecordingsFolder { get; }

    public IReadOnlyList<Choice<GpuPreference>> GpuChoices { get; }

    public IReadOnlyList<Choice<SummaryLevel>> LevelChoices { get; }

    public IReadOnlyList<string> LiveChoices { get; }

    public List<Choice<string>> LanguageChoices { get; }

    public ObservableCollection<TemplateRow> Templates { get; } = [];

    public ObservableCollection<string> TemplateNames { get; } = [];

    public ObservableCollection<string> TemplateProblems { get; } = [];

    public string PackPath { get => _packPath; set => Set(ref _packPath, value); }

    public string LanguageModelPath { get => _languageModelPath; set => Set(ref _languageModelPath, value); }

    public string LiveWhisperModelPath { get => _liveWhisperModelPath; set => Set(ref _liveWhisperModelPath, value); }

    public GpuPreference Gpu { get => _gpu; set => Set(ref _gpu, value); }

    public string GpuDeviceText { get => _gpuDeviceText; set => Set(ref _gpuDeviceText, value); }

    public string Language { get => _language; set => Set(ref _language, value); }

    public SummaryLevel Level { get => _level; set => Set(ref _level, value); }

    public string DefaultTemplate { get => _defaultTemplate; set => Set(ref _defaultTemplate, value); }

    /// <summary>0 automatic, 1 on, 2 off (index into <see cref="LiveChoices"/>).</summary>
    public int LiveMode { get => _liveMode; set => Set(ref _liveMode, Math.Clamp(value, 0, 2)); }

    public string CadenceText { get => _cadenceText; set => Set(ref _cadenceText, value); }

    public string CatchUpText { get => _catchUpText; set => Set(ref _catchUpText, value); }

    public string TemplatesFolder { get => _templatesFolder; set => Set(ref _templatesFolder, value); }

    public string ExportFolder
    {
        get => _exportFolder;
        set
        {
            if (Set(ref _exportFolder, value))
            {
                OnPropertyChanged(nameof(CanExportAll));
            }
        }
    }

    public bool AutoExport { get => _autoExport; set => Set(ref _autoExport, value); }

    public bool OverwriteEdited { get => _overwriteEdited; set => Set(ref _overwriteEdited, value); }

    public string PackStatusText { get => _packStatusText; private set => Set(ref _packStatusText, value); }

    public bool ShowInstallGuidance { get => _showInstallGuidance; private set => Set(ref _showInstallGuidance, value); }

    public TemplateRow? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (Set(ref _selectedTemplate, value))
            {
                OnPropertyChanged(nameof(HasSelectedTemplate));
            }
        }
    }

    public bool HasSelectedTemplate => SelectedTemplate is not null;

    public string TemplatesMessage { get => _templatesMessage; set => Set(ref _templatesMessage, value); }

    public string ExportMessage { get => _exportMessage; private set => Set(ref _exportMessage, value); }

    public bool CanExportAll => _exporter is not null && !_isExporting && !string.IsNullOrWhiteSpace(ExportFolder);

    /// <summary>Validation or save error shown above the buttons; null when there is none.</summary>
    public string? Error
    {
        get => _error;
        set
        {
            if (Set(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>The folder the templates list reads, honoring the unsaved field value.</summary>
    public string ResolvedTemplatesFolder => _templatesFolderResolver(Current() with
    {
        Templates = new TemplateSettings { Folder = Blank(TemplatesFolder) },
    });

    public void RefreshPackStatus()
    {
        AiPackStatus status;
        try
        {
            status = _locatorFactory(Current()).Locate();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            status = AiPackStatus.NotInstalled;
        }

        PackStatusText = PackStatusLabel(status);
        ShowInstallGuidance = !status.Has(AiPackComponent.LanguageModel);
    }

    public void ReloadTemplates()
    {
        var previous = SelectedTemplate?.Name;
        var snapshot = new TemplateCatalog(ResolvedTemplatesFolder).Load();

        Templates.Clear();
        TemplateNames.Clear();
        foreach (var entry in snapshot.Entries)
        {
            Templates.Add(Row(entry));
            TemplateNames.Add(entry.Template.Name);
        }

        if (!TemplateNames.Contains(DefaultTemplate, StringComparer.OrdinalIgnoreCase))
        {
            TemplateNames.Add(DefaultTemplate);
        }

        TemplateProblems.Clear();
        foreach (var problem in snapshot.Problems)
        {
            TemplateProblems.Add(ProblemLabel(problem));
        }

        TemplatesMessage = snapshot.Problems.Count == 0 ? Loc.Get("Settings_Templates_NoProblems") : string.Empty;
        SelectedTemplate = Templates.FirstOrDefault(row => row.Name == previous) ?? Templates.FirstOrDefault();
    }

    /// <summary>Copies the selected built-in into the user folder; returns the file to open, or null.</summary>
    public string? CustomizeSelected()
    {
        if (SelectedTemplate is not { } row)
        {
            return null;
        }

        if (!row.IsBuiltInOrOverride && row.FilePath is not null)
        {
            return row.FilePath;
        }

        var catalog = new TemplateCatalog(ResolvedTemplatesFolder);
        var (path, created) = catalog.CreateFromBuiltIn(row.Name);
        TemplatesMessage = Loc.Format(created ? "Settings_Templates_Created" : "Settings_Templates_Exists", path);
        ReloadTemplates();
        return path;
    }

    public async Task ExportAllAsync(CancellationToken cancellationToken)
    {
        if (_exporter is null)
        {
            ExportMessage = Loc.Get("Export_NotAvailable");
            return;
        }

        if (string.IsNullOrWhiteSpace(ExportFolder))
        {
            ExportMessage = Loc.Get("Export_NoFolder");
            return;
        }

        _isExporting = true;
        OnPropertyChanged(nameof(CanExportAll));
        try
        {
            var progress = new Progress<int>(done => ExportMessage = Loc.Format("Export_Running", done));
            var results = await _exporter.ExportAllAsync(Current().Export, progress, cancellationToken);
            ExportMessage = ExportSummary(results);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ExportMessage = exception.Message;
        }
        finally
        {
            _isExporting = false;
            OnPropertyChanged(nameof(CanExportAll));
        }
    }

    /// <summary>Validates the fields; on failure sets <see cref="Error"/> and returns false.</summary>
    public bool TryBuild(out QuillSettings settings)
    {
        settings = _original;
        int? device = null;
        if (!string.IsNullOrWhiteSpace(GpuDeviceText))
        {
            if (!int.TryParse(GpuDeviceText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
            {
                Error = Loc.Get("Settings_Error_GpuDevice");
                return false;
            }

            device = parsed;
        }

        if (!TryMinutes(CadenceText, "Settings_Summaries_Cadence", out var cadence) ||
            !TryMinutes(CatchUpText, "Settings_Summaries_CatchUp", out var catchUp))
        {
            return false;
        }

        settings = Current() with
        {
            Ai = Current().Ai with { GpuDevice = device },
            Summaries = Current().Summaries with { LiveCadenceMinutes = cadence, CatchUpMinutes = catchUp },
        };
        Error = null;
        return true;
    }

    public static string IssueLabel(TemplateIssue issue) =>
        Loc.Format("Settings_TemplateIssue_" + issue.Kind, issue.Detail ?? string.Empty);

    public static string ProblemLabel(TemplateFileProblem problem) =>
        Loc.Format("Settings_Templates_Problem", Path.GetFileName(problem.FilePath), string.Join("; ", problem.Issues.Select(IssueLabel)));

    public static string PackStatusLabel(AiPackStatus status)
    {
        if (!status.IsInstalled)
        {
            return Loc.Format("Settings_Ai_PackMissing", status.Root ?? string.Empty);
        }

        var installed = Loc.Format("Settings_Ai_PackInstalled", status.Version ?? "?", status.Root ?? string.Empty);
        return status.Missing.Count == 0
            ? installed
            : installed + " " + Loc.Format(
                "Settings_Ai_PackPartial",
                string.Join(", ", status.Missing.Select(component => Loc.Get("Settings_Ai_Component_" + component))));
    }

    public static string ExportSummary(IReadOnlyList<MarkdownExportResult> results) =>
        Loc.Format(
            "Export_Done",
            results.Count(r => r.Status == MarkdownExportStatus.Created),
            results.Count(r => r.Status == MarkdownExportStatus.Updated),
            results.Count(r => r.Status == MarkdownExportStatus.Unchanged),
            results.Count(r => r.Status == MarkdownExportStatus.SkippedEdited),
            results.Count(r => r.Status == MarkdownExportStatus.Failed));

    /// <summary>Settings from the fields that need no validation (numbers keep their original values).</summary>
    private QuillSettings Current() => _original with
    {
        Ai = _original.Ai with
        {
            PackPath = Blank(PackPath),
            LanguageModelPath = Blank(LanguageModelPath),
            LiveWhisperModelPath = Blank(LiveWhisperModelPath),
            Gpu = Gpu,
        },
        Summaries = _original.Summaries with
        {
            Language = Blank(Language) ?? SummaryOptions.DefaultLanguage,
            Level = Level,
            Template = Blank(DefaultTemplate) ?? SummaryOptions.DefaultTemplateName,
            LiveEnabled = LiveMode switch { 1 => true, 2 => false, _ => null },
        },
        Templates = new TemplateSettings { Folder = Blank(TemplatesFolder) },
        Export = new ExportSettings { Folder = Blank(ExportFolder), AutoExport = AutoExport, OverwriteEdited = OverwriteEdited },
    };

    private bool TryMinutes(string text, string labelKey, out int minutes)
    {
        if (int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes) &&
            minutes is >= SummarySettings.MinMinutes and <= SummarySettings.MaxMinutes)
        {
            return true;
        }

        Error = Loc.Format("Settings_Error_Minutes", Loc.Get(labelKey), SummarySettings.MinMinutes, SummarySettings.MaxMinutes);
        return false;
    }

    private static TemplateRow Row(TemplateEntry entry)
    {
        var template = entry.Template;
        var source = entry.Source == TemplateSource.BuiltIn
            ? "Settings_Templates_Source_BuiltIn"
            : entry.OverridesBuiltIn ? "Settings_Templates_Source_Override" : "Settings_Templates_Source_User";
        var sections = template.Sections.Count == 0
            ? Loc.Get("Settings_Templates_AllSections")
            : string.Join(", ", template.Sections.Select(SectionLabel));
        return new TemplateRow(
            template.Name,
            template.Description,
            Loc.Get(source),
            entry.Source == TemplateSource.BuiltIn || entry.OverridesBuiltIn,
            entry.FilePath,
            sections,
            LevelLabel(template.Level),
            string.IsNullOrWhiteSpace(template.Language) ? Loc.Get("Settings_Templates_LanguageDefault") : template.Language,
            template.Body);
    }

    /// <summary>"actionItems" → Summary_ActionItems ("Tareas").</summary>
    public static string SectionLabel(string section) =>
        section.Length == 0 ? section : Loc.Get("Summary_" + char.ToUpperInvariant(section[0]) + section[1..]);

    private static string LevelLabel(SummaryLevel level) =>
        Loc.Get(level == SummaryLevel.Detailed ? "Settings_Level_Detailed" : "Settings_Level_Brief");

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
