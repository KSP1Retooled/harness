namespace KspHarness.Installs.Tests;

public sealed class InstanceBuilderTests
{
    public sealed class WithSettings : InstallDefinition
    {
        public override void Configure(InstallBuilder install) => install.Setting("UI_SCALE", "1.25");
    }

    public sealed class WithSave : InstallDefinition
    {
        public static string SaveDir = "";

        public override void Configure(InstallBuilder install) => install.Include<WithSettings>().Saves(SaveDir);
    }

    public sealed class WithArchive : InstallDefinition
    {
        public static string Url = "";
        public static string Sha = "";

        public override void Configure(InstallBuilder install) => install.Archive("mod", Url, Sha, "Loose");
    }

    private static readonly CancellationToken Cancel = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Builds_links_and_reuses()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "file identity check is Windows only");
        using var dir = new TempDir();
        var start = StartingInstall.Open(FakeInstall.Create(dir));
        var home = new HarnessHome(dir.Combine("home"));
        string lockPath = dir.Combine("WithSettings.lock.json");

        var instance = await InstanceBuilder.BuildAsync(new WithSettings(), start, home, lockPath, cancel: Cancel);

        Assert.StartsWith("WithSettings-", instance.Name);
        Assert.True(Instance.IsReady(instance.Root));
        Assert.Equal("1.25", SettingsFile.ReadTopLevel(instance.Path("settings.cfg"))["UI_SCALE"]);

        // Large read-only files are links; files the game writes are copies.
        Assert.True(FileIdentity.SameFile(Path.Combine(start.Root, "GameData/Squad/Parts/texture.dds"),
            instance.Path("GameData/Squad/Parts/texture.dds")));
        Assert.True(FileIdentity.SameFile(Path.Combine(start.Root, "KSP_x64_Data/Managed/Assembly-CSharp.dll"),
            instance.Path("KSP_x64_Data/Managed/Assembly-CSharp.dll")));
        Assert.False(FileIdentity.SameFile(Path.Combine(start.Root, "Ships/@thumbs/VAB/craft.png"),
            instance.Path("Ships/@thumbs/VAB/craft.png")));
        Assert.False(FileIdentity.SameFile(Path.Combine(start.Root, "GameData/Squad/Plugins/Squad.dll"),
            instance.Path("GameData/Squad/Plugins/Squad.dll")));
        Assert.False(File.Exists(Path.Combine(start.Root, "settings.cfg")));

        var lockFile = LockFile.Load(lockPath)!;
        Assert.Equal("stock", lockFile.Start);
        Assert.Single(lockFile.Layers);

        var again = await InstanceBuilder.BuildAsync(new WithSettings(), start, home, lockPath, cancel: Cancel);
        Assert.Equal(instance.Root, again.Root);
        Assert.Single(Directory.GetDirectories(home.InstancesDir), d => !d.EndsWith(".building"));
    }

    [Fact]
    public async Task Derived_instance_starts_from_the_included_one()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "file identity check is Windows only");
        using var dir = new TempDir();
        var start = StartingInstall.Open(FakeInstall.Create(dir));
        var home = new HarnessHome(dir.Combine("home"));
        dir.Write("MySave/persistent.sfs", "GAME {}");
        WithSave.SaveDir = dir.Combine("MySave");

        var derived = await InstanceBuilder.BuildAsync(new WithSave(), start, home, null, cancel: Cancel);
        var baseInstance = Instance.Open(Directory.GetDirectories(home.InstancesDir, "WithSettings-*").Single());

        Assert.Equal(baseInstance.Hash, derived.Record.Parent);
        Assert.Equal("GAME {}", File.ReadAllText(derived.Path("saves/MySave/persistent.sfs")));
        Assert.Equal("1.25", SettingsFile.ReadTopLevel(derived.Path("settings.cfg"))["UI_SCALE"]);
        Assert.True(FileIdentity.SameFile(baseInstance.Path("GameData/Squad/Parts/texture.dds"),
            derived.Path("GameData/Squad/Parts/texture.dds")));
        Assert.False(File.Exists(derived.Path(".harness/ckan.log")));

        dir.Write("MySave/persistent.sfs", "GAME { changed }");
        var rebuilt = await InstanceBuilder.BuildAsync(new WithSave(), start, home, null, cancel: Cancel);
        Assert.NotEqual(derived.Root, rebuilt.Root);
        Assert.Equal(derived.Record.Parent, rebuilt.Record.Parent);
    }

    [Fact]
    public async Task Archive_is_unpacked_into_its_subfolder()
    {
        using var dir = new TempDir();
        var start = StartingInstall.Open(FakeInstall.Create(dir));
        var home = new HarnessHome(dir.Combine("home"));
        string zip = Zips.Create(dir.Combine("loose.zip"), ("Plugin.dll", "code"));
        WithArchive.Url = new Uri(zip).AbsoluteUri;
        WithArchive.Sha = await ArchiveCache.HashFileAsync(zip, Cancel);
        string lockPath = dir.Combine("lock.json");

        var instance = await InstanceBuilder.BuildAsync(new WithArchive(), start, home, lockPath, cancel: Cancel);

        Assert.Equal("code", File.ReadAllText(instance.Path("GameData/Loose/Plugin.dll")));
        var archive = Assert.Single(LockFile.Load(lockPath)!.Layers[0].Archives);
        Assert.Equal(WithArchive.Sha, archive.Sha256);
    }

    [Fact]
    public async Task Copies_everything_when_links_are_off()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "file identity check is Windows only");
        using var dir = new TempDir();
        var start = StartingInstall.Open(FakeInstall.Create(dir));
        var home = new HarnessHome(dir.Combine("home"));

        var instance = await InstanceBuilder.BuildAsync(new WithSettings(), start, home, null,
            new InstanceBuildOptions { UseHardLinks = false }, Cancel);

        Assert.False(FileIdentity.SameFile(Path.Combine(start.Root, "GameData/Squad/Parts/texture.dds"),
            instance.Path("GameData/Squad/Parts/texture.dds")));
    }

    [Fact]
    public async Task Frozen_lock_must_exist_and_match()
    {
        using var dir = new TempDir();
        var start = StartingInstall.Open(FakeInstall.Create(dir));
        var home = new HarnessHome(dir.Combine("home"));
        string lockPath = dir.Combine("lock.json");
        var frozen = new InstanceBuildOptions { LockMode = LockMode.Frozen };

        await Assert.ThrowsAsync<InstallException>(() =>
            InstanceBuilder.BuildAsync(new WithSettings(), start, home, lockPath, frozen, Cancel));

        await InstanceBuilder.BuildAsync(new WithSettings(), start, home, lockPath, cancel: Cancel);
        await InstanceBuilder.BuildAsync(new WithSettings(), start, home, lockPath, frozen, Cancel);

        var retooled = StartingInstall.Open(FakeInstall.Create(dir, "retooled", retooled: true));
        var error = await Assert.ThrowsAsync<InstallException>(() =>
            InstanceBuilder.BuildAsync(new WithSettings(), retooled, home, lockPath, frozen, Cancel));
        Assert.Contains("stock", error.Message);
    }

    [Fact]
    public async Task Deploy_mod_replaces_only_when_bytes_differ()
    {
        using var dir = new TempDir();
        var start = StartingInstall.Open(FakeInstall.Create(dir));
        var home = new HarnessHome(dir.Combine("home"));
        var instance = await InstanceBuilder.BuildAsync(new WithSettings(), start, home, null, cancel: Cancel);
        dir.Write("build/GameData/KspHarness/Plugins/KspHarness.Mod.dll", "v1");
        string mod = dir.Combine("build", "GameData", "KspHarness");

        Assert.True(instance.DeployMod(mod));
        Assert.False(instance.DeployMod(mod));

        dir.Write("build/GameData/KspHarness/Plugins/KspHarness.Mod.dll", "v2");
        Assert.True(instance.DeployMod(mod));
        Assert.Equal("v2", File.ReadAllText(instance.Path("GameData/KspHarness/Plugins/KspHarness.Mod.dll")));

        File.WriteAllText(instance.Path("GameData/KspHarness/stale.txt"), "x");
        Assert.True(instance.DeployMod(mod));
        Assert.False(File.Exists(instance.Path("GameData/KspHarness/stale.txt")));
    }

    [Fact]
    public async Task Deploy_mod_refuses_a_folder_without_the_mod_and_keeps_the_deployed_copy()
    {
        using var dir = new TempDir();
        var start = StartingInstall.Open(FakeInstall.Create(dir));
        var home = new HarnessHome(dir.Combine("home"));
        var instance = await InstanceBuilder.BuildAsync(new WithSettings(), start, home, null, cancel: Cancel);
        string dll = Path.Combine("Plugins", "KspHarness.Mod.dll");
        dir.Write("build/GameData/KspHarness/Plugins/KspHarness.Mod.dll", "v1");
        string mod = dir.Combine("build", "GameData", "KspHarness");
        Assert.True(instance.DeployMod(mod, dll));

        // An empty build output, then one that lost its DLL.
        File.Delete(Path.Combine(mod, dll));
        Assert.Contains("no files", Assert.Throws<InstallException>(() => instance.DeployMod(mod, dll)).Message);
        dir.Write("build/GameData/KspHarness/Plugins/KspHarness.Mod.pdb", "symbols");
        Assert.Contains("lacks", Assert.Throws<InstallException>(() => instance.DeployMod(mod, dll)).Message);

        Assert.Equal("v1", File.ReadAllText(instance.Path("GameData/KspHarness/Plugins/KspHarness.Mod.dll")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(instance.Path(".harness/deploying")));
    }

    [Fact]
    public async Task Deploy_mod_and_launch_refuse_while_a_game_runs_in_the_instance()
    {
        using var dir = new TempDir();
        var start = StartingInstall.Open(FakeInstall.Create(dir, platform: FakeInstall.HostPlatform));
        var home = new HarnessHome(dir.Combine("home"));
        var instance = await InstanceBuilder.BuildAsync(new WithSettings(), start, home, null, cancel: Cancel);
        dir.Write("build/GameData/KspHarness/Plugins/KspHarness.Mod.dll", "v1");
        string mod = dir.Combine("build", "GameData", "KspHarness");
        Assert.True(instance.DeployMod(mod));

        // Deleting the executable removes only the instance's link to it; a program that waits takes its place.
        string game = instance.Layout.LaunchPath;
        File.Delete(game);
        var waiting = new System.Diagnostics.ProcessStartInfo(game)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };

        if (OperatingSystem.IsWindows())
        {
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), game);
            waiting.ArgumentList.Add("-n");
            waiting.ArgumentList.Add("60");
            waiting.ArgumentList.Add("127.0.0.1");
        }
        else
        {
            File.Copy("/bin/sleep", game);
            waiting.ArgumentList.Add("60");
        }

        using var process = System.Diagnostics.Process.Start(waiting)!;

        try
        {
            Assert.Equal([process.Id], instance.RunningGameProcessIds());
            Assert.False(instance.DeployMod(mod));

            dir.Write("build/GameData/KspHarness/Plugins/KspHarness.Mod.dll", "v2");
            var error = Assert.Throws<InstallException>(() => instance.DeployMod(mod));
            Assert.Contains($"pid {process.Id}", error.Message);
            Assert.Equal("v1", File.ReadAllText(instance.Path("GameData/KspHarness/Plugins/KspHarness.Mod.dll")));
        }
        finally
        {
            process.Kill();
            process.WaitForExit();
        }

        Assert.Empty(instance.RunningGameProcessIds());
        Assert.True(instance.DeployMod(mod));
    }

    [Fact]
    public async Task Deploy_mod_keeps_the_old_copy_when_a_file_in_it_is_open()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "an open file blocks the rename only on Windows");
        using var dir = new TempDir();
        var start = StartingInstall.Open(FakeInstall.Create(dir));
        var home = new HarnessHome(dir.Combine("home"));
        var instance = await InstanceBuilder.BuildAsync(new WithSettings(), start, home, null, cancel: Cancel);
        dir.Write("build/GameData/KspHarness/Plugins/KspHarness.Mod.dll", "v1");
        dir.Write("build/GameData/KspHarness/Plugins/KspHarness.Mod.pdb", "symbols");
        string mod = dir.Combine("build", "GameData", "KspHarness");
        Assert.True(instance.DeployMod(mod));

        dir.Write("build/GameData/KspHarness/Plugins/KspHarness.Mod.dll", "v2");
        using (File.Open(instance.Path("GameData/KspHarness/Plugins/KspHarness.Mod.dll"), FileMode.Open,
            FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<InstallException>(() => instance.DeployMod(mod));
        }

        Assert.Equal("v1", File.ReadAllText(instance.Path("GameData/KspHarness/Plugins/KspHarness.Mod.dll")));
        Assert.True(File.Exists(instance.Path("GameData/KspHarness/Plugins/KspHarness.Mod.pdb")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(instance.Path(".harness/deploying")));
    }

    [Theory]
    [InlineData("GameData/Squad/Parts/texture.dds", true)]
    [InlineData("KSP_x64_Data/Managed/Assembly-CSharp.dll", true)]
    [InlineData("KSP_x64_Data/sharedassets0.assets", true)]
    [InlineData("GameData/Squad/Plugins/Squad.dll", false)]
    [InlineData("GameData/Mod/PluginData/cache.bin", false)]
    [InlineData("Ships/@thumbs/VAB/craft.png", false)]
    [InlineData("saves/scenarios/banners/a.png", false)]
    [InlineData("GameData/Squad/Parts/part.cfg", false)]
    [InlineData("KSP_x64_Data/level0", true)]
    [InlineData("UnityPlayer.dll", true)]
    [InlineData("GameData/SquadExpansion/Serenity/AssetBundles/serenity_assets", true)]
    [InlineData("settings.cfg", false)]
    [InlineData("GameData/ModuleManager.4.2.3.dll", false)]
    [InlineData("GameData/Squad/Parts/@thumbs/mk1pod_icon.png", false)]
    [InlineData("GameData/Squad/Parts/CompoundParts/@thumbs/strut_icon.png", false)]
    [InlineData("GameData/SquadExpansion/Serenity/Parts/@thumbs/hinge_icon0.png", false)]
    [InlineData("GameData/SquadExpansion/MakingHistory/Ships/@thumbs/VAB/craft.png", false)]
    [InlineData("@thumbs/Parts/part_icon.png", false)]
    [InlineData("thumbs/default_VAB_craft.png", false)]
    [InlineData("GameData/Squad/Parts/Command/mk1pod/model.png", true)]
    [InlineData("GameData/Squad/squadcore.ksp", true)]
    [InlineData("GameData/Kopernicus/Cache/Kerbin.bin", false)]
    public void Link_rules(string relative, bool linkable)
    {
        Assert.Equal(linkable, new TreeCopier("KSP_x64_Data", allowLinks: true).IsLinkable(relative));
    }

    [Theory]
    [InlineData("KSP_Data", "KSP.x86_64", true)]
    [InlineData("KSP_Data", "UnityPlayer.so", true)]
    [InlineData("KSP_Data", "KSP_Data/Managed/Assembly-CSharp.dll", true)]
    [InlineData("KSP_Data", "KSP_Data/level0", true)]
    [InlineData("KSP.app/Contents/Resources/Data", "KSP.app/Contents/Resources/Data/level0", true)]
    [InlineData("KSP.app/Contents/Resources/Data", "KSP.app/Contents/Resources/Data/Managed/UnityEngine.dll", true)]
    [InlineData("KSP.app/Contents/Resources/Data", "KSP.app/Contents/MacOS/KSP", false)]
    [InlineData("KSP.app/Contents/Resources/Data", "KSP.app/Contents/Info.plist", false)]
    public void Link_rules_of_other_layouts(string dataDir, string relative, bool linkable)
    {
        Assert.Equal(linkable, new TreeCopier(dataDir, allowLinks: true).IsLinkable(relative));
    }

    [Theory]
    [InlineData("/opt/ckan/ckan.exe", false, "mono", "/opt/ckan/ckan.exe")]
    [InlineData("/opt/ckan/CKAN.EXE", false, "mono", "/opt/ckan/CKAN.EXE")]
    [InlineData("/usr/bin/ckan", false, "/usr/bin/ckan", null)]
    [InlineData(@"C:\CKAN\ckan.exe", true, @"C:\CKAN\ckan.exe", null)]
    public void Ckan_exe_runs_under_mono_outside_windows(string executable, bool windows, string fileName, string? prefix)
    {
        var command = CkanClient.CommandFor(executable, windows);

        Assert.Equal(fileName, command.FileName);
        Assert.Equal(prefix is null ? [] : [prefix], command.Prefix);
    }

    [Fact]
    public async Task Derived_instance_leaves_out_what_the_game_wrote_into_the_included_one()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "file identity check is Windows only");
        using var dir = new TempDir();
        string startRoot = FakeInstall.Create(dir);
        Directory.CreateDirectory(Path.Combine(startRoot, "GameData/Squad/Parts/@thumbs"));
        File.WriteAllText(Path.Combine(startRoot, "GameData/Squad/Parts/@thumbs/pod_icon.png"), "icon");
        var start = StartingInstall.Open(startRoot);
        var home = new HarnessHome(dir.Combine("home"));
        dir.Write("MySave/persistent.sfs", "GAME {}");
        WithSave.SaveDir = dir.Combine("MySave");

        var baseInstance = await InstanceBuilder.BuildAsync(new WithSettings(), start, home, null, cancel: Cancel);

        // A game played in the included instance.
        File.WriteAllText(baseInstance.Path("settings.cfg"), "SETTINGS_FILE_VERSION = 1.12.5\nUI_SCALE = 2\n");
        Directory.CreateDirectory(baseInstance.Path("saves/played"));
        File.WriteAllText(baseInstance.Path("saves/played/persistent.sfs"), "GAME { played }");
        File.WriteAllText(baseInstance.Path("KSP.log"), "log");

        var derived = await InstanceBuilder.BuildAsync(new WithSave(), start, home, null, cancel: Cancel);

        Assert.Equal(baseInstance.Hash, derived.Record.Parent);
        Assert.Equal("1.25", SettingsFile.ReadTopLevel(derived.Path("settings.cfg"))["UI_SCALE"]);
        Assert.False(Directory.Exists(derived.Path("saves/played")));
        Assert.False(File.Exists(derived.Path("KSP.log")));
        Assert.True(FileIdentity.SameFile(Path.Combine(start.Root, "GameData/Squad/Parts/texture.dds"),
            derived.Path("GameData/Squad/Parts/texture.dds")));
        Assert.False(FileIdentity.SameFile(Path.Combine(start.Root, "GameData/Squad/Parts/@thumbs/pod_icon.png"),
            derived.Path("GameData/Squad/Parts/@thumbs/pod_icon.png")));
    }

    [Fact]
    public void A_build_that_lost_the_race_reuses_the_finished_instance()
    {
        using var dir = new TempDir();
        string final = dir.Combine("instances", "Plain-0123456789ab");
        dir.Write("instances/Plain-0123456789ab/.harness/instance.json", "{}");
        dir.Write("building/a/file.txt", "x");

        Assert.False(InstanceBuilder.MoveIntoPlace(dir.Combine("building", "a"), final));
        Assert.True(Directory.Exists(dir.Combine("building", "a")));

        dir.Write("broken/file.txt", "x");
        dir.Write("building/b/file.txt", "x");
        Assert.Throws<InstallException>(() => InstanceBuilder.MoveIntoPlace(dir.Combine("building", "b"),
            dir.Combine("broken")));

        Assert.True(InstanceBuilder.MoveIntoPlace(dir.Combine("building", "b"), dir.Combine("instances", "Plain-x")));
    }

    [Fact]
    public void Abandoned_builds_are_removed_and_running_ones_kept()
    {
        using var dir = new TempDir();
        var home = new HarnessHome(dir.Combine("home"));
        dir.Write("home/instances/.building/Dead-1/file.txt", "x");
        dir.Write("home/instances/.building/Dead-1.owner", BuildOwner.Describe(Environment.ProcessId, DateTime.UnixEpoch));
        dir.Write("home/instances/.building/Live-2/file.txt", "x");
        dir.Write("home/instances/.building/Live-2.owner", BuildOwner.Current());
        dir.Write("home/instances/.building/Unowned-3/file.txt", "x");

        InstanceBuilder.RemoveAbandonedBuilds(home, new InstanceBuildOptions());

        Assert.False(Directory.Exists(dir.Combine("home/instances/.building/Dead-1")));
        Assert.False(File.Exists(dir.Combine("home/instances/.building/Dead-1.owner")));
        Assert.True(Directory.Exists(dir.Combine("home/instances/.building/Live-2")));
        Assert.True(Directory.Exists(dir.Combine("home/instances/.building/Unowned-3")));
    }

    [Fact]
    public void A_locked_layer_must_add_exactly_its_locked_modules()
    {
        var locked = new LockLayer { Definition = "D", Ckan = [new("ModuleManager", "4.2.3"), new("Foo", "1.0")] };

        InstanceBuilder.RequireLockedModules("D", locked, [new("Foo", "1.0"), new("ModuleManager", "4.2.3")], "log");

        var extra = Assert.Throws<InstallException>(() => InstanceBuilder.RequireLockedModules("D", locked,
            [new("ModuleManager", "4.2.3"), new("Foo", "1.0"), new("Bar", "2.0")], "log"));
        Assert.Contains("Bar=2.0", extra.Message);

        var absent = Assert.Throws<InstallException>(() => InstanceBuilder.RequireLockedModules("D", locked,
            [new("Foo", "1.0")], "log"));
        Assert.Contains("ModuleManager=4.2.3", absent.Message);
    }

    [Fact]
    public void Junctions_in_the_start_are_refused()
    {
        using var dir = new TempDir();
        string startRoot = FakeInstall.Create(dir);
        dir.Write("checkout/Plugins/Mod.dll", "code");
        string junction = Path.Combine(startRoot, "GameData", "Mod");

        // A junction needs no privilege on Windows, where a symbolic link does; elsewhere a symbolic link.
        if (OperatingSystem.IsWindows())
        {
            var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", junction, dir.Combine("checkout") },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            })!;
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(junction, dir.Combine("checkout"));
        }

        try
        {
            var error = Assert.Throws<InstallException>(() => new TreeCopier("KSP_x64_Data", allowLinks: true)
                .Copy(startRoot, dir.Combine("copy"), new HashSet<string>(), Cancel));
            Assert.Contains("junction", error.Message);
            Assert.True(File.Exists(dir.Combine("checkout/Plugins/Mod.dll")));
        }
        finally
        {
            // Removes the link alone, before the folder's recursive delete meets it.
            if (OperatingSystem.IsWindows())
            {
                Directory.Delete(junction);
            }
            else
            {
                File.Delete(junction);
            }
        }
    }
}
