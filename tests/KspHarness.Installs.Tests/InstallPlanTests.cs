namespace KspHarness.Installs.Tests;

public sealed class InstallPlanTests
{
    public sealed class Base : InstallDefinition
    {
        public override void Configure(InstallBuilder install) => install.Ckan("ModuleManager").Setting("A", "1");
    }

    public sealed class Derived : InstallDefinition
    {
        public override void Configure(InstallBuilder install) =>
            install.Include<Base>().Ckan("KSPCommunityFixes", "1:1.41.1");
    }

    public sealed class TwoIncludes : InstallDefinition
    {
        public override void Configure(InstallBuilder install) => install.Include<Base>().Include<Derived>();
    }

    public sealed class SelfInclude : InstallDefinition
    {
        public override void Configure(InstallBuilder install) => install.Include<SelfInclude>();
    }

    [Fact]
    public void Included_definition_is_the_first_layer()
    {
        var plan = InstallPlan.Create(new Derived());

        Assert.Equal(["Base", "Derived"], plan.Layers.Select(layer => layer.Definition.Name));
        Assert.Equal(["ckan ModuleManager", "setting A=1"], plan.Layers[0].Fingerprints);
        Assert.Equal(["ckan KSPCommunityFixes=1:1.41.1"], plan.Layers[1].Fingerprints);
    }

    [Fact]
    public void Only_one_include_is_allowed()
    {
        Assert.Throws<InvalidOperationException>(() => InstallPlan.Create(new TwoIncludes()));
    }

    [Fact]
    public void Include_cycles_are_reported()
    {
        var error = Assert.Throws<InvalidOperationException>(() => InstallPlan.Create(new SelfInclude()));
        Assert.Contains("includes itself", error.Message);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData("-leading")]
    public void Bad_ckan_identifiers_are_rejected(string identifier)
    {
        Assert.Throws<ArgumentException>(() => new InstallBuilder().Ckan(identifier));
    }

    [Fact]
    public void Archive_checks_hash_and_subfolder()
    {
        var builder = new InstallBuilder();
        Assert.Throws<ArgumentException>(() => builder.Archive("a", "https://x/a.zip", "abc"));
        Assert.Throws<ArgumentException>(() => builder.Archive("a", "https://x/a.zip", new string('a', 64), "../out"));

        builder.Archive("a", "https://x/a.zip", new string('A', 64), "Mod");
        Assert.Equal(new string('a', 64), Assert.IsType<ArchiveStep>(builder.Steps[0]).Sha256);
    }

    [Fact]
    public void Directory_fingerprint_follows_content()
    {
        using var dir = new TempDir();
        dir.Write("data/persistent.sfs", "GAME {}");

        string first = new InstallBuilder().Saves(dir.Combine("data")).Steps[0].Fingerprint;
        string same = new InstallBuilder().Saves(dir.Combine("data")).Steps[0].Fingerprint;
        dir.Write("data/persistent.sfs", "GAME { changed }");
        string changed = new InstallBuilder().Saves(dir.Combine("data")).Steps[0].Fingerprint;

        Assert.Equal(first, same);
        Assert.NotEqual(first, changed);
        Assert.StartsWith("directory saves/data ", first);
    }

    [Fact]
    public void Compatible_version_must_look_like_a_version()
    {
        Assert.Throws<ArgumentException>(() => new InstallBuilder().CompatibleVersion("latest"));
        new InstallBuilder().CompatibleVersion("1.12.5");
    }
}
