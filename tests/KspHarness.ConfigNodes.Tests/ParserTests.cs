namespace KspHarness.ConfigNodes.Tests;

public class ParserTests
{
    [Fact]
    public void Reads_values_and_nodes_with_braces_on_their_own_lines()
    {
        var root = ConfigParser.Parse("PART\n{\n\tname = a\n\tMODULE\n\t{\n\t\tname = M\n\t}\n}\n");

        var part = Assert.Single(root.Nodes);
        Assert.Equal("PART", part.Name);
        Assert.Equal("a", part.GetValue("name"));
        Assert.Equal("M", Assert.Single(part.Nodes).GetValue("name"));
    }

    [Fact]
    public void Reads_braces_on_the_same_line_and_one_line_nodes()
    {
        var root = ConfigParser.Parse("PART {\n name = a\n MODULE { name = M }\n} NEXT { x = 1 }");

        Assert.Equal(new[] { "PART", "NEXT" }, root.Nodes.Select(node => node.Name));
        Assert.Equal("M", root.Nodes[0].Nodes[0].GetValue("name"));
        Assert.Equal("1", root.Nodes[1].GetValue("x"));
    }

    [Fact]
    public void Keeps_duplicate_keys_in_order()
    {
        var root = ConfigParser.Parse("N\n{\n key = 1\n other = x\n key = 2\n}");

        Assert.Equal(new[] { "1", "2" }, root.Nodes[0].GetValues("key"));
        Assert.Equal(new[] { "key", "other", "key" }, root.Nodes[0].ValueList.Select(value => value.Key));
    }

    [Fact]
    public void Splits_on_the_first_equals_and_trims_both_sides()
    {
        var root = ConfigParser.Parse("  key\t=   a = b = c  \n= anonymous\nempty =\n");

        Assert.Equal("a = b = c", root.GetValue("key"));
        Assert.Equal("anonymous", root.GetValue(""));
        Assert.Equal("", root.GetValue("empty"));
    }

    [Fact]
    public void Drops_comments_from_the_first_double_slash_even_inside_values()
    {
        var root = ConfigParser.Parse("// header\nurl = http://example.com\nkey = value // note\n");

        Assert.Equal("http:", root.GetValue("url"));
        Assert.Equal("value", root.GetValue("key"));
        Assert.Equal(2, root.ValueList.Count);
    }

    [Fact]
    public void Splits_braces_inside_values_like_ksp()
    {
        // KSP tokenises every brace on a line, so "x{y}" becomes the value "x", an unnamed node, a dropped word "y"
        // and the close of that node.
        var root = ConfigParser.Parse("a = x{y}\nb = 2");

        Assert.Equal("x", root.GetValue("a"));
        Assert.Equal("2", root.GetValue("b"));
        var unnamed = Assert.Single(root.Nodes);
        Assert.Equal("", unnamed.Name);
        Assert.Empty(unnamed.ValueList);
    }

    [Fact]
    public void Drops_a_word_that_no_brace_follows()
    {
        var root = ConfigParser.Parse("stray\nkey = 1\nNODE\n{\n}\n");

        Assert.Equal("1", root.GetValue("key"));
        Assert.Equal("NODE", Assert.Single(root.Nodes).Name);
    }

    [Fact]
    public void Reads_unnamed_top_level_nodes()
    {
        var root = ConfigParser.Parse("{\n a = 1\n}\n");

        var node = Assert.Single(root.Nodes);
        Assert.Equal("", node.Name);
        Assert.Equal("1", node.GetValue("a"));
    }

    [Fact]
    public void Stops_at_a_stray_close_brace_at_the_top_level()
    {
        var root = ConfigParser.Parse("a = 1\n}\nb = 2\nNODE\n{\n}\n");

        Assert.Equal("1", root.GetValue("a"));
        Assert.Null(root.GetValue("b"));
        Assert.Empty(root.Nodes);
    }

    [Fact]
    public void Closes_unterminated_nodes_at_the_end_of_the_text()
    {
        var root = ConfigParser.Parse("A\n{\n B\n {\n  x = 1\n");

        Assert.Equal("1", root.Nodes[0].Nodes[0].GetValue("x"));
    }

    [Theory]
    [InlineData("﻿PART\r\n{\r\n\tname = a\r\n}\r\n")]
    [InlineData("PART\r{\rname = a\r}")]
    [InlineData("\t PART \t\n\t{\t\n\t\tname\t=\ta\t\n}")]
    public void Handles_bom_line_endings_and_tabs(string text)
    {
        var root = ConfigParser.Parse(text);

        var part = Assert.Single(root.Nodes);
        Assert.Equal("PART", part.Name);
        Assert.Equal("a", part.GetValue("name"));
    }

    [Fact]
    public void Reads_a_root_with_values_and_nodes_like_a_craft_file()
    {
        var root = ConfigParser.Parse("ship = Test\nversion = 1.12.5\nPART\n{\n part = mk1pod.v2_4294\n}\nPART\n{\n part = fuelTank_123\n}\n");

        Assert.Equal(ConfigParser.RootName, root.Name);
        Assert.Equal("Test", root.GetValue("ship"));
        Assert.Equal(2, root.GetNodes("PART").Count);
    }
}
