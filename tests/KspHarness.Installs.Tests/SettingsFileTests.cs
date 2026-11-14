namespace KspHarness.Installs.Tests;

public sealed class SettingsFileTests
{
    [Fact]
    public void Creates_a_file_KSP_will_read()
    {
        using var dir = new TempDir();
        string path = dir.Combine("settings.cfg");

        SettingsFile.Apply(path, [KeyValuePair.Create("SCREEN_RESOLUTION_WIDTH", "1280")]);

        var values = SettingsFile.ReadTopLevel(path);
        Assert.Equal("1.12.5", values["SETTINGS_FILE_VERSION"]);
        Assert.Equal("1280", values["SCREEN_RESOLUTION_WIDTH"]);
    }

    [Fact]
    public void Replaces_top_level_values_and_keeps_nodes()
    {
        using var dir = new TempDir();
        string path = dir.Write(
            "settings.cfg",
            "// KSP Game Settings\r\n\r\nSETTINGS_FILE_VERSION = 1.12.5\r\nUI_SCALE = 1\r\nAXIS_PITCH\r\n{\r\n\tUI_SCALE = 7\r\n}\r\n"
        );

        SettingsFile.Apply(path, [KeyValuePair.Create("UI_SCALE", "1.5"), KeyValuePair.Create("NEW_KEY", "True")]);

        string text = File.ReadAllText(path);
        Assert.Contains("UI_SCALE = 1.5\r\n", text);
        Assert.Contains("\tUI_SCALE = 7\r\n", text);
        Assert.True(
            text.IndexOf("NEW_KEY = True", StringComparison.Ordinal)
                < text.IndexOf("AXIS_PITCH", StringComparison.Ordinal)
        );
        Assert.Equal(2, text.Split("SETTINGS_FILE_VERSION").Length);
        Assert.Equal("1.5", SettingsFile.ReadTopLevel(path)["UI_SCALE"]);
    }

    [Fact]
    public void Removes_top_level_values_only()
    {
        using var dir = new TempDir();
        string path = dir.Write("settings.cfg", "A = 1\r\nB = 2\r\nNODE\r\n{\r\n\tA = 3\r\n}\r\n");

        SettingsFile.Remove(path, ["A"]);

        Assert.Equal("B = 2\r\nNODE\r\n{\r\n\tA = 3\r\n}\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void Adds_the_version_when_an_existing_file_lacks_it()
    {
        using var dir = new TempDir();
        string path = dir.Write("settings.cfg", "UI_SCALE = 1\n");

        SettingsFile.Apply(path, [KeyValuePair.Create("UI_SCALE", "2")]);

        var values = SettingsFile.ReadTopLevel(path);
        Assert.Equal("1.12.5", values["SETTINGS_FILE_VERSION"]);
        Assert.Equal("2", values["UI_SCALE"]);
    }

    [Theory]
    [InlineData("A B", "1")]
    [InlineData("A=", "1")]
    [InlineData("A", "x\ny")]
    [InlineData("A", "{")]
    public void Rejects_entries_that_would_break_the_file(string key, string value)
    {
        Assert.Throws<ArgumentException>(() => SettingsFile.CheckEntry(key, value));
    }
}
