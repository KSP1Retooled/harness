using System.Text.Json.Nodes;
using KspHarness.Installs;
using KspHarness.Library;
using KspHarness.Snippets;
using KspHarness.Tests.Infrastructure;

namespace KspHarness.Tests;

public sealed class PopupSnippetTests
{
    public static TheoryData<string, string> Snippets()
    {
        var data = new TheoryData<string, string>();
        foreach (var install in new[] { TestInstalls.Stock, TestInstalls.Retooled })
        {
            data.Add(install, nameof(PopupSnippets.Sweep));
            data.Add(install, nameof(PopupSnippets.LoadedAssemblies));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Snippets))]
    public void Popup_snippet_compiles_against_the_game(string install, string name)
    {
        var snippet = (LibrarySnippet)typeof(PopupSnippets).GetField(name)!.GetValue(null)!;
        var prepared = SnippetBuilder.FromString(snippet.Source, returnsValue: true, [], snippet.File, snippet.Line);

        var compiled = TestInstalls.Compiler(install).Compile(prepared.Source);

        Assert.NotEmpty(compiled.Assembly);
    }

    [Fact]
    public async Task A_broken_closer_is_reported_and_the_others_still_run()
    {
        var runs = 0;
        await using var fake = await FakeSession.StartAsync(
            TestInstalls.Stock,
            game =>
                game.OnRun = (_, _) =>
                    Interlocked.Increment(ref runs) switch
                    {
                        // The sweep, then the loaded-assembly query, then the closers that compile.
                        1 => Task.FromResult<JsonNode?>(new JsonArray()),
                        2 => Task.FromResult<JsonNode?>(new JsonArray("Assembly-CSharp")),
                        3 => throw new FakeError(
                            -32000,
                            "snippet threw",
                            new JsonObject
                            {
                                ["type"] = "System.NullReferenceException",
                                ["message"] = "no instance",
                                ["stack"] = "",
                            }
                        ),
                        _ => Task.FromResult<JsonNode?>(true),
                    }
        );

        var policy = PopupPolicy.Default with
        {
            Settle = TimeSpan.Zero,
            Closers =
            [
                new WindowCloser("Renamed", "Assembly-CSharp", "return NoSuchMod.Window.visible;"),
                new WindowCloser("Throws", "Assembly-CSharp", "return HighLogic.CurrentGame.Title == null;"),
                new WindowCloser("Works", "Assembly-CSharp", "return true;"),
            ],
        };

        var closed = await fake.Session.DismissDialogsAsync(
            policy,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(["Renamed", "Throws", "Works"], closed.Select(c => c.Name));
        Assert.StartsWith("could not run: ", closed[0].Detail);
        Assert.Contains("NoSuchMod", closed[0].Detail);
        Assert.Contains("no instance", closed[1].Detail);
        Assert.Equal("closed", closed[2].Detail);
    }

    public sealed class Mods : InstallDefinition
    {
        public override void Configure(InstallBuilder install) =>
            install
                .AnswerDialog("Incompatible*", "OK")
                .LeaveDialog("Keep me")
                .CloseWindowWith("**/MyWindow/ButtonClose");
    }

    [Fact]
    public void Definition_choices_come_before_the_defaults()
    {
        var policy = PopupPolicy.For(new Mods());

        Assert.Equal(new DialogAnswer("Incompatible*", "OK"), policy.Dialogs[0]);
        Assert.Equal(new DialogAnswer("Keep me", null), policy.Dialogs[1]);
        Assert.Equal(PopupPolicy.Default.Dialogs, policy.Dialogs.Skip(2));
        Assert.Equal("**/MyWindow/ButtonClose", policy.CloseButtons[0]);
        Assert.Equal(PopupPolicy.Default.Closers, policy.Closers);
    }
}
