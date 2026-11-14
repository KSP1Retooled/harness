using System.Globalization;

namespace KspHarness.ConfigNodes.Tests;

/// <summary>Reads a real ModuleManager.ConfigCache (KSPHARNESS_TEST_MM_CACHE) without changing it.</summary>
public class ModuleManagerCacheTests
{
    private static readonly Lazy<ConfigDatabase> Database = new(() =>
        ConfigDatabase.LoadModuleManagerCache(TestData.ModuleManagerCache)
    );

    [FactWithData(TestData.ModuleManagerCacheVariable)]
    public void Loads_every_url_config_with_its_file()
    {
        var root = ConfigFile.Load(TestData.ModuleManagerCache);
        var entries = root.GetNodes("UrlConfig");

        Assert.True(entries.Count > 100);
        Assert.Equal(entries.Sum(entry => entry.Nodes.Count), Database.Value.Configs.Count);
        Assert.All(Database.Value.Configs, config => Assert.False(string.IsNullOrEmpty(config.File)));
        Assert.NotNull(Database.Value.PatchedNodeCount);
    }

    [FactWithData(TestData.ModuleManagerCacheVariable)]
    public void Gives_ksp_urls()
    {
        var pod = Assert.Single(Database.Value.SelectConfigs("PART[mk1pod_v2]"));

        Assert.Equal("Squad/Parts/Command/mk1pod_v2/mk1Pod_v2.cfg", pod.File);
        Assert.Equal("Squad/Parts/Command/mk1pod_v2/mk1Pod_v2/mk1pod_v2", pod.Url);
        Assert.Equal("PART", pod.Type);
    }

    [FactWithData(TestData.ModuleManagerCacheVariable)]
    public void Queries_the_patched_database()
    {
        var database = Database.Value;

        var crew = database.SelectValues("PART[mk1pod*]/MODULE[ModuleCommand]/minimumCrew");
        var engineModules = database.Select("//MODULE[ModuleEngines*]");
        var bigEngineParts = database.Select("PART:HAS[@MODULE[ModuleEngines*]:HAS[#maxThrust[>100]]]");

        Assert.Equal(1, Assert.Single(crew).As<int>());
        var expectedModules = database.Nodes.SelectMany(Descendants).Where(IsEngineModule).ToList();
        Assert.NotEmpty(engineModules);
        Assert.Equal(expectedModules, engineModules);
        var expectedParts = database
            .Nodes.Where(node => node.Name == "PART")
            .Where(part =>
                part.GetNodes("MODULE")
                    .Any(module =>
                        IsEngineModule(module)
                        && module
                            .GetValues("maxThrust")
                            .Any(text =>
                                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var thrust)
                                && thrust > 100
                            )
                    )
            )
            .ToList();
        Assert.NotEmpty(bigEngineParts);
        Assert.Equal(expectedParts, bigEngineParts);
    }

    [FactWithData(TestData.ModuleManagerCacheVariable)]
    public void Round_trips_the_cache_through_the_writer()
    {
        var parsed = ConfigFile.Load(TestData.ModuleManagerCache);

        var reparsed = ConfigParser.Parse(ConfigWriter.WriteRoot(parsed));

        Assert.True(TestData.SameTree(parsed, reparsed));
    }

    private static IEnumerable<CfgNode> Descendants(CfgNode node)
    {
        foreach (var child in node.Nodes)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static bool IsEngineModule(CfgNode node)
    {
        return node.Name == "MODULE" && node.NameValue?.StartsWith("ModuleEngines", StringComparison.Ordinal) == true;
    }
}
