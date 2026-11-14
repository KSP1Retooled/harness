namespace KspHarness.Installs.Tests;

public sealed class KnownPopupsTests
{
    private static readonly CancellationToken Cancel = TestContext.Current.CancellationToken;

    private static readonly string KspcfFile = "GameData/KSPCommunityFixes/PluginData/PNGTextureCache.cfg";

    public sealed class Plain : InstallDefinition
    {
        public override void Configure(InstallBuilder install) => install.Setting("UI_SCALE", "1.25");
    }

    public sealed class NoAnswers : InstallDefinition
    {
        public override void Configure(InstallBuilder install) => install.Include<Plain>().AnswerKnownPopups(false);
    }

    public sealed class OneAnswerBack : InstallDefinition
    {
        public override void Configure(InstallBuilder install) =>
            install.Include<NoAnswers>().AnswerPopup(KnownPopups.KspcfTextureCache, "No");
    }

    public sealed class AllBack : InstallDefinition
    {
        public override void Configure(InstallBuilder install) => install.Include<NoAnswers>().AnswerKnownPopups(true);
    }

    [Fact]
    public void Answers_follow_the_include_chain()
    {
        var plain = InstallPlan.Create(new Plain()).Popups.KnownAnswers;
        Assert.Equal("yes", plain[KnownPopups.KspcfTextureCache]);
        Assert.Equal("hide", plain[KnownPopups.KspPrivacy]);

        var none = InstallPlan.Create(new NoAnswers()).Popups.KnownAnswers;
        Assert.All(none.Values, Assert.Null);

        var one = InstallPlan.Create(new OneAnswerBack()).Popups.KnownAnswers;
        Assert.Equal("no", one[KnownPopups.KspcfTextureCache]);
        Assert.Null(one[KnownPopups.ToolbarControllerIntro]);

        var all = InstallPlan.Create(new AllBack()).Popups.KnownAnswers;
        Assert.Equal(plain, all);
    }

    [Fact]
    public void Unknown_popups_and_answers_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new InstallBuilder().AnswerPopup("Nope.Popup", "yes"));
        Assert.Throws<ArgumentException>(() => new InstallBuilder().AnswerPopup(KnownPopups.KspcfTextureCache, "maybe"));
    }

    [Fact]
    public void Answers_are_written_only_for_mods_that_are_present()
    {
        using var dir = new TempDir();
        var start = StartingInstall.Open(FakeInstall.Create(dir));
        string game = dir.Combine("game");
        Directory.CreateDirectory(game);
        dir.Write("game/GameData/KSPCommunityFixes/Plugins/KSPCommunityFixes.dll", "plugin");
        var answers = InstallPlan.Create(new Plain()).Popups.KnownAnswers;

        var applied = KnownPopups.Apply(game, answers, start);

        Assert.Contains("KSPCommunityFixes.TextureCache=yes", applied);
        Assert.DoesNotContain(applied, entry => entry.StartsWith(KnownPopups.ToolbarControllerIntro));
        Assert.Equal("userOptInChoiceDone = True\r\ntextureCacheEnabled = True\r\n",
            File.ReadAllText(Path.Combine(game, KspcfFile)));
        Assert.False(Directory.Exists(Path.Combine(game, "GameData/001_ToolbarControl")));

        var settings = SettingsFile.ReadTopLevel(Path.Combine(game, "settings.cfg"));
        Assert.Equal("False", settings["SHOW_ANALYTICS_DIALOG"]);
        Assert.Equal("False", settings["SHOW_WHATSNEW_DIALOG"]);
        Assert.Equal("1.12.5", settings["SHOW_WHATSNEW_DIALOG_VersionsShown"]);
    }

    [Fact]
    public async Task A_definition_can_take_the_answers_away()
    {
        using var dir = new TempDir();
        string startRoot = FakeInstall.Create(dir);
        File.WriteAllText(Path.Combine(startRoot, "settings.cfg"), "SETTINGS_FILE_VERSION = 1.12.5\nUI_SCALE = 1\n");
        Directory.CreateDirectory(Path.Combine(startRoot, "GameData/KSPCommunityFixes/Plugins"));
        File.WriteAllText(Path.Combine(startRoot, "GameData/KSPCommunityFixes/Plugins/KSPCommunityFixes.dll"), "plugin");
        var start = StartingInstall.Open(startRoot);
        var home = new HarnessHome(dir.Combine("home"));

        var plain = await InstanceBuilder.BuildAsync(new Plain(), start, home, null, cancel: Cancel);
        var bare = await InstanceBuilder.BuildAsync(new NoAnswers(), start, home, null, cancel: Cancel);

        Assert.True(File.Exists(plain.Path(KspcfFile)));
        Assert.False(File.Exists(bare.Path(KspcfFile)));
        var settings = SettingsFile.ReadTopLevel(bare.Path("settings.cfg"));
        Assert.False(settings.ContainsKey("SHOW_ANALYTICS_DIALOG"));
        Assert.Equal("1.25", settings["UI_SCALE"]);
        Assert.NotEqual(plain.Hash, bare.Hash);
    }
}
