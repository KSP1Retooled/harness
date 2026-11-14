namespace KspHarness.Cli.Tests;

public sealed class ReplInputTests
{
    [Fact]
    public void A_bare_expression_runs_at_once()
    {
        var input = new ReplInput();

        var step = input.Feed("FlightGlobals.Vessels.Count");

        Assert.Equal(new ReplStep.Snippet("FlightGlobals.Vessels.Count"), step);
        Assert.False(input.IsContinuing);
    }

    [Fact]
    public void Statements_ending_in_return_run_once_complete()
    {
        var input = new ReplInput();

        Assert.Null(input.Feed("var v = FlightGlobals.ActiveVessel;"));
        Assert.True(input.IsContinuing);
        var step = input.Feed("return v.altitude;");

        Assert.Equal(new ReplStep.Snippet("var v = FlightGlobals.ActiveVessel;\nreturn v.altitude;"), step);
    }

    [Fact]
    public void Statements_without_return_wait_for_a_blank_line()
    {
        var input = new ReplInput();

        Assert.Null(input.Feed("Debug.Log(\"a\");"));
        Assert.Null(input.Feed("await Tick.Update;"));
        var step = input.Feed("");

        Assert.Equal(new ReplStep.Snippet("Debug.Log(\"a\");\nawait Tick.Update;"), step);
    }

    [Fact]
    public void Double_semicolon_ends_input()
    {
        var input = new ReplInput();

        Assert.Null(input.Feed("if (true)"));
        Assert.Null(input.Feed("{"));
        Assert.Null(input.Feed("    Debug.Log(\"a\");"));
        Assert.Null(input.Feed("}"));
        var step = input.Feed(";;");

        Assert.Equal(new ReplStep.Snippet("if (true)\n{\n    Debug.Log(\"a\");\n}"), step);
    }

    [Fact]
    public void A_nested_return_does_not_end_input()
    {
        var input = new ReplInput();

        Assert.Null(input.Feed("if (FlightGlobals.ActiveVessel == null) return 0;"));
        var step = input.Feed("return FlightGlobals.ActiveVessel.altitude;");

        Assert.IsType<ReplStep.Snippet>(step);
    }

    [Fact]
    public void An_expression_split_over_lines_runs_when_it_closes()
    {
        var input = new ReplInput();

        Assert.Null(input.Feed("Ui.Find("));
        var step = input.Feed("    \"Canvas/**/Button\")");

        Assert.Equal(new ReplStep.Snippet("Ui.Find(\n    \"Canvas/**/Button\")"), step);
    }

    [Fact]
    public void Blank_lines_on_an_empty_buffer_do_nothing()
    {
        var input = new ReplInput();

        Assert.Null(input.Feed(""));
        Assert.Null(input.Feed(";;"));
        Assert.False(input.IsContinuing);
    }

    [Theory]
    [InlineData(":help", "help", "")]
    [InlineData("  :Screenshot  shots/a.png ", "screenshot", "shots/a.png")]
    [InlineData(":click Start Game", "click", "Start Game")]
    [InlineData(":db PART[mk1pod_v2]/MODULE[ModuleCommand]", "db", "PART[mk1pod_v2]/MODULE[ModuleCommand]")]
    [InlineData(":log 50", "log", "50")]
    public void Colon_lines_are_commands(string line, string name, string argument)
    {
        Assert.Equal(new ReplStep.Command(name, argument), new ReplInput().Feed(line));
    }

    [Fact]
    public void A_colon_line_inside_a_snippet_is_code()
    {
        var input = new ReplInput();

        Assert.Null(input.Feed("var x = flag"));
        Assert.Null(input.Feed("  ? 1"));
        var step = input.Feed("  : 2;");

        Assert.Null(step);
        Assert.Equal(new ReplStep.Snippet("var x = flag\n  ? 1\n  : 2;"), input.Feed(""));
    }

    [Fact]
    public void A_throw_ends_input_like_a_return()
    {
        Assert.Equal(new ReplStep.Snippet("throw new Exception(\"x\");"), new ReplInput().Feed("throw new Exception(\"x\");"));
    }

    [Fact]
    public void The_end_of_input_runs_what_is_buffered()
    {
        var input = new ReplInput();
        input.Feed("Debug.Log(\"a\");");

        Assert.Equal(new ReplStep.Snippet("Debug.Log(\"a\");"), input.End());
        Assert.Null(input.End());
    }

    [Fact]
    public void Clear_drops_a_partial_snippet()
    {
        var input = new ReplInput();
        input.Feed("if (a) {");

        input.Clear();

        Assert.False(input.IsContinuing);
        Assert.Equal(new ReplStep.Command("quit", ""), input.Feed(":quit"));
    }
}
