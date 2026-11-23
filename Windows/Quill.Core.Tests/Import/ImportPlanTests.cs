using System.Text.Json.Nodes;
using Quill.Core.Domain;
using Quill.Core.Import;
using Quill.Windows.Sessions;
using Xunit;

namespace Quill.Core.Tests.Import;

public sealed class ImportPlanTests
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 20, 9, 30, 15, TimeSpan.FromHours(1));

    private static ImportSource Source(string name, DateTimeOffset? created = null) =>
        new(Path.Combine("C:", "Users", "ana", "Grabaciones", name), created ?? Morning);

    [Theory]
    [InlineData("reunion.wav", true)]
    [InlineData("reunion.MP3", true)]
    [InlineData("reunion.m4a", true)]
    [InlineData("video.mp4", true)]
    [InlineData("notas.txt", false)]
    [InlineData("sin-extension", false)]
    public void IsSupported_AcceptsOnlyTheImportableFormats(string name, bool expected) =>
        Assert.Equal(expected, ImportPlan.IsSupported(name));

    [Fact]
    public void OneFile_DefaultsToTheSystemTrack_SoItIsTranscribedAsTheOthers()
    {
        var plan = ImportPlan.Create(new[] { Source("Llamada con Ana.m4a") });

        Assert.Equal(ImportMode.Others, plan.Mode);
        var track = Assert.Single(plan.Tracks);
        Assert.Equal(Track.System, track.Track);
        Assert.Equal("system.wav", track.FileName);
        Assert.EndsWith("Llamada con Ana.m4a", track.SourcePath);
        Assert.Equal("Llamada con Ana", plan.Title);
        Assert.Equal(Morning, plan.Started);
    }

    [Fact]
    public void OneFile_MarkedAsMe_GoesToTheMicrophoneTrack()
    {
        var plan = ImportPlan.Create(new[] { Source("dictado.wav") }, ImportRole.Me);

        Assert.Equal(ImportMode.Me, plan.Mode);
        var track = Assert.Single(plan.Tracks);
        Assert.Equal(Track.Microphone, track.Track);
        Assert.Equal("mic.wav", track.FileName);
    }

    [Fact]
    public void OneFile_MarkedAsMixed_StaysOnTheSystemTrack_ForLaterSpeakerSeparation()
    {
        var plan = ImportPlan.Create(new[] { Source("sala.mp3") }, ImportRole.Mixed);

        Assert.Equal(ImportMode.Mixed, plan.Mode);
        Assert.Equal(Track.System, Assert.Single(plan.Tracks).Track);
    }

    [Fact]
    public void TwoFiles_BecomeMicrophoneAndSystem_FirstOneIsTheMicrophoneByDefault()
    {
        var plan = ImportPlan.Create(new[] { Source("a.wav"), Source("b.wav") });

        Assert.Equal(ImportMode.Pair, plan.Mode);
        Assert.Equal(2, plan.Tracks.Count);
        Assert.Equal((Track.Microphone, "mic.wav"), (plan.Tracks[0].Track, plan.Tracks[0].FileName));
        Assert.EndsWith("a.wav", plan.Tracks[0].SourcePath);
        Assert.Equal((Track.System, "system.wav"), (plan.Tracks[1].Track, plan.Tracks[1].FileName));
        Assert.EndsWith("b.wav", plan.Tracks[1].SourcePath);
    }

    [Theory]
    [InlineData("demo-system.wav", "demo-mic.wav")]
    [InlineData("loopback.wav", "microphone.wav")]
    [InlineData("otros.mp3", "yo.mp3")]
    public void TwoFiles_NamesHintWhichOneIsTheMicrophone(string first, string second)
    {
        var plan = ImportPlan.Create(new[] { Source(first), Source(second) });

        Assert.EndsWith(second, plan.Tracks.Single(track => track.Track == Track.Microphone).SourcePath);
        Assert.EndsWith(first, plan.Tracks.Single(track => track.Track == Track.System).SourcePath);
    }

    [Fact]
    public void TwoFiles_Swap_ExchangesTheTracks()
    {
        var plan = ImportPlan.Create(new[] { Source("a.wav"), Source("b.wav") }, swapPair: true);

        Assert.EndsWith("b.wav", plan.Tracks.Single(track => track.Track == Track.Microphone).SourcePath);
        Assert.EndsWith("a.wav", plan.Tracks.Single(track => track.Track == Track.System).SourcePath);
    }

    [Fact]
    public void TwoFiles_TitleIsTheCommonName_AndStartIsTheEarliestFile()
    {
        var later = Morning.AddMinutes(1);
        var plan = ImportPlan.Create(new[] { Source("Comite semanal - mic.wav", later), Source("Comite semanal - system.wav") });

        Assert.Equal("Comite semanal", plan.Title);
        Assert.Equal(Morning, plan.Started);
    }

    [Fact]
    public void TwoFiles_WithoutACommonName_UseTheMicrophoneFileName()
    {
        var plan = ImportPlan.Create(new[] { Source("uno.wav"), Source("dos.wav") });

        Assert.Equal("uno", plan.Title);
    }

    [Fact]
    public void Create_RejectsNoFiles_MoreThanTwo_Unsupported_AndDuplicates()
    {
        Assert.Throws<ArgumentException>(() => ImportPlan.Create(Array.Empty<ImportSource>()));
        Assert.Throws<ArgumentException>(() => ImportPlan.Create(new[] { Source("a.wav"), Source("b.wav"), Source("c.wav") }));
        Assert.Throws<ArgumentException>(() => ImportPlan.Create(new[] { Source("a.txt") }));
        Assert.Throws<ArgumentException>(() => ImportPlan.Create(new[] { Source("a.wav"), Source("A.WAV") }));
    }

    [Fact]
    public void FolderName_FollowsTheRecorderFormat_AndSkipsTakenNames()
    {
        var plan = ImportPlan.Create(new[] { Source("a.wav") });

        Assert.Equal("2026.11.20-0930", plan.FolderName(_ => false));
        var taken = new HashSet<string> { "2026.11.20-0930", "2026.11.20-0930-2" };
        Assert.Equal("2026.11.20-0930-3", plan.FolderName(taken.Contains));
    }

    [Fact]
    public void Metadata_IsReadableByTheSessionFormat_WithTitleOffsetsAndDuration()
    {
        using var temp = new TempDirectory();
        var folder = temp.Session("import");
        var plan = ImportPlan.Create(new[] { Source("Comite - mic.wav"), Source("Comite - system.wav") });

        var json = plan.MetadataJson(new Dictionary<Track, TimeSpan>
        {
            [Track.Microphone] = TimeSpan.FromSeconds(90.4),
            [Track.System] = TimeSpan.FromSeconds(120.6),
        });
        temp.Write(folder, "meta.json", json);
        var metadata = SessionMetadata.Read(folder);

        Assert.Equal("Comite", metadata.Title);
        Assert.Equal(Morning, metadata.Started);
        Assert.Equal(Morning + TimeSpan.FromSeconds(120.6), metadata.Ended);
        Assert.Equal(121, metadata.DurationSeconds);
        Assert.Equal("mic.wav", metadata.Files["mic"]);
        Assert.Equal("system.wav", metadata.Files["system"]);
        Assert.Equal(0, metadata.StartOffsetMs["mic"]);
        Assert.Equal(0, metadata.StartOffsetMs["system"]);
        Assert.Empty(metadata.Markers);
    }

    [Fact]
    public void Metadata_ForOneFile_ListsOnlyThatTrack_AndRecordsTheImportWithoutFullPaths()
    {
        var plan = ImportPlan.Create(new[] { Source("sala.mp3") }, ImportRole.Mixed);

        var json = JsonNode.Parse(plan.MetadataJson(new Dictionary<Track, TimeSpan> { [Track.System] = TimeSpan.FromMinutes(3) }))!;

        var files = json["files"]!.AsObject();
        Assert.Equal(new[] { "system" }, files.Select(pair => pair.Key));
        Assert.Equal("mixed", json["import"]!["mode"]!.GetValue<string>());
        Assert.Equal("sala.mp3", json["import"]!["sources"]![0]!.GetValue<string>());
        Assert.DoesNotContain("Grabaciones", json.ToJsonString());
    }

    [Fact]
    public void Metadata_RequiresADurationForEveryPlannedTrack()
    {
        var plan = ImportPlan.Create(new[] { Source("a.wav"), Source("b.wav") });

        Assert.Throws<ArgumentException>(() =>
            plan.MetadataJson(new Dictionary<Track, TimeSpan> { [Track.Microphone] = TimeSpan.FromSeconds(1) }));
    }
}
