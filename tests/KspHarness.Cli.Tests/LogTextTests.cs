using System.Text.Json.Nodes;

namespace KspHarness.Cli.Tests;

public sealed class LogTextTests
{
    [Fact]
    public void Entries_that_arrive_between_the_cursor_and_the_snapshot_are_not_skipped()
    {
        // The cursor read 10; entries 10 and 11 arrived before the snapshot was taken.
        var (page, next) = LogText.Page(10, [Entry(8), Entry(9), Entry(10), Entry(11)], since: null, [], null, limit: 100);

        Assert.Equal([8L, 9L, 10L, 11L], page.Select(e => e.Seq));
        Assert.Equal(12, next);
    }

    [Fact]
    public void An_empty_snapshot_continues_from_the_cursor_read_before_it()
    {
        var (page, next) = LogText.Page(10, [], since: 7, [], null, limit: 100);

        Assert.Empty(page);
        Assert.Equal(10, next);
    }

    [Fact]
    public void Filtered_out_entries_still_move_the_cursor_past_the_snapshot()
    {
        var (page, next) = LogText.Page(5, [Entry(5, LogLevel.Error), Entry(6)], since: 5, ["errors"], null, limit: 100);

        Assert.Equal(5, Assert.Single(page).Seq);
        Assert.Equal(7, next);
    }

    [Fact]
    public void A_full_page_after_a_cursor_continues_after_its_last_entry()
    {
        var (page, next) = LogText.Page(20, [Entry(3), Entry(4), Entry(5)], since: 3, [], null, limit: 2);

        Assert.Equal([3L, 4L], page.Select(e => e.Seq));
        Assert.Equal(5, next);
    }

    [Fact]
    public void Without_a_cursor_the_latest_entries_are_returned()
    {
        var (page, next) = LogText.Page(6, [Entry(3), Entry(4), Entry(5)], since: null, [], null, limit: 2);

        Assert.Equal([4L, 5L], page.Select(e => e.Seq));
        Assert.Equal(6, next);
    }

    private static LogEvent Entry(long seq, LogLevel level = LogLevel.Log)
    {
        return new LogEvent(seq, 0, 0, null, new JsonObject(), level, $"entry {seq}", "", []);
    }
}
