using System.Text.Json.Nodes;

namespace KspHarness.Tests;

public sealed class GameLogTests
{
    [Fact]
    public void Cursors_split_the_log_into_before_and_since()
    {
        var log = new GameLog();
        log.Add(Log(0, "Log", "booting"));
        var cursor = log.Cursor;
        log.Add(Log(1, "Warning", "careful"));
        log.Add(Log(2, "Assert", "assert", "ModA@1.0"));
        log.Add(Log(3, "Error", "error"));

        Assert.Equal(1, cursor);
        Assert.Equal(["careful", "assert", "error"], log.LogsSince(cursor).Select(e => e.Message));
        Assert.Equal(["assert", "error"], log.ErrorsSince(cursor).Select(e => e.Message));
        Assert.Equal(["ModA@1.0"], log.ErrorsSince(cursor)[0].Mods);
        Assert.Equal(4, log.Cursor);
    }

    [Fact]
    public async Task Waiting_finds_a_later_message()
    {
        var log = new GameLog();
        log.Add(Log(0, "Log", "old match"));

        var waiting = log.WaitForMessageAsync(
            "match",
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.False(waiting.IsCompleted);

        log.Add(Log(1, "Log", "new match"));

        Assert.Equal("new match", (await waiting).Message);
    }

    [Fact]
    public async Task Waiting_times_out()
    {
        var log = new GameLog();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            log.WaitForMessageAsync(
                "never",
                TimeSpan.FromMilliseconds(50),
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
    }

    private static GameEvent Log(long seq, string level, string message, params string[] mods)
    {
        return GameEventParser.Parse(
            new JsonObject
            {
                ["kind"] = "log",
                ["seq"] = seq,
                ["realTime"] = 0.5,
                ["frame"] = 1,
                ["ut"] = null,
                ["level"] = level,
                ["message"] = message,
                ["stack"] = "",
                ["mods"] = new JsonArray(mods.Select(m => (JsonNode)m).ToArray()),
            }
        );
    }
}
