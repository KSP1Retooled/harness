using System.Text.RegularExpressions;

namespace KspHarness.ConfigNodes.Tests;

/// <summary>Reads the files of a stock KSP 1.12.5 install (KSP_DIR) without changing them.</summary>
public class StockFileTests
{
    private static readonly string[] ConfigExtensions = { ".cfg", ".craft", ".sfs", ".loadmeta" };

    private static IEnumerable<string> ConfigFiles()
    {
        foreach (var folder in new[] { "GameData", "saves", "Ships" })
        {
            var path = Path.Combine(TestData.KspDir, folder);
            if (!Directory.Exists(path))
                continue;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (ConfigExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    yield return file;
            }
        }
    }

    [FactWithData(TestData.KspDirVariable)]
    public void Every_config_file_round_trips_through_the_writer()
    {
        var failures = new List<string>();
        var count = 0;
        foreach (var file in ConfigFiles())
        {
            count++;
            var parsed = ConfigFile.Load(file);
            var written = ConfigWriter.WriteRoot(parsed);
            var reparsed = ConfigParser.Parse(written);
            if (!TestData.SameTree(parsed, reparsed) || ConfigWriter.WriteRoot(reparsed) != written)
                failures.Add(file);
        }

        Assert.True(count > 900, $"only {count} config files found");
        Assert.Empty(failures);
    }

    [FactWithData(TestData.KspDirVariable)]
    public void Finds_every_part_node_in_squad_configs()
    {
        // An independent count: lines that are just PART, the way every stock part file opens its node.
        var partLine = new Regex(@"^\s*PART\s*$");
        var expected = 0;
        var actual = 0;
        foreach (
            var file in Directory.EnumerateFiles(
                Path.Combine(TestData.KspDir, "GameData", "Squad"),
                "*.cfg",
                SearchOption.AllDirectories
            )
        )
        {
            expected += File.ReadLines(file).Count(line => partLine.IsMatch(line));
            actual += ConfigFile.Load(file).GetNodes("PART").Count;
        }

        Assert.True(expected > 200);
        Assert.Equal(expected, actual);
    }

    [FactWithData(TestData.KspDirVariable)]
    public void Reads_the_mk1_pod_minimum_crew()
    {
        var file = Path.Combine(TestData.KspDir, "GameData", "Squad", "Parts", "Command", "mk1pod_v2", "mk1Pod_v2.cfg");

        var root = ConfigFile.Load(file);

        var value = Assert.Single(root.SelectValues("PART[mk1pod*]/MODULE[ModuleCommand]/minimumCrew"));
        Assert.Equal(1, value.As<int>());
        Assert.Equal("mk1pod_v2", root.Nodes[0].NameValue);
        Assert.Equal(1, root.Nodes[0].Value<int>("CrewCapacity"));
    }

    [FactWithData(TestData.KspDirVariable)]
    public void Queries_engines_across_squad_game_data()
    {
        var database = ConfigDatabase.LoadGameData(Path.Combine(TestData.KspDir, "GameData"));

        var engineModules = database.Select("//MODULE[ModuleEngines*]");
        var bigEngineParts = database.Select("PART:HAS[@MODULE[ModuleEngines*]:HAS[#maxThrust[>100]]]");

        var expectedModules = database.Nodes.SelectMany(Descendants).Where(IsEngineModule).ToList();
        var expectedParts = database
            .Nodes.Where(node => node.Name == "PART")
            .Where(part =>
                part.GetNodes("MODULE")
                    .Any(module =>
                        IsEngineModule(module)
                        && module
                            .GetValues("maxThrust")
                            .Any(text =>
                                double.TryParse(
                                    text,
                                    System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    out var thrust
                                )
                                && thrust > 100
                            )
                    )
            )
            .ToList();

        Assert.True(engineModules.Count > 50);
        Assert.Equal(expectedModules, engineModules);
        Assert.NotEmpty(bigEngineParts);
        Assert.Equal(expectedParts, bigEngineParts);
        Assert.Contains(
            database.SelectConfigs("PART[liquidEngine]"),
            config => config.Url == "Squad/Parts/Engine/liquidEngineLV-T30/liquidEngineLV-T30/liquidEngine"
        );
    }

    [FactWithData(TestData.KspDirVariable)]
    public void Finds_landed_vessels_in_a_save()
    {
        var root = ConfigFile.Load(Path.Combine(TestData.KspDir, "saves", "scenarios", "Mun Rover.sfs"));
        var flightState = root.SelectFirst("GAME/FLIGHTSTATE")!;

        var landed = flightState.Select("VESSEL:HAS[#sit[LANDED]]");

        Assert.Equal(new[] { "#autoLOC_6002442", "#autoLOC_6002443" }, landed.Select(vessel => vessel.NameValue));
        Assert.Equal(landed, root.Select("//VESSEL:HAS[#sit[LANDED]]"));
        Assert.All(
            root.Select("//VESSEL:HAS[~sit[LANDED]]"),
            vessel => Assert.Equal("ORBITING", vessel.GetValue("sit"))
        );
    }

    [FactWithData(TestData.KspDirVariable)]
    public void Reads_a_craft_file()
    {
        var root = ConfigFile.Load(Path.Combine(TestData.KspDir, "Ships", "VAB", "Kerbal X.craft"));

        Assert.Equal("#autoLOC_501232", root.GetValue("ship"));
        Assert.Equal("VAB", root.GetValue("type"));
        Assert.Equal(3, root.Value<double[]>("size").Length);
        Assert.True(root.GetNodes("PART").Count > 10);
        Assert.NotEmpty(root.Select("PART:HAS[#part[mk1-3pod*]]"));
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
