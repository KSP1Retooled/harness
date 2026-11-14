namespace KspHarness.Cli.Tests;

public sealed class SnippetTextTests
{
    [Theory]
    [InlineData("FlightGlobals.Vessels.Count")]
    [InlineData("  HighLogic.LoadedScene  ")]
    [InlineData("new { a = 1, b = \"x\" }")]
    [InlineData("Ui.Find(\"a/b\")\n    .name")]
    [InlineData("await Screenshot.Capture()")]
    [InlineData("1 + 2 // a comment")]
    public void Bare_expressions_are_returned(string code)
    {
        var text = SnippetText.Parse(code);

        Assert.Equal(SnippetShape.Expression, text.Shape);
        Assert.True(text.ReturnsValue);
    }

    [Theory]
    [InlineData("return 1;", true)]
    [InlineData("var v = FlightGlobals.ActiveVessel;\nreturn v.altitude;", true)]
    [InlineData("if (x) { return 2; }\nDebug.Log(\"a\");", true)]
    [InlineData("Debug.Log(\"a\");", false)]
    [InlineData("await Tick.FixedUpdate;", false)]
    [InlineData("Func<int> f = () => { return 1; };\nf();", false)]
    [InlineData("int F() { return 1; }\nF();", false)]
    [InlineData("return;", false)]
    public void Statements_return_a_value_only_when_their_own_code_does(string code, bool returnsValue)
    {
        var text = SnippetText.Parse(code);

        Assert.Equal(SnippetShape.Statements, text.Shape);
        Assert.Equal(returnsValue, text.ReturnsValue);
    }

    [Theory]
    [InlineData("if (a) {")]
    [InlineData("Ui.Find(")]
    [InlineData("var x = 1")]
    [InlineData("/* a comment that goes on")]
    [InlineData("var s = @\"two\nlines")]
    public void Input_cut_short_is_incomplete(string code)
    {
        Assert.Equal(SnippetShape.Incomplete, SnippetText.Parse(code).Shape);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("// only a comment")]
    [InlineData("using KSP.UI.Screens.Flight;")]
    public void Nothing_to_run_is_empty(string code)
    {
        Assert.Equal(SnippetShape.Empty, SnippetText.Parse(code).Shape);
    }

    [Fact]
    public void A_syntax_error_in_the_middle_is_complete_so_the_compiler_reports_it()
    {
        Assert.Equal(SnippetShape.Statements, SnippetText.Parse("var x = ;\nreturn 1;").Shape);
    }

    [Fact]
    public void Leading_usings_become_snippet_usings_and_keep_their_lines()
    {
        var text = SnippetText.Parse(
            "using KSP.UI.Screens.Flight;\n// note\nusing static System.Math;\nusing M = System.Math;\nreturn Abs(-1);"
        );

        Assert.Equal(["KSP.UI.Screens.Flight", "static System.Math", "M = System.Math"], text.Usings);
        Assert.Equal("\n// note\n\n\nreturn Abs(-1);", text.Body);
    }

    [Fact]
    public void Using_statements_are_code_not_directives()
    {
        var text = SnippetText.Parse("using var s = new System.IO.MemoryStream();\nreturn s.Length;");

        Assert.Empty(text.Usings);
        Assert.Equal(SnippetShape.Statements, text.Shape);
    }

    [Fact]
    public void A_returned_expression_keeps_its_lines_and_survives_a_trailing_comment()
    {
        var text = SnippetText.Parse("\n  x + 1 // why");

        Assert.Equal("\n  return x + 1 // why\n;", text.ReturningBody());
        Assert.Equal((2, 3), text.WrapPosition);
    }

    [Fact]
    public void Errors_after_the_inserted_return_move_back_to_the_input_column()
    {
        var text = SnippetText.Parse("  foo.bar");
        var after = new SnippetCompileError("<input>", 1, 10, "CS0103", "The name 'foo' does not exist");
        var otherLine = new SnippetCompileError("<input>", 2, 10, "CS1002", "; expected");

        Assert.Equal(3, text.Unwrap(after).Column);
        Assert.Equal(10, text.Unwrap(otherLine).Column);
    }

    [Theory]
    [InlineData("if (args.Length == 0) return \"none\";\nSystem.Console.WriteLine(args.Length);")]
    [InlineData("if (args.Length == 0) { return 2; }\nawait Task.Yield();")]
    [InlineData("foreach (var a in args) { if (a == null) return a; }")]
    [InlineData("if (args.Length == 0) return 1; else return 2;")]
    [InlineData("var n = args.Length;\nreturn n;")]
    [InlineData("return 1; // the end")]
    public void Bodies_that_return_a_value_compile_whatever_the_paths(string code)
    {
        var text = SnippetText.Parse(code);
        Assert.True(text.ReturnsValue);

        // The generated snippet method's shape: the body goes straight into an async Task<object> method.
        var source = $$"""
            using System.Threading.Tasks;
            public static class S
            {
                public static async Task<object> Run(object[] args)
                {
            {{text.ValueBody()}}
                }
            }
            """;
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "snippet",
            [
                Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                    source,
                    cancellationToken: TestContext.Current.CancellationToken
                ),
            ],
            [
                Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(Path.Combine(runtime, "System.Runtime.dll")),
            ],
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary
            )
        );

        var errors = compilation
            .GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void Value_bodies_keep_the_input_lines()
    {
        var text = SnippetText.Parse("if (x) return 1;\nDebug.Log(\"a\");");

        Assert.StartsWith("if (x) return 1;\nDebug.Log(\"a\");\n", text.ValueBody());
        Assert.Equal("var n = 1;\nreturn n;", SnippetText.Parse("var n = 1;\nreturn n;").ValueBody());
    }
}
