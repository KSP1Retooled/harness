using System.Text.Json.Nodes;
using KspHarness.Tests.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace KspHarness.Tests;

/// <summary>Test code loaded per game binds to that game's assemblies, so each session decodes into its own types.</summary>
public sealed class GameAssembliesTests
{
    [Fact]
    public void Test_code_loaded_into_each_game_sees_that_games_types()
    {
        var dir = TestInstalls.TempDir("game-assemblies");
        var stock = Directory.CreateDirectory(Path.Combine(dir, "stock", "Managed")).FullName;
        var retooled = Directory.CreateDirectory(Path.Combine(dir, "retooled", "Managed")).FullName;
        var stockGame = Build(stock, "Assembly-CSharp", "public enum GameScenes { MAINMENU, FLIGHT }", []);
        Build(retooled, "Assembly-CSharp", "public enum GameScenes { MAINMENU, FLIGHT, RETOOLED_ONLY }", []);

        // Test code compiled against stock, as a suite project is.
        var tests = Build(
            Path.Combine(dir, "tests"),
            "FakeSuite",
            "public static class Probe { public static System.Type Scenes => typeof(GameScenes); }",
            [stockGame]
        );

        var inStock = SceneType(GameAssemblies.LoadInto(stock, tests));
        var inRetooled = SceneType(GameAssemblies.LoadInto(retooled, tests));

        Assert.NotEqual(inStock, inRetooled);
        Assert.Equal(stock, Path.GetDirectoryName(inStock.Assembly.Location));
        Assert.Equal(retooled, Path.GetDirectoryName(inRetooled.Assembly.Location));
        Assert.Same(GameAssemblies.LoadInto(retooled, tests), GameAssemblies.LoadInto(retooled, tests));

        // A value only Retooled has decodes in the Retooled copy and not in the stock one.
        var value = JsonValue.Create("RETOOLED_ONLY");
        Assert.Equal("RETOOLED_ONLY", GameValue.Convert(value, inRetooled, null)!.ToString());
        Assert.ThrowsAny<ArgumentException>(() => GameValue.Convert(value, inStock, null));

        // The harness's own types are shared, so the runner can use what the copies create.
        var harnessInCopy = GameAssemblies
            .ContextFor(retooled)
            .LoadFromAssemblyName(typeof(GameSession).Assembly.GetName());
        Assert.Same(typeof(GameSession).Assembly, harnessInCopy);
    }

    private static Type SceneType(System.Reflection.Assembly tests)
    {
        return (Type)tests.GetType("Probe", throwOnError: true)!.GetProperty("Scenes")!.GetValue(null)!;
    }

    private static string Build(string dir, string name, string source, string[] references)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".dll");
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                .. references.Select(r => MetadataReference.CreateFromFile(r)),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return path;
    }
}
