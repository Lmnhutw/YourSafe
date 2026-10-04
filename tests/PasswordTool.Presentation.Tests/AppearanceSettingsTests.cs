using PasswordTool_WinUI;

namespace PasswordTool.Presentation.Tests;

public sealed class AppearanceSettingsTests
{
    [Fact]
    public void Rgb_input_is_normalized_and_rejects_short_alpha_or_whitespace_colors()
    {
        Assert.Equal("#12ABEF", AppearanceSettings.NormalizeRgb("  #12abef  "));
        foreach (var invalid in new[] { null, "", "red", "#123", "#FF123456", "# FFFFF", "#12 456", "#12GG56" })
            Assert.Throws<ArgumentException>(() => AppearanceSettings.NormalizeRgb(invalid));
    }

    [Fact]
    public void Preferences_round_trip_separately_and_invalid_save_preserves_previous_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PasswordTool.AppearanceTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "appearance.json");
        try
        {
            var expected = new AppearanceSettings { Theme = AppearanceTheme.Dark, AllTabName = " All accounts ", UngroupedTabName = " Inbox ", AllTabColor = "#12abef", UngroupedTabColor = "#eb42d0", IsVerticalTabs = true };
            expected.Save(path);
            var normalized = expected with { AllTabName = "All accounts", UngroupedTabName = "Inbox", AllTabColor = "#12ABEF", UngroupedTabColor = "#EB42D0" };
            Assert.Equal(normalized, AppearanceSettings.Load(path));
            Assert.Contains("\"Dark\"", File.ReadAllText(path));
            Assert.DoesNotContain("AccentMode", File.ReadAllText(path));
            Assert.DoesNotContain("CustomAccent", File.ReadAllText(path));
            Assert.Throws<ArgumentException>(() => (expected with { AllTabColor = "# FFFFF" }).Save(path));
            Assert.Throws<ArgumentException>(() => (expected with { UngroupedTabColor = "# FFFFF" }).Save(path));
            Assert.Throws<ArgumentException>(() => (expected with { AllTabName = new string('a', 101) }).Save(path));
            Assert.Throws<ArgumentException>(() => (expected with { UngroupedTabName = new string('a', 101) }).Save(path));
            Assert.Equal(normalized, AppearanceSettings.Load(path));
            Assert.Single(Directory.GetFiles(directory));
            (expected with { AllTabName = string.Empty, UngroupedTabName = " ", AllTabColor = string.Empty, UngroupedTabColor = " " }).Save(path);
            Assert.Null(AppearanceSettings.Load(path).AllTabName);
            Assert.Null(AppearanceSettings.Load(path).UngroupedTabName);
            Assert.Null(AppearanceSettings.Load(path).AllTabColor);
            Assert.Null(AppearanceSettings.Load(path).UngroupedTabColor);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Missing_corrupt_or_unsupported_preferences_fall_back_to_system_theme_and_blue()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PasswordTool.AppearanceTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "appearance.json");
        try
        {
            Assert.Equal(new AppearanceSettings(), AppearanceSettings.Load(path));
            Directory.CreateDirectory(directory);
            foreach (var invalid in new[] { "{", "null", "{\"Theme\":\"Neon\"}", "{\"Theme\":99}", "{\"AllTabColor\":\"# FFFFF\"}", "{\"UngroupedTabColor\":\"# FFFFF\"}", "{\"AllTabName\":\"" + new string('a', 101) + "\"}" })
            {
                File.WriteAllText(path, invalid);
                Assert.Equal(new AppearanceSettings(), AppearanceSettings.Load(path));
            }
            File.WriteAllText(path, "{\"Theme\":\"Dark\",\"AccentMode\":\"Custom\",\"CustomAccent\":\"#FF0000\"}");
            Assert.Equal(new AppearanceSettings { Theme = AppearanceTheme.Dark }, AppearanceSettings.Load(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Foregrounds_and_accent_surface_colors_keep_contrast_for_bright_dark_and_midrange_colors()
    {
        Assert.Equal("#204BDB", AppearanceSettings.DefaultAccent);
        Assert.Equal("#FFFFFF", AppearanceSettings.TextColorFor(AppearanceSettings.DefaultAccent));
        Assert.Equal("#000000", AppearanceSettings.TextColorFor("#EB42D0"));
        foreach (var color in new[] { "#000000", "#FFFFFF", "#204BDB", "#EB42D0", "#FF0000", "#00FF00", "#00AA00", "#777777" })
        {
            Assert.True(AppearanceSettings.ContrastRatio(color, AppearanceSettings.TextColorFor(color)) >= 4.5);
            foreach (var dark in new[] { false, true })
            {
                var foreground = AppearanceSettings.ColorForSurface(color, dark);
                Assert.True(AppearanceSettings.ContrastRatio(foreground, dark ? "#2D2D2D" : "#EEF2F6") >= 4.5);
                var fill = AppearanceSettings.ColorForSurface(color, dark, 3);
                var text = AppearanceSettings.TextColorFor(fill);
                foreach (var amount in new[] { 0d, 0.08, 0.16 })
                    Assert.True(AppearanceSettings.ContrastRatio(text, AppearanceSettings.Blend(fill, text == "#FFFFFF" ? "#000000" : "#FFFFFF", amount)) >= 4.5);
            }
        }
    }
}
