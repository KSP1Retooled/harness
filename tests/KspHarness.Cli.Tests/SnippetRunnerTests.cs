namespace KspHarness.Cli.Tests;

public sealed class SnippetRunnerTests
{
    [Fact]
    public void Stacks_lose_the_frames_every_awaited_exception_passes_through()
    {
        const string stack = """
              at KspHarness.Script.Ui.ClickText (System.String text) [0x0004a] in <271ff>:0
              at KspHarness.Snippets.S_1+<<Run>g____body|0_0>d.MoveNext () [0x0000f] in <a09c>:0
            --- End of stack trace from previous location where exception was thrown ---
              at System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw () [0x0000c] in <9577>:0
              at System.Runtime.CompilerServices.TaskAwaiter.GetResult () [0x00000] in <9577>:0
              at KspHarness.Snippets.S_1+<Run>d__0.MoveNext () [0x00064] in <a09c>:0
            """;

        var frames = SnippetRunner.UsefulFrames(stack).Split(Environment.NewLine);

        Assert.Equal(3, frames.Length);
        Assert.Contains("Ui.ClickText", frames[0]);
        Assert.Contains("<Run>d__0", frames[2]);
    }

    [Fact]
    public void Results_print_as_indented_json_and_null()
    {
        Assert.Equal("null", SnippetRunner.Format(null));
        Assert.Equal("[\n  1,\n  \"é\"\n]", SnippetRunner.Format(System.Text.Json.Nodes.JsonNode.Parse("[1,\"é\"]")).ReplaceLineEndings("\n"));
    }
}
