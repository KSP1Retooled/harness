namespace KspHarness.Cli.Tests;

/// <summary>The MCP server without a game: the handshake, the tool list, and errors an agent can act on.</summary>
public sealed class McpServerTests
{
    [Fact]
    public async Task Lists_the_harness_tools()
    {
        await using var client = await McpClient.StartAsync();

        var result = await client.RequestAsync("tools/list");
        var names = result["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Order().ToArray();

        Assert.Equal(
            ["db_query", "db_values", "dialog_click", "dialogs_list", "game_attach", "game_launch", "game_quit",
                "game_status", "instance_build", "logs", "run_csharp", "screenshot", "ui_click", "ui_describe"],
            names);
        var runCsharp = result["tools"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "run_csharp")!;
        Assert.Contains("await Tick.FixedUpdate", runCsharp["description"]!.GetValue<string>());
        Assert.Equal("string", runCsharp["inputSchema"]!["properties"]!["code"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tools_that_need_a_game_say_how_to_get_one()
    {
        await using var client = await McpClient.StartAsync();

        var status = await client.CallAsync("game_status");
        var run = await client.CallAsync("run_csharp", new() { ["code"] = "1 + 1" });

        Assert.Contains("\"attached\": false", McpClient.TextOf(status));
        Assert.True(run["isError"]!.GetValue<bool>());
        Assert.Contains("game_launch", McpClient.TextOf(run));
    }

    [Fact]
    public async Task Attaching_without_a_launched_game_is_an_error_not_a_crash()
    {
        var folder = Directory.CreateTempSubdirectory("ksph-tests-").FullName;
        try
        {
            await using var client = await McpClient.StartAsync();

            var attach = await client.CallAsync("game_attach", new() { ["instance"] = folder });

            Assert.True(attach["isError"]!.GetValue<bool>());
            Assert.Contains("ksph launch", McpClient.TextOf(attach));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
