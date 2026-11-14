using System.Dynamic;
using System.Text.Json.Nodes;
using KspHarness.Script;
using KspHarness.Values;

namespace KspHarness.Tests;

public sealed class ValueTests
{
    public enum Scene
    {
        MainMenu = 2,
        Flight = 7,
    }

    [Flags]
    public enum Layers
    {
        None = 0,
        A = 1,
        B = 2,
    }

    public struct Vector3
    {
        public float x;
        public float y;
        public float z;
    }

    public struct Color
    {
        public float r;
        public float g;
        public float b;
        public float a;
    }

    public sealed record Orbit(double Apoapsis, double Periapsis, string Body);

    public sealed class Settable
    {
        public int Count { get; set; }

        public string? Name;
    }

    [Theory]
    [InlineData("1", typeof(int), 1)]
    [InlineData("1", typeof(double), 1.0)]
    [InlineData("2.5", typeof(float), 2.5f)]
    [InlineData("3.0", typeof(long), 3L)]
    [InlineData("\"text\"", typeof(string), "text")]
    [InlineData("true", typeof(bool), true)]
    public void Scalars_convert_to_the_requested_type(string json, Type type, object expected)
    {
        Assert.Equal(expected, Decode(json, type));
    }

    [Fact]
    public void Special_numbers_round_trip()
    {
        Assert.Equal("""{"$":"num","v":"NaN"}""", ValueEncoder.Encode(double.NaN)!.ToJsonString());
        Assert.True(double.IsNaN((double)Decode("""{"$":"num","v":"NaN"}""", typeof(double))!));
        Assert.Equal(double.NegativeInfinity, Decode("""{"$":"num","v":"-Infinity"}""", typeof(double)));
    }

    [Fact]
    public void Enums_convert_by_name()
    {
        var json = """{"$":"enum","type":"GameScenes","name":"Flight","value":7}""";

        Assert.Equal(Scene.Flight, Decode(json, typeof(Scene)));
        Assert.Equal("Flight", Decode(json, typeof(string)));
        Assert.Equal(7, Decode(json, typeof(int)));
        Assert.Equal(Layers.A | Layers.B, Decode("""{"$":"enum","type":"L","name":"A, B","value":3}""", typeof(Layers)));
    }

    [Fact]
    public void Enums_encode_with_name_and_value()
    {
        var encoded = ValueEncoder.Encode(Scene.Flight)!;

        Assert.Equal("enum", encoded["$"]!.GetValue<string>());
        Assert.Equal(typeof(Scene).FullName, encoded["type"]!.GetValue<string>());
        Assert.Equal("Flight", encoded["name"]!.GetValue<string>());
        Assert.Equal(7, encoded["value"]!.GetValue<long>());
    }

    [Fact]
    public void Vectors_convert_to_matching_structs_and_arrays()
    {
        var json = """{"$":"vec","type":"Vector3","v":[1,2,3]}""";

        var vector = (Vector3)Decode(json, typeof(Vector3))!;
        Assert.Equal((1f, 2f, 3f), (vector.x, vector.y, vector.z));
        Assert.Equal([1.0, 2.0, 3.0], (double[])Decode(json, typeof(double[]))!);

        var color = (Color)Decode("""{"$":"vec","type":"Color","v":[0.5,0.25,0,1]}""", typeof(Color))!;
        Assert.Equal((0.5f, 0.25f, 0f, 1f), (color.r, color.g, color.b, color.a));
    }

    [Fact]
    public void Vectors_encode_by_component()
    {
        var vector = new Vector3 { x = 1, y = 2, z = 3 };

        Assert.Equal("""{"$":"vec","type":"Vector3","v":[1,2,3]}""", ValueEncoder.Encode(vector)!.ToJsonString());
    }

    [Fact]
    public void Objects_convert_to_records_classes_tuples_and_dictionaries()
    {
        var json = """{"$":"obj","type":"x","fields":{"apoapsis":80000,"Periapsis":75000.5,"body":"Kerbin"}}""";

        Assert.Equal(new Orbit(80000, 75000.5, "Kerbin"), Decode(json, typeof(Orbit)));

        var settable = (Settable)Decode("""{"$":"obj","type":"x","fields":{"count":3,"name":"n"}}""", typeof(Settable))!;
        Assert.Equal((3, "n"), (settable.Count, settable.Name));

        var tuple = Decode("""{"$":"obj","type":"System.ValueTuple`2","fields":{"Item1":1,"Item2":"b"}}""", typeof((int, string)));
        Assert.Equal((1, "b"), tuple);

        var dictionary = (Dictionary<string, double>)Decode("""{"$":"obj","type":"x","fields":{"a":1,"b":2.5}}""", typeof(Dictionary<string, double>))!;
        Assert.Equal(2.5, dictionary["b"]);
    }

    [Fact]
    public void Arrays_convert_to_arrays_and_lists()
    {
        Assert.Equal([1, 2, 3], (int[])Decode("[1,2,3]", typeof(int[]))!);
        Assert.Equal(["a", "b"], (List<string>)Decode("""["a","b"]""", typeof(List<string>))!);
        Assert.Equal([1.5], (IReadOnlyList<double>)Decode("[1.5]", typeof(IReadOnlyList<double>))!);
    }

    [Fact]
    public void Game_time_bytes_and_handles_convert()
    {
        Assert.Equal(GameTime.Seconds(12.5), Decode("""{"$":"gametime","seconds":12.5}""", typeof(GameTime)));
        Assert.Equal([1, 2, 3], (byte[])Decode("""{"$":"bytes","v":"AQID"}""", typeof(byte[]))!);

        var handle = (Handle)Decode("""{"$":"handle","id":4,"type":"Vessel","assembly":"Assembly-CSharp","text":"X"}""", typeof(Handle))!;
        Assert.Equal((4L, "Vessel"), (handle.Id, handle.Type));

        var error = Assert.Throws<InvalidCastException>(() => Decode("""{"$":"handle","id":4,"type":"Vessel"}""", typeof(double)));
        Assert.Contains("Handle", error.Message);
    }

    [Fact]
    public void Dynamic_results_keep_integers_and_doubles_apart()
    {
        Assert.IsType<int>(Dynamic("3"));
        Assert.IsType<long>(Dynamic("30000000000"));
        Assert.IsType<double>(Dynamic("3.0"));
        Assert.Equal([1.0, 2.5], (double[])Dynamic("[1,2.5]")!);
        Assert.Equal("FLIGHT", Dynamic("""{"$":"enum","type":"GameScenes","name":"FLIGHT","value":7}"""));
        Assert.Equal([1.0, 2.0, 3.0], (double[])Dynamic("""{"$":"vec","type":"Vector3","v":[1,2,3]}""")!);

        dynamic obj = Dynamic("""{"$":"obj","type":"x","fields":{"altitude":70000.5,"name":"Kerbal X"}}""")!;
        Assert.IsType<ExpandoObject>((object)obj);
        Assert.Equal(70000.5, (double)obj.altitude);
        Assert.Equal("Kerbal X", (string)obj.name);
    }

    [Fact]
    public void Arguments_encode_per_the_value_table()
    {
        Assert.Equal("[1,2]", ValueEncoder.Encode(new[] { 1, 2 })!.ToJsonString());
        Assert.Equal("""{"$":"bytes","v":"AQI="}""", ValueEncoder.Encode(new byte[] { 1, 2 })!.ToJsonString());
        Assert.Equal("""{"$":"gametime","seconds":3}""", ValueEncoder.Encode(GameTime.Seconds(3))!.ToJsonString());
        var tuple = ValueEncoder.Encode((1, "a"))!;
        Assert.Equal("obj", tuple["$"]!.GetValue<string>());
        Assert.Equal("System.ValueTuple`2", tuple["type"]!.GetValue<string>());
        Assert.Equal("""{"Item1":1,"Item2":"a"}""", tuple["fields"]!.ToJsonString());
        Assert.Throws<ArgumentException>(() => ValueEncoder.Encode(new object()));
    }

    private static object? Decode(string json, Type type) => GameValue.Convert(JsonNode.Parse(json), type, null);

    private static object? Dynamic(string json) => GameValue.ToDynamic(JsonNode.Parse(json), null);
}
