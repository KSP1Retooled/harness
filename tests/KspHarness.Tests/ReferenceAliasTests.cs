using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json.Nodes;
using KspHarness.Snippets;
using KspHarness.Tests.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace KspHarness.Tests;

/// <summary>A type two references both declare resolves to the earlier reference, as the game binds it.</summary>
public sealed class ReferenceAliasTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public void Game_type_names_pick_the_earliest_reference()
    {
        var dir = TestInstalls.TempDir("aliases");
        var first = Build(dir, "Second", "namespace Dup { public class T { } }");
        var second = Build(dir, "A.First", "namespace Dup { public class T { } }");

        // Priority is the order given, not the name.
        var references = ReferenceSet.FromFiles([first, second]).BaseCompilation;

        Assert.Equal("global::Dup.T", GameTypeNames.FromGameName(references, "Dup.T"));
        Assert.Equal("global::Dup.T", GameTypeNames.FromGameName(references, "Dup.T", "Second"));
        Assert.Null(GameTypeNames.FromGameName(references, "Dup.T", "A.First"));
        Assert.Equal("global::Dup.T[]", GameTypeNames.FromGameName(references, "Dup.T[]"));
        Assert.Equal("_A_First", ReferenceSet.AliasFor("A.First"));
    }

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public async Task TMP_Text_binds_to_the_games_own_TextMesh_Pro(string install)
    {
        await using var fake = await FakeSession.StartAsync(
            install,
            game => game.OnRun = (_, _) => Task.FromResult<JsonNode?>(1)
        );

        await fake.Session.Run<int>(
            """
            TMPro.TMP_Text text = Object.FindObjectOfType<TMPro.TMP_Text>();
            var ugui = Object.FindObjectOfType<TMPro.TextMeshProUGUI>();
            return text == null || ugui == null ? 0 : text.text.Length;
            """,
            cancellationToken: Cancel
        );

        // KSP declares TMPro in Assembly-CSharp; Retooled also ships Unity's copy, which stock-built code never names.
        var assembly = fake.Game.Loaded.Values.Single();
        Assert.Equal("Assembly-CSharp", ScopeOf(assembly, "TMPro", "TMP_Text"));
        Assert.Equal("Assembly-CSharp", ScopeOf(assembly, "TMPro", "TextMeshProUGUI"));
    }

    [Fact]
    public async Task Unity_TextMesh_Pro_stays_reachable_through_its_alias()
    {
        await using var fake = await FakeSession.StartAsync(
            TestInstalls.Retooled,
            game => game.OnRun = (_, _) => Task.FromResult<JsonNode?>(1)
        );

        await fake.Session.Run<int>(
            """
            TMPro.TMP_Text ksp = null;
            _Unity_TextMeshPro::TMPro.TMP_Text unity = null;
            return ksp == null && unity == null ? 1 : 0;
            """,
            cancellationToken: Cancel
        );

        var references = TypeReferenceScopes(fake.Game.Loaded.Values.Single(), "TMPro", "TMP_Text");
        Assert.Equal(["Assembly-CSharp", "Unity.TextMeshPro"], references.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Compiler_generated_types_have_no_name()
    {
        var dir = TestInstalls.TempDir("unspeakable");
        var path = Build(
            dir,
            "Iter",
            "namespace N { public class Foo { public System.Collections.Generic.IEnumerable<int> GetParts() { yield return 1; } } }"
        );
        var references = ReferenceSet.FromFiles([path]).BaseCompilation;

        Assert.Equal("global::N.Foo", GameTypeNames.FromGameName(references, "N.Foo"));
        Assert.NotNull(references.GetTypeByMetadataName("N.Foo+<GetParts>d__0"));
        Assert.Null(GameTypeNames.FromGameName(references, "N.Foo+<GetParts>d__0"));
        Assert.Null(GameTypeNames.FromGameName(references, "N.Foo+<GetParts>d__0[]"));
    }

    [Fact]
    public void Plugin_data_copies_are_not_references()
    {
        var instance = TestInstalls.TempDir("plugindata");
        Directory.CreateDirectory(Path.Combine(instance, "KSP_x64_Data", "Managed"));
        var stray = Path.Combine(instance, "GameData", "A", "PluginData");
        var loaded = Path.Combine(instance, "GameData", "Z", "Plugins");
        Directory.CreateDirectory(stray);
        Directory.CreateDirectory(loaded);
        Build(stray, "X", "namespace X { public class Old { } }");
        var plugin = Build(loaded, "X", "namespace X { public class New { } }");

        var references = ReferenceSet.FromInstance(instance);

        Assert.Equal(Path.GetFullPath(plugin), Assert.Single(references.Files).Path);
    }

    [Theory]
    [InlineData("KSP_x64_Data")]
    [InlineData("KSP_Data")]
    [InlineData("KSP.app/Contents/Resources/Data")]
    public void Game_assemblies_come_from_the_data_folder_of_any_layout(string dataDir)
    {
        var instance = TestInstalls.TempDir("layout");
        var managed = Path.Combine(instance, dataDir.Replace('/', Path.DirectorySeparatorChar), "Managed");
        Directory.CreateDirectory(managed);
        var game = Build(managed, "Assembly-CSharp", "public class Part { }");

        var references = ReferenceSet.FromInstance(instance);

        Assert.Equal(Path.GetFullPath(game), Assert.Single(references.Files).Path);
    }

    [Fact]
    public void A_folder_without_a_managed_folder_is_not_an_install()
    {
        var instance = TestInstalls.TempDir("no-managed");
        Directory.CreateDirectory(Path.Combine(instance, "GameData"));

        var error = Assert.Throws<DirectoryNotFoundException>(() => ReferenceSet.FromInstance(instance));
        Assert.Contains("KSP_Data/Managed", error.Message);
    }

    private static string Build(string dir, string name, string source)
    {
        var path = Path.Combine(dir, name + ".dll");
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return path;
    }

    private static string? ScopeOf(byte[] assembly, string ns, string name)
    {
        return Assert.Single(TypeReferenceScopes(assembly, ns, name));
    }

    private static List<string> TypeReferenceScopes(byte[] assembly, string ns, string name)
    {
        using var pe = new PEReader(new MemoryStream(assembly));
        var reader = pe.GetMetadataReader();
        var scopes = new List<string>();
        foreach (var handle in reader.TypeReferences)
        {
            var reference = reader.GetTypeReference(handle);
            if (
                reader.GetString(reference.Namespace) == ns
                && reader.GetString(reference.Name) == name
                && reference.ResolutionScope.Kind == HandleKind.AssemblyReference
            )
            {
                var scope = reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope);
                scopes.Add(reader.GetString(scope.Name));
            }
        }

        return scopes;
    }
}
