using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace KspHarness.Tests;

public sealed class PolyfillTests
{
    private const string AttributeName = "IgnoresAccessChecksToAttribute";

    [Fact]
    public void A_reference_with_only_an_internal_declaration_still_gets_the_polyfill()
    {
        // As in Harmony 2, which declares the attribute for itself.
        var plugin = Reference(
            "namespace System.Runtime.CompilerServices { internal sealed class IgnoresAccessChecksToAttribute : System.Attribute { } }");

        var source = Snippets.Polyfills.SourceFor(Compilation(plugin));

        Assert.NotNull(source);
        Assert.Contains(AttributeName, source);
    }

    [Fact]
    public void A_public_declaration_in_a_reference_is_used()
    {
        var plugin = Reference(
            "namespace System.Runtime.CompilerServices { public sealed class IgnoresAccessChecksToAttribute : System.Attribute { } }");

        var source = Snippets.Polyfills.SourceFor(Compilation(plugin));

        Assert.DoesNotContain(AttributeName, source ?? "");
    }

    private static CSharpCompilation Compilation(params MetadataReference[] references)
    {
        return CSharpCompilation.Create("snippet", references: [CoreLibrary(), .. references]);
    }

    private static MetadataReference Reference(string source)
    {
        var compilation = CSharpCompilation.Create(
            "plugin",
            [CSharpSyntaxTree.ParseText(source)],
            [CoreLibrary()],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static MetadataReference CoreLibrary() => MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
}
