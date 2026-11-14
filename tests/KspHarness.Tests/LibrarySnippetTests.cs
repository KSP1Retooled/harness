using System.Text.Json.Nodes;
using KspHarness.Library;
using KspHarness.Snippets;
using KspHarness.Tests.Infrastructure;

namespace KspHarness.Tests;

public sealed class LibrarySnippetTests
{
    public static TheoryData<string, string, bool> Snippets()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (var install in new[] { TestInstalls.Stock, TestInstalls.Retooled })
        {
            foreach (
                var name in new[]
                {
                    "WaitForMainMenu",
                    "StartSandbox",
                    "Launch",
                    "LoadSave",
                    "QuickSave",
                    "ReturnToMainMenu",
                }
            )
            {
                data.Add(install, name, false);
            }

            data.Add(install, "DatabaseQuery", true);
            data.Add(install, "DatabaseValues", true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Snippets))]
    public void Library_snippet_compiles_against_the_game(string install, string name, bool returnsValue)
    {
        var snippet = (LibrarySnippet)typeof(LibrarySnippets).GetField(name)!.GetValue(null)!;
        var prepared = SnippetBuilder.FromString(snippet.Source, returnsValue, [], snippet.File, snippet.Line);

        var compiled = TestInstalls.Compiler(install).Compile(prepared.Source);

        Assert.NotEmpty(compiled.Assembly);
    }

    [Fact]
    public async Task Database_queries_share_one_assembly()
    {
        var queries = new List<string>();
        await using var fake = await FakeSession.StartAsync(
            TestInstalls.Stock,
            game =>
                game.OnRun = (run, _) =>
                {
                    lock (queries)
                    {
                        queries.Add(run["args"]![0]!.GetValue<string>());
                    }

                    return Task.FromResult<JsonNode?>(new JsonArray("4"));
                }
        );

        var cancel = TestContext.Current.CancellationToken;
        Assert.Equal([4], await fake.Session.Database.Values<int>("PART[a]/mass", cancel));
        Assert.Equal([4], await fake.Session.Database.Values<int>("PART[b]/mass", cancel));

        Assert.Equal(["PART[a]/mass", "PART[b]/mass"], queries);
        Assert.Single(fake.Game.Loaded);
    }
}
