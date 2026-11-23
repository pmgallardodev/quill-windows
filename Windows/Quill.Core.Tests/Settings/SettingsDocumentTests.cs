using System.Text.Json.Nodes;
using Quill.Core.Domain;
using Quill.Core.Settings;
using Quill.Core.Templates;
using Xunit;

namespace Quill.Core.Tests.Settings;

public sealed class SettingsDocumentTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{ \"recording\": { \"quality\": \"voice\" } }")]
    public void MissingSectionsResolveToDefaults(string? json)
    {
        var settings = SettingsDocument.Parse(json);

        Assert.Equal(new QuillSettings(), settings);
        Assert.Null(settings.Ai.PackPath);
        Assert.Equal(GpuPreference.Auto, settings.Ai.Gpu);
        Assert.Equal("es", settings.Summaries.Language);
        Assert.Equal(SummaryLevel.Brief, settings.Summaries.Level);
        Assert.Equal("general", settings.Summaries.Template);
        Assert.Null(settings.Summaries.LiveEnabled);
        Assert.Equal(3, settings.Summaries.LiveCadenceMinutes);
        Assert.Equal(5, settings.Summaries.CatchUpMinutes);
        Assert.Null(settings.Templates.Folder);
        Assert.Null(settings.Export.Folder);
        Assert.False(settings.Export.AutoExport);
        Assert.False(settings.Export.OverwriteEdited);
    }

    [Fact]
    public void ReadsEveryKey()
    {
        const string json = """
            {
              "ai": { "pack_path": "D:\\pack", "llm_model": "D:\\m.gguf", "live_whisper_model": "D:\\s.bin",
                      "gpu": "Vulkan", "gpu_device": 1 },
              "summaries": { "language": "en", "level": "detailed", "template": "daily",
                             "live_enabled": true, "live_cadence_minutes": 5, "catch_up_minutes": 10 },
              "templates": { "folder": "D:\\templates" },
              "export": { "folder": "D:\\vault", "auto_export": true, "overwrite_edited": true }
            }
            """;

        var settings = SettingsDocument.Parse(json);

        Assert.Equal(@"D:\pack", settings.Ai.PackPath);
        Assert.Equal(@"D:\m.gguf", settings.Ai.LanguageModelPath);
        Assert.Equal(@"D:\s.bin", settings.Ai.LiveWhisperModelPath);
        Assert.Equal(GpuPreference.Vulkan, settings.Ai.Gpu);
        Assert.Equal(1, settings.Ai.GpuDevice);
        Assert.Equal("en", settings.Summaries.Language);
        Assert.Equal(SummaryLevel.Detailed, settings.Summaries.Level);
        Assert.Equal("daily", settings.Summaries.Template);
        Assert.True(settings.Summaries.LiveEnabled);
        Assert.Equal(5, settings.Summaries.LiveCadenceMinutes);
        Assert.Equal(10, settings.Summaries.CatchUpMinutes);
        Assert.Equal(@"D:\templates", settings.Templates.Folder);
        Assert.Equal(@"D:\vault", settings.Export.Folder);
        Assert.True(settings.Export.AutoExport);
        Assert.True(settings.Export.OverwriteEdited);
    }

    [Fact]
    public void InvalidValuesFallBackToDefaultsOneByOne()
    {
        const string json = """
            {
              "ai": { "pack_path": "   ", "gpu": "cuda", "gpu_device": -2 },
              "summaries": { "language": 3, "level": "huge", "template": "",
                             "live_cadence_minutes": 0, "catch_up_minutes": 1000 },
              "export": { "auto_export": "yes" }
            }
            """;

        var settings = SettingsDocument.Parse(json);

        Assert.Null(settings.Ai.PackPath);
        Assert.Equal(GpuPreference.Auto, settings.Ai.Gpu);
        Assert.Null(settings.Ai.GpuDevice);
        Assert.Equal("es", settings.Summaries.Language);
        Assert.Equal(SummaryLevel.Brief, settings.Summaries.Level);
        Assert.Equal("general", settings.Summaries.Template);
        Assert.Equal(SummarySettings.MinMinutes, settings.Summaries.LiveCadenceMinutes);
        Assert.Equal(SummarySettings.MaxMinutes, settings.Summaries.CatchUpMinutes);
        Assert.False(settings.Export.AutoExport);
    }

    [Fact]
    public void MalformedJsonResolvesToDefaults()
    {
        Assert.Equal(new QuillSettings(), SettingsDocument.Parse("{ not json"));
    }

    [Fact]
    public void MergeRoundTripsAndKeepsUnrelatedKeys()
    {
        const string existing = """
            { "recordings_dir": "D:\\rec", "recording": { "quality": "native" },
              "ai": { "future_key": 7, "gpu": "cpu" } }
            """;
        var settings = new QuillSettings
        {
            Ai = new AiSettings { PackPath = @"E:\pack", Gpu = GpuPreference.Cpu, GpuDevice = 0 },
            Summaries = new SummarySettings { Language = "en", LiveEnabled = false, LiveCadenceMinutes = 4 },
            Export = new ExportSettings { Folder = @"E:\vault", AutoExport = true },
        };

        var merged = SettingsDocument.Merge(existing, settings);
        var root = JsonNode.Parse(merged)!.AsObject();

        Assert.Equal(settings, SettingsDocument.Parse(merged));
        Assert.Equal(@"D:\rec", (string?)root["recordings_dir"]);
        Assert.Equal("native", (string?)root["recording"]!["quality"]);
        Assert.Equal(7, (int?)root["ai"]!["future_key"]);
        Assert.Equal("cpu", (string?)root["ai"]!["gpu"]);
        Assert.False(root["ai"]!.AsObject().ContainsKey("llm_model"));
    }

    [Fact]
    public void MergeIntoMissingFileCreatesTheSections()
    {
        var merged = SettingsDocument.Merge(null, new QuillSettings());

        Assert.Equal(new QuillSettings(), SettingsDocument.Parse(merged));
        Assert.NotNull(JsonNode.Parse(merged)!["summaries"]);
    }

    [Fact]
    public void MergeRefusesToOverwriteABrokenFile()
    {
        Assert.Throws<FormatException>(() => SettingsDocument.Merge("{ broken", new QuillSettings()));
        Assert.Throws<FormatException>(() => SettingsDocument.Merge("[1, 2]", new QuillSettings()));
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public void LiveSummaryDefaultsToOnOnlyWithAGpu(bool? configured, bool gpuAvailable, bool expected)
    {
        var settings = new SummarySettings { LiveEnabled = configured };

        Assert.Equal(expected, settings.IsLiveSummaryEnabled(gpuAvailable));
    }

    [Fact]
    public void SummaryOptionsLetTemplateLanguageAndDetailedLevelWin()
    {
        var settings = new SummarySettings { Language = "en", Level = SummaryLevel.Brief };
        var planning = DefaultTemplates.Find("planning")!;
        var general = DefaultTemplates.Find("general")!;
        var german = general with { Language = "de" };

        Assert.Equal("en", settings.ToOptions(general).ResolvedLanguage);
        Assert.Equal(SummaryLevel.Brief, settings.ToOptions(general).ResolvedLevel);
        Assert.Equal(SummaryLevel.Detailed, settings.ToOptions(planning).ResolvedLevel);
        Assert.Equal("de", settings.ToOptions(german).ResolvedLanguage);
        Assert.Equal(SummaryLevel.Detailed, (settings with { Level = SummaryLevel.Detailed }).ToOptions(general).ResolvedLevel);
        Assert.Same(general, settings.ToOptions(general).Template);
    }

    [Fact]
    public void TemplatesFolderDefaultsUnderRoamingAppData()
    {
        var appData = Path.Combine("C:", "Users", "ana", "AppData", "Roaming");

        Assert.Equal(Path.Combine(appData, "Quill", "templates"), new TemplateSettings().ResolveFolder(appData));
        Assert.Equal("X", new TemplateSettings { Folder = "X" }.ResolveFolder(appData));
    }
}
