using System.Text.RegularExpressions;

namespace KspHarness;

/// <summary>
/// Every event the game sent on this session, in order. A cursor is a <c>seq</c> value: the events at or after it
/// are the ones that happened since it was taken.
/// </summary>
public sealed class GameLog
{
    private readonly object gate = new();
    private readonly List<GameEvent> events = [];
    private readonly List<(Func<GameEvent, bool> Match, TaskCompletionSource<GameEvent> Waiter)> waiters = [];
    private long nextSeq;

    /// <summary>The cursor for "now": events that arrive later have a seq at or after it.</summary>
    public long Cursor
    {
        get
        {
            lock (gate)
            {
                return nextSeq;
            }
        }
    }

    public IReadOnlyList<GameEvent> All
    {
        get
        {
            lock (gate)
            {
                return events.ToArray();
            }
        }
    }

    public IReadOnlyList<GameEvent> Since(long cursor)
    {
        lock (gate)
        {
            return events.Where(e => e.Seq >= cursor).ToArray();
        }
    }

    public IReadOnlyList<LogEvent> LogsSince(long cursor)
    {
        return Since(cursor).OfType<LogEvent>().ToArray();
    }

    /// <summary>Error, Exception and Assert log entries at or after the cursor, each with the mods in its stack.</summary>
    public IReadOnlyList<LogEvent> ErrorsSince(long cursor)
    {
        return LogsSince(cursor).Where(e => e.IsError).ToArray();
    }

    /// <summary>
    /// The popup dialogs seen at or after the cursor, one entry per dialog in the order they appeared: its latest
    /// event, so an answered dialog shows the button that closed it.
    /// </summary>
    public IReadOnlyList<DialogEvent> DialogsSince(long cursor)
    {
        var latest = new Dictionary<long, DialogEvent>();
        var order = new List<long>();
        foreach (var dialog in Since(cursor).OfType<DialogEvent>())
        {
            // An event without an id (from an older driver mod) cannot be paired, so it counts on its own.
            var key = dialog.Id != 0 ? dialog.Id : -dialog.Seq - 1;
            if (!latest.ContainsKey(key))
            {
                order.Add(key);
            }

            latest[key] = dialog;
        }

        return order.Select(key => latest[key]).ToArray();
    }

    /// <summary>Waits for an event at or after <paramref name="since"/> (default: now) that matches.</summary>
    public async Task<GameEvent> WaitForAsync(
        Func<GameEvent, bool> match,
        TimeSpan timeout,
        long? since = null,
        CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<GameEvent> waiter;
        lock (gate)
        {
            var cursor = since ?? nextSeq;
            var existing = events.FirstOrDefault(e => e.Seq >= cursor && match(e));
            if (existing is not null)
            {
                return existing;
            }

            waiter = new TaskCompletionSource<GameEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((e => e.Seq >= cursor && match(e), waiter));
        }

        try
        {
            return await waiter.Task.WaitAsync(timeout, cancellationToken);
        }
        finally
        {
            lock (gate)
            {
                waiters.RemoveAll(w => w.Waiter == waiter);
            }
        }
    }

    /// <summary>Waits for a log message that matches the regular expression.</summary>
    public async Task<LogEvent> WaitForMessageAsync(
        string pattern,
        TimeSpan timeout,
        long? since = null,
        CancellationToken cancellationToken = default)
    {
        var regex = new Regex(pattern);
        var found = await WaitForAsync(e => e is LogEvent log && regex.IsMatch(log.Message), timeout, since, cancellationToken);
        return (LogEvent)found;
    }

    internal void SetCursorFloor(long cursor)
    {
        lock (gate)
        {
            nextSeq = Math.Max(nextSeq, cursor);
        }
    }

    internal void Add(GameEvent gameEvent)
    {
        List<TaskCompletionSource<GameEvent>> matched = [];
        List<(TaskCompletionSource<GameEvent> Waiter, Exception Error)> failed = [];
        lock (gate)
        {
            events.Add(gameEvent);
            nextSeq = Math.Max(nextSeq, gameEvent.Seq + 1);
            foreach (var (match, waiter) in waiters)
            {
                // Predicates are test code running on the connection's reader thread: one that throws fails its own
                // wait, and must not reach the reader, which would close the session.
                try
                {
                    if (match(gameEvent))
                    {
                        matched.Add(waiter);
                    }
                }
                catch (Exception e)
                {
                    failed.Add((waiter, e));
                }
            }
        }

        foreach (var waiter in matched)
        {
            waiter.TrySetResult(gameEvent);
        }

        foreach (var (waiter, error) in failed)
        {
            waiter.TrySetException(error);
        }
    }
}
