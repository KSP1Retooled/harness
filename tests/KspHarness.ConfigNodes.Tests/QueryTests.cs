namespace KspHarness.ConfigNodes.Tests;

public class QueryTests
{
    private const string Parts = """
        PART
        {
            name = mk1pod_v2
            MODULE
            {
                name = ModuleCommand
                minimumCrew = 1
            }
            RESOURCE
            {
                name = MonoPropellant
                amount = 10
            }
        }
        PART
        {
            name = liquidEngine
            MODULE
            {
                name = ModuleEnginesFX
                maxThrust = 215
                PROPELLANT
                {
                    name = LiquidFuel
                }
                PROPELLANT
                {
                    name = Oxidizer
                }
            }
            MODULE
            {
                name = ModuleGimbal
            }
        }
        PART
        {
            name = smallEngine
            title = Small Engine
            MODULE
            {
                name = ModuleEngines
                maxThrust = 20
            }
        }
        PART
        {
            title = No Name
        }
        """;

    private static readonly CfgNode Root = ConfigParser.Parse(Parts);

    private static string[] Names(IEnumerable<CfgNode> nodes)
    {
        return nodes.Select(node => node.NameValue ?? "-").ToArray();
    }

    [Fact]
    public void Matches_types_and_name_patterns()
    {
        Assert.Equal(new[] { "mk1pod_v2", "liquidEngine", "smallEngine", "-" }, Names(Root.Select("PART")));
        Assert.Equal(new[] { "mk1pod_v2" }, Names(Root.Select("PART[mk1pod*]")));
        Assert.Equal(new[] { "liquidEngine", "smallEngine" }, Names(Root.Select("PART[*Engine]")));
        Assert.Equal(new[] { "mk1pod_v2", "smallEngine" }, Names(Root.Select("PART[mk1pod_v?|small*]")));
        Assert.Equal(new[] { "mk1pod_v2", "smallEngine" }, Names(Root.Select("PART[smallEngine,mk1pod_v2]")));
        Assert.Empty(Root.Select("PART[MK1POD*]"));
        Assert.Empty(Root.Select("part"));
    }

    [Fact]
    public void Follows_child_and_descendant_paths()
    {
        Assert.Equal(new[] { "ModuleCommand" }, Names(Root.Select("PART[mk1pod*]/MODULE")));
        Assert.Equal(new[] { "ModuleEnginesFX", "ModuleEngines" }, Names(Root.Select("//MODULE[ModuleEngines*]")));
        Assert.Equal(new[] { "LiquidFuel", "Oxidizer" }, Names(Root.Select("PART//PROPELLANT")));
        Assert.Equal(new[] { "LiquidFuel", "Oxidizer" }, Names(Root.Select("//PROPELLANT")));
        Assert.Equal(new[] { "ModuleEnginesFX", "ModuleGimbal" }, Names(Root.Select("PART[liquidEngine]/*")));
    }

    [Fact]
    public void Picks_by_index_among_siblings()
    {
        Assert.Equal(new[] { "liquidEngine" }, Names(Root.Select("PART,1")));
        Assert.Equal(new[] { "-" }, Names(Root.Select("PART,-1")));
        Assert.Empty(Root.Select("PART,9"));
        Assert.Equal(4, Root.Select("PART,*").Count);
        Assert.Equal(new[] { "ModuleGimbal" }, Names(Root.Select("PART/MODULE,1")));
        Assert.Equal(new[] { "Oxidizer" }, Names(Root.Select("//PROPELLANT,1")));
    }

    [Fact]
    public void Selects_values_with_their_owning_node()
    {
        var values = Root.SelectValues("PART[mk1pod*]/MODULE[ModuleCommand]/minimumCrew");

        var value = Assert.Single(values);
        Assert.Equal("minimumCrew", value.Key);
        Assert.Equal("1", value.Value);
        Assert.Equal(1, value.As<int>());
        Assert.Equal("ModuleCommand", value.Node.NameValue);
        Assert.Equal(new[] { "215", "20" }, Root.SelectValues("PART/MODULE/#maxThrust").Select(v => v.Value));
        Assert.Equal(
            new[] { "LiquidFuel", "Oxidizer" },
            Root.SelectValues("PART[liquidEngine]//name").Skip(2).Take(2).Select(v => v.Value)
        );
        Assert.Equal(new[] { "Small Engine", "No Name" }, Root.SelectValues("PART/ti*").Select(v => v.Value));
        Assert.Equal(new[] { "mk1pod_v2" }, Root.Nodes[0].SelectValues("name").Select(v => v.Value));
    }

    [Fact]
    public void Has_child_node_constraints()
    {
        Assert.Equal(new[] { "liquidEngine", "smallEngine" }, Names(Root.Select("PART:HAS[@MODULE[ModuleEngines*]]")));
        Assert.Equal(new[] { "mk1pod_v2", "-" }, Names(Root.Select("PART:HAS[!MODULE[ModuleEngines*]]")));
        Assert.Equal(
            new[] { "liquidEngine" },
            Names(Root.Select("PART:HAS[@MODULE[ModuleEngines*]:HAS[#maxThrust[>100]]]"))
        );
        Assert.Equal(
            new[] { "smallEngine" },
            Names(Root.Select("PART:HAS[@MODULE[ModuleEngines*]:HAS[#maxThrust[<100]]]"))
        );
        Assert.Equal(new[] { "liquidEngine" }, Names(Root.Select("PART:HAS[@MODULE:HAS[@PROPELLANT[Oxidizer]]]")));
    }

    [Fact]
    public void Has_value_constraints()
    {
        Assert.Equal(new[] { "smallEngine", "-" }, Names(Root.Select("PART:HAS[#title]")));
        Assert.Equal(new[] { "smallEngine" }, Names(Root.Select("PART:HAS[#title[Small?Engine]]")));
        Assert.Equal(new[] { "mk1pod_v2", "liquidEngine" }, Names(Root.Select("PART:HAS[~title[]]")));
        Assert.Equal(new[] { "mk1pod_v2", "liquidEngine" }, Names(Root.Select("PART:HAS[~title]")));
        Assert.Equal(new[] { "mk1pod_v2", "liquidEngine", "-" }, Names(Root.Select("PART:HAS[~title[Small*]]")));
    }

    [Fact]
    public void Combines_constraints_with_commas_as_and()
    {
        Assert.Equal(new[] { "smallEngine" }, Names(Root.Select("PART:HAS[#title, @MODULE[ModuleEngines]]")));
        Assert.Equal(new[] { "smallEngine" }, Names(Root.Select("PART:HAS[#title]:HAS[@MODULE]")));
        Assert.Equal(
            new[] { "liquidEngine" },
            Names(Root.Select("PART:HAS[@MODULE[ModuleEnginesFX]:HAS[#maxThrust[>100],@PROPELLANT[LiquidFuel]]]"))
        );
        Assert.Empty(Root.Select("PART:HAS[@MODULE[ModuleEnginesFX]:HAS[#maxThrust[>100],!PROPELLANT[LiquidFuel]]]"));
    }

    [Fact]
    public void Runs_over_a_list_of_top_level_nodes()
    {
        var query = ConfigQuery.Parse("PART[small*]/MODULE/maxThrust");

        var values = query.SelectValues((IEnumerable<CfgNode>)Root.Nodes, CfgNodeAdapter.Instance);

        Assert.Equal("20", Assert.Single(values).Value);
    }

    [Theory]
    [InlineData("PART[")]
    [InlineData("PART]")]
    [InlineData("PART:HAS[x]")]
    [InlineData("PART,x")]
    [InlineData("PART/")]
    [InlineData("PART[a]junk")]
    public void Rejects_malformed_queries(string query)
    {
        Assert.Throws<FormatException>(() => Root.Select(query));
    }

    [Fact]
    public void Rejects_names_on_a_key_segment()
    {
        Assert.Throws<FormatException>(() => Root.SelectValues("PART/name[x]"));
    }
}
