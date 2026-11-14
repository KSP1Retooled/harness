using System.Text.Json;

namespace KspHarness.Installs.Tests;

/// <summary>
/// Builds instances from real starting installs with the real CKAN. Skipped unless <c>KSPHARNESS_HOME</c>,
/// <c>CKAN_EXE</c> and <c>KSPHARNESS_TEST_STOCK</c> / <c>KSPHARNESS_TEST_RETOOLED</c> name them. Run with
/// <c>--filter-trait Category=Ckan</c>; each test takes up to a few minutes on a cold cache.
/// </summary>
[Trait("Category", "Ckan")]
public sealed class RealInstallTests
{
    public sealed class CommunityFixes : InstallDefinition
    {
        public override void Configure(InstallBuilder install) => install.Ckan("KSPCommunityFixes");
    }

    public sealed class CommunityFixesWindowed : InstallDefinition
    {
        public override void Configure(InstallBuilder install) =>
            install.Include<CommunityFixes>().Setting("FULLSCREEN", "False").Setting("SCREEN_RESOLUTION_WIDTH", "1280");
    }

    private static readonly CancellationToken Cancel = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stock_instance_with_ckan_mods_is_locked_reused_and_shared()
    {
        var (start, home, lockDir) = Setup("KSPHARNESS_TEST_STOCK");
        string lockPath = Path.Combine(lockDir, "CommunityFixes.stock.lock.json");
        File.Delete(lockPath);
        var options = Options();

        var instance = await InstanceBuilder.BuildAsync(new CommunityFixes(), start, home, lockPath, options, Cancel);

        var lockFile = LockFile.Load(lockPath)!;
        Assert.Equal("stock", lockFile.Start);
        var modules = lockFile.Layers.Single().Ckan.Select(module => module.Identifier).ToList();
        Assert.Contains("KSPCommunityFixes", modules);
        Assert.Contains("ModuleManager", modules);
        Assert.Contains("Harmony2", modules);
        Assert.True(File.Exists(instance.Path(".harness/ckan.log")));
        Assert.True(Directory.Exists(instance.Path("GameData/KSPCommunityFixes")));
        DateTime lockWritten = File.GetLastWriteTimeUtc(lockPath);

        Assert.DoesNotContain("MakingHistory-DLC", modules);

        var again = await InstanceBuilder.BuildAsync(new CommunityFixes(), start, home, lockPath, options, Cancel);
        Assert.Equal(instance.Root, again.Root);
        Assert.Equal(lockWritten, File.GetLastWriteTimeUtc(lockPath));

        // From the lock alone: CKAN installs the pinned versions and the instance comes back under the same hash.
        Directory.Delete(instance.Root, recursive: true);
        var frozen = new InstanceBuildOptions { LockMode = LockMode.Frozen, Log = options.Log };
        var replayed = await InstanceBuilder.BuildAsync(new CommunityFixes(), start, home, lockPath, frozen, Cancel);
        Assert.Equal(instance.Root, replayed.Root);
        Assert.Contains(
            "install --no-recommends Harmony2=2.2.1.0",
            File.ReadAllText(replayed.Path(".harness/ckan.log")).Replace("\"", "")
        );

        string derivedLock = Path.Combine(lockDir, "CommunityFixesWindowed.stock.lock.json");
        File.Delete(derivedLock);
        var derived = await InstanceBuilder.BuildAsync(
            new CommunityFixesWindowed(),
            start,
            home,
            derivedLock,
            options,
            Cancel
        );
        Assert.Equal(instance.Hash, derived.Record.Parent);
        Assert.Equal("False", SettingsFile.ReadTopLevel(derived.Path("settings.cfg"))["FULLSCREEN"]);

        string texture = "GameData/Squad/Parts/Engine/liquidEngineLV-T45/model000.dds";
        var startId = FileIdentity.Get(Path.Combine(start.Root, texture));
        Assert.Equal(startId.Index, FileIdentity.Get(instance.Path(texture)).Index);
        Assert.Equal(startId.Index, FileIdentity.Get(derived.Path(texture)).Index);
        Assert.True(startId.Links >= 3);
    }

    [Fact]
    public async Task Retooled_instance_gets_the_compatible_versions()
    {
        var (start, home, lockDir) = Setup("KSPHARNESS_TEST_RETOOLED");
        Assert.True(start.IsRetooled);
        string lockPath = Path.Combine(lockDir, "CommunityFixes.retooled.lock.json");
        File.Delete(lockPath);

        var instance = await InstanceBuilder.BuildAsync(new CommunityFixes(), start, home, lockPath, Options(), Cancel);

        Assert.Equal("retooled", LockFile.Load(lockPath)!.Start);
        Assert.Contains("compat add 1.12 1.12.5", File.ReadAllText(instance.Path(".harness/ckan.log")));
        using var compat = JsonDocument.Parse(File.ReadAllText(instance.Path("CKAN/compatible_ksp_versions.json")));
        var versions = compat.RootElement.GetProperty("Versions").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Contains("1.12.5", versions);
        Assert.Contains("1.12", versions);
        Assert.True(Directory.Exists(instance.Path("GameData/KSPCommunityFixes")));
    }

    private static InstanceBuildOptions Options() =>
        new() { Log = message => TestContext.Current.SendDiagnosticMessage(message) };

    private static (StartingInstall Start, HarnessHome Home, string LockDir) Setup(string startVariable)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "file identity check is Windows only");
        string? startRoot = Environment.GetEnvironmentVariable(startVariable);
        string? homeRoot = Environment.GetEnvironmentVariable(HarnessHome.EnvironmentVariable);
        Assert.SkipWhen(
            string.IsNullOrEmpty(startRoot) || string.IsNullOrEmpty(homeRoot),
            $"set {startVariable} and {HarnessHome.EnvironmentVariable} to run"
        );
        Assert.SkipWhen(
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable(CkanClient.EnvironmentVariable)),
            $"set {CkanClient.EnvironmentVariable} to run"
        );

        var home = new HarnessHome(homeRoot!);
        string lockDir = Path.Combine(home.Root, "test-locks");
        Directory.CreateDirectory(lockDir);
        return (StartingInstall.Open(startRoot!), home, lockDir);
    }
}
