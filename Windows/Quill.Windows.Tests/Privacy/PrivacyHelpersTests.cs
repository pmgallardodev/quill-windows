using Quill.Core.Settings;
using Quill.Windows.Privacy;
using Xunit;

namespace Quill.Windows.Tests.Privacy;

public sealed class QuillNotifierTests
{
    private readonly List<QuillNotification> _shown = [];
    private bool _discreet;

    private QuillNotifier Create() => new(_shown.Add, () => _discreet, () => DateTimeOffset.UnixEpoch);

    [Fact]
    public void OutsideDiscreetModeShowsTheOsNotification()
    {
        var notifier = Create();
        var inApp = 0;
        notifier.InAppNotification += (_, _) => inApp++;

        Assert.True(notifier.Notify(3_000, "quill", "Grabación activa", NotificationKind.Info));

        var shown = Assert.Single(_shown);
        Assert.Equal("Grabación activa", shown.Text);
        Assert.Equal(TimeSpan.FromSeconds(3), shown.Duration);
        Assert.Equal(0, inApp);
        Assert.Empty(notifier.Recent);
    }

    [Fact]
    public void DiscreetModeKeepsNotificationsInApp()
    {
        _discreet = true;
        var notifier = Create();
        QuillNotification? raised = null;
        notifier.InAppNotification += (_, notification) => raised = notification;

        Assert.False(notifier.Notify(6_000, "quill - error", "Fallo", NotificationKind.Error));

        Assert.Empty(_shown);
        Assert.Equal(NotificationKind.Error, raised!.Kind);
        Assert.Same(raised, Assert.Single(notifier.Recent));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MeetingDetectionIsAlwaysRaisedAsAnEvent(bool discreet)
    {
        _discreet = discreet;
        var notifier = Create();
        QuillNotification? meeting = null;
        notifier.MeetingDetected += (_, notification) => meeting = notification;

        notifier.Notify(6_000, "quill", "Teams", NotificationKind.Info, NotificationTopic.MeetingDetected, "Teams");

        Assert.Equal("Teams", meeting!.Subject);
        Assert.Equal(discreet ? 0 : 1, _shown.Count);
    }

    [Fact]
    public void RecentKeepsOnlyTheNewest()
    {
        _discreet = true;
        var notifier = Create();

        for (var index = 0; index < QuillNotifier.RecentCapacity + 5; index++)
        {
            notifier.Notify(1_000, "quill", index.ToString(System.Globalization.CultureInfo.InvariantCulture), NotificationKind.Info);
        }

        Assert.Equal(QuillNotifier.RecentCapacity, notifier.Recent.Count);
        Assert.Equal("5", notifier.Recent[0].Text);
        notifier.ClearRecent();
        Assert.Empty(notifier.Recent);
    }
}

public sealed class TrayAppearanceTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void RecordingDotOnlyWithoutNeutralIcon(bool recording, bool neutral, bool expected) =>
        Assert.Equal(expected, TrayAppearance.ShowsRecordingIcon(recording, neutral));

    [Fact]
    public void TooltipIsNeutralOnlyWhileRecordingInNeutralMode()
    {
        Assert.Equal("Quill", TrayAppearance.Tooltip("quill - GRABANDO 1:00", recordingActive: true, neutral: true));
        Assert.Equal("quill - GRABANDO 1:00", TrayAppearance.Tooltip("quill - GRABANDO 1:00", recordingActive: true, neutral: false));
        Assert.Equal("quill - listo", TrayAppearance.Tooltip("quill - listo", recordingActive: false, neutral: true));
    }
}

public sealed class HotkeyInteropTests
{
    [Fact]
    public void MapsTheDefaultCompanionGesture()
    {
        var (modifiers, key) = HotkeyInterop.ToNative(HotkeyGesture.DefaultCompanion);

        Assert.Equal(HotkeyInterop.ModControl | HotkeyInterop.ModShift | HotkeyInterop.ModNoRepeat, modifiers);
        Assert.Equal((uint)'Q', key);
    }

    [Theory]
    [InlineData("Alt+Win+F1", HotkeyInterop.ModAlt | HotkeyInterop.ModWin, 0x70u)]
    [InlineData("Ctrl+Alt+F24", HotkeyInterop.ModControl | HotkeyInterop.ModAlt, 0x87u)]
    [InlineData("Ctrl+Alt+7", HotkeyInterop.ModControl | HotkeyInterop.ModAlt, 0x37u)]
    public void MapsModifiersAndKeys(string text, uint modifiers, uint key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));

        Assert.Equal((modifiers | HotkeyInterop.ModNoRepeat, key), HotkeyInterop.ToNative(gesture!));
    }
}
