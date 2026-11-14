namespace KspHarness.ConfigNodes.Tests;

public class ValueParserTests
{
    private enum Situation
    {
        LANDED = 1,
        ORBITING = 32,
    }

    [Flags]
    private enum Mask
    {
        None = 0,
        A = 1,
        B = 2,
    }

    [Fact]
    public void Parses_numbers_in_the_invariant_culture()
    {
        var node = new CfgNode("N")
            .AddValue("i", "-42")
            .AddValue("l", "4294967296")
            .AddValue("f", "0.25")
            .AddValue("d", "1.5E-05")
            .AddValue("u", "4294967295");

        Assert.Equal(-42, node.Value<int>("i"));
        Assert.Equal(4294967296L, node.Value<long>("l"));
        Assert.Equal(0.25f, node.Value<float>("f"));
        Assert.Equal(1.5e-5, node.Value<double>("d"));
        Assert.Equal(uint.MaxValue, node.Value<uint>("u"));
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("TRUE", true)]
    public void Parses_bools_in_any_case(string text, bool expected)
    {
        Assert.Equal(expected, ValueParser.Parse<bool>(text));
    }

    [Fact]
    public void Parses_enums_by_name_value_and_flag_list()
    {
        Assert.Equal(Situation.LANDED, ValueParser.Parse<Situation>("LANDED"));
        Assert.Equal(Situation.ORBITING, ValueParser.Parse<Situation>("32"));
        Assert.Equal(Mask.A | Mask.B, ValueParser.Parse<Mask>("A, B"));
        Assert.False(ValueParser.TryParse<Situation>("landed", out _));
    }

    [Fact]
    public void Parses_vectors_and_lists()
    {
        Assert.Equal(new[] { 0.0, -1.5, 2.0 }, ValueParser.Parse<double[]>("0,-1.5, 2"));
        Assert.Equal(new[] { 1f, 2f, 3f, 4f }, ValueParser.Parse<float[]>("1 2\t3,4"));
        Assert.Equal(new List<int> { 1, 2 }, ValueParser.Parse<List<int>>("1, 2"));
        Assert.Equal(new[] { "a b", "c" }, ValueParser.Parse<string[]>(" a b , c"));
        Assert.Empty(ValueParser.Parse<string[]>(""));
    }

    [Fact]
    public void Parses_colours_with_optional_alpha()
    {
        var opaque = ValueParser.Parse<CfgColor>("1,0.5,0");
        var translucent = ValueParser.Parse<CfgColor>("0.1, 0.2, 0.3, 0.4");

        Assert.Equal((1f, 0.5f, 0f, 1f), (opaque.R, opaque.G, opaque.B, opaque.A));
        Assert.Equal(0.4f, translucent.A);
        Assert.False(ValueParser.TryParse<CfgColor>("1,2", out _));
    }

    [Fact]
    public void Reports_missing_and_malformed_values()
    {
        var node = new CfgNode("N").AddValue("x", "abc").AddValue("n", "1").AddValue("n", "2");

        Assert.Throws<KeyNotFoundException>(() => node.Value<int>("missing"));
        Assert.Throws<FormatException>(() => node.Value<int>("x"));
        Assert.Equal(7, node.ValueOr("x", 7));
        Assert.Equal(new List<int> { 1, 2 }, node.Values<int>("n"));
        Assert.True(node.TryGetValue("n", out int first));
        Assert.Equal(1, first);
        Assert.Equal(5, (int?)ValueParser.Parse<int?>("5"));
    }
}
