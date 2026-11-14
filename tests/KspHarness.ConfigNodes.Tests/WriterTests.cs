namespace KspHarness.ConfigNodes.Tests;

public class WriterTests
{
    [Fact]
    public void Writes_ksp_layout()
    {
        var root = new CfgNode(ConfigParser.RootName);
        root.AddValue("ship", "Test");
        var part = root.AddNode("PART");
        part.AddValue("name", "a");
        part.AddNode("MODULE").AddValue("name", "M");

        var text = ConfigWriter.WriteRoot(root);

        Assert.Equal("ship = Test\nPART\n{\n\tname = a\n\tMODULE\n\t{\n\t\tname = M\n\t}\n}\n", text);
    }

    [Fact]
    public void Writes_a_single_node_with_its_braces()
    {
        var node = new CfgNode("RESOURCE").AddValue("amount", "1");

        Assert.Equal("RESOURCE\n{\n\tamount = 1\n}\n", node.ToString());
        Assert.Equal("RESOURCE\r\n{\r\n\tamount = 1\r\n}\r\n", ConfigWriter.WriteNode(node, "\r\n"));
    }

    [Fact]
    public void Turns_braces_into_brackets_so_text_reads_back_the_same()
    {
        var root = new CfgNode(ConfigParser.RootName);
        root.AddValue("a=b", "{x}");

        var reread = ConfigParser.Parse(ConfigWriter.WriteRoot(root));

        Assert.Equal("[x]", reread.GetValue("a-b"));
        Assert.Empty(reread.Nodes);
    }

    [Fact]
    public void Round_trips_parsed_text()
    {
        // KSP writes an empty key or node name as nothing before " = " or the brace line.
        const string text = "a = 1\nb = x = y\n = anonymous\nN\n{\n\tname = n\n\t\n\t{\n\t\tinner = 1\n\t}\n\tM\n\t{\n\t}\n}\n";

        var root = ConfigParser.Parse(text);

        Assert.Equal(text, ConfigWriter.WriteRoot(root));
    }
}
