using KspHarness.Snippets;
using KspHarness.Tests.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace KspHarness.Tests;

public sealed class SnippetSourceTests
{
    [Fact]
    public void Generated_code_declares_captures_before_the_mapped_user_code()
    {
        var source = new SnippetSource(
            SnippetForm.BodyWithReturn,
            "return target * 2;",
            [new Capture("target", "double", 1000.0)],
            ["KSP.UI.Screens.Flight"],
            new SourcePosition(@"C:\tests\RocketTests.cs", 42, 13));

        var generated = source.Generate();

        Assert.Contains("public static async Task<object> Run(object[] __args)", generated);
        Assert.Contains("using KSP.UI.Screens.Flight;", generated);
        Assert.Contains($"public static class S_{SnippetSource.HashPlaceholder}", generated);
        var declaration = generated.IndexOf("double target = Args.Get<double>(__args, 0);", StringComparison.Ordinal);
        var mapped = generated.IndexOf("#line 42 \"C:\\tests\\RocketTests.cs\"\nreturn target * 2;\n#line default", StringComparison.Ordinal);
        Assert.True(declaration >= 0 && mapped > declaration, generated);
    }

    [Fact]
    public void Body_without_return_is_wrapped_in_a_local_function()
    {
        var source = new SnippetSource(SnippetForm.BodyWithoutReturn, "Debug.Log(1);", [], [], SourcePosition.Unknown);

        var generated = source.Generate();

        Assert.Contains("async Task __body()", generated);
        Assert.Contains("await __body();\n            return null;", generated);
        Assert.DoesNotContain("#line", generated);
    }

    [Fact]
    public void Lambda_first_line_is_padded_to_its_column()
    {
        var source = new SnippetSource(SnippetForm.Lambda, "() => 1", [], [], new SourcePosition("T.cs", 5, 9));

        Assert.Contains("#line 5 \"T.cs\"\n        () => 1\n#line default", source.Generate());
    }

    [Fact]
    public void Dynamic_parameter_is_removed_keeping_lines()
    {
        var text = "async ksp =>\n{\n    var v = ksp.FlightGlobals.ActiveVessel;\n    return ksp.Math.Max(v.altitude, 0);\n}";

        var code = LambdaText.RemoveDynamicParameter(text, SourcePosition.Unknown);

        Assert.Equal("async () =>\n{\n    var v = FlightGlobals.ActiveVessel;\n    return Math.Max(v.altitude, 0);\n}", code);
    }

    [Fact]
    public void Parenthesized_dynamic_parameter_is_removed()
    {
        Assert.Equal("() => FlightGlobals.ready", LambdaText.RemoveDynamicParameter("(ksp) => ksp.FlightGlobals.ready", SourcePosition.Unknown));
        Assert.Equal("() => FlightGlobals.ready", LambdaText.RemoveDynamicParameter("(dynamic k) => k.FlightGlobals.ready", SourcePosition.Unknown));
    }

    [Fact]
    public void Used_names_skip_member_names()
    {
        var names = LambdaText.UsedNames("() => vessel.altitude + target");

        Assert.Contains("vessel", names);
        Assert.Contains("target", names);
        Assert.DoesNotContain("altitude", names);
    }

    [Fact]
    public void Source_locator_finds_the_exact_line_and_column()
    {
        var file = Path.Combine(TestInstalls.TempDir("locate"), "Sample.cs");
        File.WriteAllText(file, "class C\n{\n    void M()\n    {\n        session.Run(\n            ksp => ksp.X);\n    }\n}\n");

        var position = SourceLocator.Locate(file, 5, "ksp => ksp.X");

        Assert.Equal(new SourcePosition(file, 6, 13), position);
        Assert.Equal(new SourcePosition("missing.cs", 5, 1), SourceLocator.Locate("missing.cs", 5, "ksp => ksp.X"));
    }

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public void Compiled_snippets_are_cached_on_disk_by_hash(string install)
    {
        var cache = TestInstalls.TempDir("cache");
        var source = new SnippetSource(SnippetForm.BodyWithReturn, "return HighLogic.LoadedScene;", [], [], SourcePosition.Unknown);

        var first = TestInstalls.Compiler(install, cache).Compile(source);
        Assert.True(File.Exists(Path.Combine(cache, first.Hash + ".dll")));
        Assert.True(File.Exists(Path.Combine(cache, first.Hash + ".pdb")));

        // A new compiler finds the same bytes on disk instead of compiling again.
        var second = TestInstalls.Compiler(install, cache).Compile(source);
        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(first.Assembly, second.Assembly);
        Assert.Equal($"KspHarness.Snippets.S_{first.Hash[..16]}", first.TypeName);
    }

    [Fact]
    public void Cache_key_covers_the_support_sources_and_the_options()
    {
        var polyfill = CSharpSyntaxTree.ParseText("class P { }", path: "polyfills.g.cs", cancellationToken: TestContext.Current.CancellationToken);
        var changed = CSharpSyntaxTree.ParseText("class P { int x; }", path: "polyfills.g.cs", cancellationToken: TestContext.Current.CancellationToken);
        var options = SnippetCompiler.CreateOptions();

        var key = SnippetCompiler.Fingerprint([polyfill], options);

        Assert.Equal(key, SnippetCompiler.Fingerprint([polyfill], SnippetCompiler.CreateOptions()));
        Assert.NotEqual(key, SnippetCompiler.Fingerprint([changed], options));
        Assert.NotEqual(key, SnippetCompiler.Fingerprint([polyfill], options.WithAllowUnsafe(false)));
        Assert.NotEqual(key, SnippetCompiler.Fingerprint([polyfill], options.WithOptimizationLevel(OptimizationLevel.Release)));
    }

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public void Private_members_compile_only_with_access_checks_off(string install)
    {
        var compiler = TestInstalls.Compiler(install);
        var source = new SnippetSource(SnippetForm.BodyWithReturn, "return MainMenu.pName;", [], [], SourcePosition.Unknown);
        var tree = CSharpSyntaxTree.ParseText(
            source.Generate().Replace(SnippetSource.HashPlaceholder, "check"),
            new CSharpParseOptions(LanguageVersion.Latest),
            cancellationToken: TestContext.Current.CancellationToken);
        var checkedCompilation = CSharpCompilation.Create(
            "check",
            [tree],
            compiler.References.References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Default options do not even import the member (CS0117), so it is not public, and the snippet compiler
        // reaching it proves both the import option and the ignored accessibility.
        Assert.Contains(checkedCompilation.GetDiagnostics(TestContext.Current.CancellationToken), d => d.Id is "CS0117" or "CS0122");
        Assert.NotEmpty(compiler.Compile(source).Assembly);
    }

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public void Game_type_names_resolve_against_the_references(string install)
    {
        var references = TestInstalls.Compiler(install).References.BaseCompilation;

        Assert.Equal("global::Vessel", GameTypeNames.FromGameName(references, "Vessel"));
        Assert.Equal(
            "global::System.Collections.Generic.List<global::Part>",
            GameTypeNames.FromGameName(references, "System.Collections.Generic.List`1[[Part, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null]]"));
        Assert.Equal("global::Part[]", GameTypeNames.FromGameName(references, "Part[]"));
        Assert.Null(GameTypeNames.FromGameName(references, "No.Such.Type"));
        Assert.Equal("double[]", GameTypeNames.FromDriverType(references, typeof(double[])));
        Assert.Equal("global::KspHarness.Script.GameTime", GameTypeNames.FromDriverType(references, typeof(KspHarness.Script.GameTime)));
    }
}
