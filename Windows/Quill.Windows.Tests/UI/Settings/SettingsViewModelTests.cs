using Quill.Core.Domain;
using Quill.Core.Ports;
using Quill.Core.Settings;
using Quill.Core.Templates;
using Quill.Windows.UI.Settings;
using Xunit;

namespace Quill.Windows.Tests.UI.Settings;

public sealed class SettingsViewModelTests
{
    private sealed class FixedLocator(AiPackStatus status) : IAiPackLocator
    {
        public AiPackStatus Locate() => status;
    }

    private static SettingsViewModel Create(QuillSettings settings, string templatesFolder, AiPackStatus? pack = null) =>
        new(settings, _ => new FixedLocator(pack ?? AiPackStatus.NotInstalled), _ => templatesFolder);

    [Fact]
    public void UnchangedFieldsRebuildTheSameSettings()
    {
        using var temp = new TempDirectory();
        var settings = new QuillSettings
        {
            Ai = new AiSettings { PackPath = @"D:\pack", Gpu = GpuPreference.Cpu, GpuDevice = 1 },
            Summaries = new SummarySettings { Language = "en", Level = SummaryLevel.Detailed, LiveEnabled = false, LiveCadenceMinutes = 7 },
            Export = new ExportSettings { Folder = @"D:\vault", AutoExport = true },
        };
        var viewModel = Create(settings, temp.Path);

        Assert.True(viewModel.TryBuild(out var rebuilt));
        Assert.Equal(settings, rebuilt);
    }

    [Theory]
    [InlineData("0", "3", "")]
    [InlineData("3", "abc", "")]
    [InlineData("3", "5", "-1")]
    public void InvalidNumbersBlockSavingWithAMessage(string cadence, string catchUp, string device)
    {
        using var temp = new TempDirectory();
        var viewModel = Create(new QuillSettings(), temp.Path);
        viewModel.CadenceText = cadence;
        viewModel.CatchUpText = catchUp;
        viewModel.GpuDeviceText = device;

        Assert.False(viewModel.TryBuild(out _));
        Assert.True(viewModel.HasError);
        Assert.DoesNotContain("[", viewModel.Error);
    }

    [Fact]
    public void MissingPackShowsInstallGuidance()
    {
        using var temp = new TempDirectory();

        Assert.True(Create(new QuillSettings(), temp.Path).ShowInstallGuidance);
    }

    [Fact]
    public void TemplatesListShowsUserTemplatesAndLocalizedProblems()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Path, "ventas.md"), "---\nname: ventas\n---\nVende.");
        File.WriteAllText(Path.Combine(temp.Path, "rota.md"), "---\nname: rota\nsections: [agenda]\n---\nCuerpo");

        var viewModel = Create(new QuillSettings(), temp.Path);

        Assert.Equal(DefaultTemplates.All.Count + 1, viewModel.Templates.Count);
        Assert.Contains("ventas", viewModel.TemplateNames);
        var problem = Assert.Single(viewModel.TemplateProblems);
        Assert.Contains("rota.md", problem);
        Assert.Contains("agenda", problem);
        Assert.DoesNotContain("[Settings_", problem);
    }

    [Fact]
    public void EveryIssueKindHasALocalizedMessage()
    {
        foreach (var kind in Enum.GetValues<TemplateIssueKind>())
        {
            Assert.DoesNotContain("[Settings_", SettingsViewModel.IssueLabel(new TemplateIssue(kind, "x")));
        }

        foreach (var component in Enum.GetValues<AiPackComponent>())
        {
            var status = new AiPackStatus(true, "/p", "1", new Dictionary<AiPackComponent, string>(), [component]);
            Assert.DoesNotContain("[Settings_", SettingsViewModel.PackStatusLabel(status));
        }

        foreach (var section in SummarySections.All)
        {
            Assert.DoesNotContain("[", SettingsViewModel.SectionLabel(section));
        }
    }
}
