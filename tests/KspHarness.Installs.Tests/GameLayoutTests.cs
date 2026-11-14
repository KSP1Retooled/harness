namespace KspHarness.Installs.Tests;

public sealed class GameLayoutTests
{
    private static readonly CancellationToken Cancel = TestContext.Current.CancellationToken;

    public static TheoryData<GamePlatform> Platforms => [GamePlatform.Windows, GamePlatform.Linux, GamePlatform.MacOS];

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Detects_each_layout(GamePlatform platform)
    {
        using var dir = new TempDir();
        string root = FakeInstall.Create(dir, platform: platform);
        var (executable, playerLibrary, data) = FakeInstall.Player(platform);

        var layout = GameLayout.Require(root);

        Assert.Equal(platform, layout.Platform);
        Assert.Equal(data, layout.DataDirRelative);
        Assert.Equal(Native(root, data + "/Managed"), layout.ManagedDir);
        Assert.Equal(Native(root, "GameData"), layout.GameDataDir);
        Assert.Equal(Native(root, executable), layout.LaunchPath);
        Assert.Equal(Native(root, playerLibrary), layout.PlayerLibrary);
        Assert.True(File.Exists(layout.LaunchPath));
    }

    [Theory]
    [InlineData(GamePlatform.Windows, "KSP_x64.exe", "KSP_x64")]
    [InlineData(GamePlatform.Linux, "KSP.x86_64", "KSP.x86_64")]
    [InlineData(GamePlatform.MacOS, "KSP.app", "KSP")]
    public void Names_the_executable_and_process(GamePlatform platform, string executable, string process)
    {
        using var dir = new TempDir();
        string root = FakeInstall.Create(dir, platform: platform);

        var layout = GameLayout.Require(root);

        Assert.Equal(Native(root, executable), layout.Executable);
        Assert.Equal(process, layout.ProcessName);
    }

    [Fact]
    public void Mac_binary_comes_from_the_bundle_info()
    {
        using var dir = new TempDir();
        string root = FakeInstall.Create(dir, platform: GamePlatform.MacOS);
        File.Delete(Path.Combine(root, "KSP.app/Contents/MacOS/KSP"));
        dir.Write("start/KSP.app/Contents/MacOS/Kerbal Space Program", "exe");
        dir.Write("start/KSP.app/Contents/Info.plist", """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0">
            <dict>
                <key>CFBundleExecutable</key>
                <string>Kerbal Space Program</string>
            </dict>
            </plist>
            """);

        var layout = GameLayout.Require(root);

        Assert.Equal(Native(root, "KSP.app/Contents/MacOS/Kerbal Space Program"), layout.LaunchPath);
        Assert.Equal("Kerbal Space Program", layout.ProcessName);
    }

    [Fact]
    public void Mac_binary_defaults_to_KSP()
    {
        using var dir = new TempDir();
        string root = FakeInstall.Create(dir, platform: GamePlatform.MacOS);
        File.Delete(Path.Combine(root, "KSP.app/Contents/MacOS/KSP"));

        var layout = GameLayout.Require(root);

        Assert.Equal(Native(root, "KSP.app/Contents/MacOS/KSP"), layout.LaunchPath);
        Assert.Null(GameLayout.FindByLaunchPath(root));
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Finds_a_managed_folder_alone(GamePlatform platform)
    {
        using var dir = new TempDir();
        var (_, _, data) = FakeInstall.Player(platform);
        Directory.CreateDirectory(dir.Combine(data, "Managed"));

        var layout = GameLayout.FindByManagedDir(dir.Root);

        Assert.NotNull(layout);
        Assert.Equal(platform, layout.Platform);
        Assert.Equal(Native(dir.Root, data + "/Managed"), layout.ManagedDir);
        Assert.Null(GameLayout.Detect(dir.Root, out _));
    }

    [Fact]
    public void Finds_nothing_in_a_folder_without_a_game()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("GameData"));

        Assert.Null(GameLayout.FindByManagedDir(dir.Root));
        Assert.Null(GameLayout.FindByLaunchPath(dir.Root));
        Assert.Null(GameLayout.Detect(dir.Root, out string? problem));
        Assert.Contains("KSP_x64.exe, KSP.x86_64 or KSP.app", problem);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Finds_the_executable_alone(GamePlatform platform)
    {
        using var dir = new TempDir();
        var (executable, _, _) = FakeInstall.Player(platform);
        dir.Write(executable, "exe");

        var layout = GameLayout.FindByLaunchPath(dir.Root);

        Assert.NotNull(layout);
        Assert.Equal(platform, layout.Platform);
        Assert.Equal(Native(dir.Root, executable), layout.LaunchPath);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Missing_game_code_names_the_data_folder(GamePlatform platform)
    {
        using var dir = new TempDir();
        string root = FakeInstall.Create(dir, platform: platform);
        var (_, _, data) = FakeInstall.Player(platform);
        File.Delete(Path.Combine(root, data, "Managed", "Assembly-CSharp.dll"));

        Assert.Null(GameLayout.Detect(root, out string? problem));
        Assert.Contains(data + "/Managed/Assembly-CSharp.dll", problem);
    }

    [Theory]
    [InlineData(GamePlatform.Windows)]
    [InlineData(GamePlatform.Linux)]
    [InlineData(GamePlatform.MacOS)]
    public void Only_the_matching_operating_system_runs_the_player(GamePlatform platform)
    {
        using var dir = new TempDir();
        var layout = GameLayout.Require(FakeInstall.Create(dir, platform: platform));

        Assert.Equal(platform == FakeInstall.HostPlatform, layout.RunsOnThisMachine);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Identity_covers_the_player_files(GamePlatform platform)
    {
        using var dir = new TempDir();
        string root = FakeInstall.Create(dir, platform: platform);
        var (executable, playerLibrary, _) = FakeInstall.Player(platform);
        string first = StartingInstall.Open(root).IdentityHash;

        File.AppendAllText(Path.Combine(root, playerLibrary), "patched");
        string patchedLibrary = StartingInstall.Open(root).IdentityHash;
        Assert.NotEqual(first, patchedLibrary);

        File.AppendAllText(Path.Combine(root, executable), "patched");
        Assert.NotEqual(patchedLibrary, StartingInstall.Open(root).IdentityHash);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public async Task Builds_an_instance_of_each_layout(GamePlatform platform)
    {
        using var dir = new TempDir();
        string root = FakeInstall.Create(dir, platform: platform);
        var (executable, _, data) = FakeInstall.Player(platform);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(root, executable), (UnixFileMode)Convert.ToInt32("755", 8));
        }

        var start = StartingInstall.Open(root);
        var home = new HarnessHome(dir.Combine("home"));

        var instance = await InstanceBuilder.BuildAsync(
            new InstanceBuilderTests.WithSettings(), start, home, null, cancel: Cancel);

        Assert.Equal(platform, instance.Layout.Platform);
        Assert.True(File.Exists(instance.Layout.LaunchPath));
        Assert.True(File.Exists(instance.Path(data + "/Managed/Assembly-CSharp.dll")));
        Assert.Equal("1.25", SettingsFile.ReadTopLevel(instance.Path("settings.cfg"))["UI_SCALE"]);
        Assert.Empty(instance.RunningGameProcessIds());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(File.GetUnixFileMode(Path.Combine(root, executable)),
                File.GetUnixFileMode(instance.Layout.LaunchPath));
        }
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void Copies_keep_the_executable_mode(GamePlatform platform)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows files have no execute bit");
        using var dir = new TempDir();
        string root = FakeInstall.Create(dir, platform: platform);
        var (executable, _, _) = FakeInstall.Player(platform);
        var mode = (UnixFileMode)Convert.ToInt32("750", 8);
        File.SetUnixFileMode(Path.Combine(root, executable), mode);

        new TreeCopier(GameLayout.Require(root).DataDirRelative, allowLinks: false)
            .Copy(root, dir.Combine("copy"), new HashSet<string>(), Cancel);

        Assert.Equal(mode, File.GetUnixFileMode(dir.Combine("copy", executable)));
    }

    private static string Native(string root, string relative)
    {
        return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }
}
