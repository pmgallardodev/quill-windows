using System.Text.Json.Nodes;
using Quill.Core.Settings;
using Xunit;

namespace Quill.Core.Tests.Settings;

public sealed class PrivacySettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("{ \"privacy\": {} }")]
    [InlineData("{ \"privacy\": 3 }")]
    public void MissingPrivacyDefaultsToDiscreet(string? json)
    {
        var privacy = SettingsDocument.Parse(json).Privacy;

        Assert.True(privacy.DiscreetMode);
        Assert.True(privacy.NeutralTrayIcon);
        Assert.True(privacy.UsesNeutralTrayIcon);
        Assert.Equal("Ctrl+Shift+Q", privacy.CompanionHotkey);
        Assert.Equal(HotkeyGesture.DefaultCompanion, privacy.CompanionGesture);
    }

    [Fact]
    public void ReadsEveryPrivacyKey()
    {
        const string json = """
            { "privacy": { "discreet_mode": false, "neutral_tray_icon": false, "companion_hotkey": "alt+win+f9" } }
            """;

        var privacy = SettingsDocument.Parse(json).Privacy;

        Assert.False(privacy.DiscreetMode);
        Assert.False(privacy.NeutralTrayIcon);
        Assert.Equal("Alt+Win+F9", privacy.CompanionHotkey);
    }

    [Fact]
    public void InvalidPrivacyValuesFallBackOneByOne()
    {
        const string json = """
            { "privacy": { "discreet_mode": "no", "neutral_tray_icon": false, "companion_hotkey": "Q" } }
            """;

        var privacy = SettingsDocument.Parse(json).Privacy;

        Assert.True(privacy.DiscreetMode);
        Assert.False(privacy.NeutralTrayIcon);
        Assert.Equal("Ctrl+Shift+Q", privacy.CompanionHotkey);
    }

    [Fact]
    public void NeutralTrayIconOnlyAppliesInDiscreetMode()
    {
        Assert.False(new PrivacySettings { DiscreetMode = false, NeutralTrayIcon = true }.UsesNeutralTrayIcon);
        Assert.False(new PrivacySettings { DiscreetMode = true, NeutralTrayIcon = false }.UsesNeutralTrayIcon);
    }

    [Fact]
    public void MergeWritesThePrivacySectionAndRoundTrips()
    {
        var settings = new QuillSettings
        {
            Privacy = new PrivacySettings { DiscreetMode = false, NeutralTrayIcon = false, CompanionHotkey = "Ctrl+Alt+P" },
        };

        var merged = SettingsDocument.Merge("""{ "privacy": { "future": 1 } }""", settings);
        var privacy = JsonNode.Parse(merged)!["privacy"]!.AsObject();

        Assert.Equal(settings, SettingsDocument.Parse(merged));
        Assert.False((bool?)privacy["discreet_mode"]);
        Assert.False((bool?)privacy["neutral_tray_icon"]);
        Assert.Equal("Ctrl+Alt+P", (string?)privacy["companion_hotkey"]);
        Assert.Equal(1, (int?)privacy["future"]);
    }

    [Theory]
    [InlineData("Ctrl+Shift+Q", HotkeyModifiers.Control | HotkeyModifiers.Shift, "Q")]
    [InlineData(" control + alt + 7 ", HotkeyModifiers.Control | HotkeyModifiers.Alt, "7")]
    [InlineData("Win+Shift+f12", HotkeyModifiers.Windows | HotkeyModifiers.Shift, "F12")]
    [InlineData("Shift+F5", HotkeyModifiers.Shift, "F5")]
    public void ParsesGestures(string text, HotkeyModifiers modifiers, string key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(modifiers, gesture!.Modifiers);
        Assert.Equal(key, gesture.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Q")]
    [InlineData("Shift+Q")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Shift+QQ")]
    [InlineData("Ctrl+Ctrl+Q")]
    [InlineData("Ctrl+Shift+F25")]
    [InlineData("Hyper+Q")]
    [InlineData("Ctrl+Shift+R")]
    [InlineData("ctrl+shift+m")]
    public void RejectsInvalidOrReservedGestures(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Null(gesture);
    }

    [Fact]
    public void FormatsGesturesCanonically()
    {
        Assert.True(HotkeyGesture.TryParse("shift+win+alt+ctrl+a", out var gesture));
        Assert.Equal("Ctrl+Alt+Shift+Win+A", gesture!.ToString());
    }
}
