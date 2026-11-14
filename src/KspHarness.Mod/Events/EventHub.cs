using System.Collections.Generic;
using KspHarness.Mod.Protocol;
using KspHarness.Mod.Runtime;

namespace KspHarness.Mod.Events;

/// <summary>
/// Numbers events and sends them to the client. Log events are also kept so that a client that connects late, or
/// reconnects, receives the log from before it was there: the last <see cref="LogLines"/> plain lines, and
/// warnings, errors, asserts and exceptions up to <see cref="ProblemChars"/> characters of text. A modded boot logs
/// tens of thousands of plain lines, which would otherwise push out the loading errors that matter most.
/// </summary>
internal sealed class EventHub
{
    public const int LogLines = 5000;

    public const int ProblemChars = 8 * 1024 * 1024;

    // Counted on top of the message and stack, for the fields every event carries
    private const int EventOverheadChars = 200;

    private readonly Queue<Kept> logLines = new Queue<Kept>(LogLines);
    private readonly Queue<Kept> problems = new Queue<Kept>();
    private readonly object gate = new object();
    private int problemChars;
    private long nextSeq;
    private Connection live;

    public bool HasClient
    {
        get
        {
            lock (gate)
                return live != null && live.IsOpen;
        }
    }

    /// <summary>Sends an event with the common fields first; callable from any thread.</summary>
    public void Publish(string kind, JsonObject extra)
    {
        lock (gate)
        {
            long seq = nextSeq++;
            var parameters = new JsonObject
            {
                { "kind", kind },
                { "seq", seq },
                { "realTime", GameClock.RealSeconds },
                { "frame", GameClock.Frame },
                { "ut", GameClock.UniversalTime },
            };
            foreach (KeyValuePair<string, object> field in extra)
                parameters.Add(field.Key, field.Value);

            if (kind == "log")
                Keep(new Kept(parameters, seq, extra));

            if (live != null && live.IsOpen)
                live.SendNotification("event", parameters);
        }
    }

    /// <summary>
    /// Starts sending events to <paramref name="connection"/>: <paramref name="reply"/> runs with the next sequence
    /// number, then the kept log follows in order, and no live event can slip in between.
    /// </summary>
    public void GoLive(Connection connection, System.Action<long> reply)
    {
        lock (gate)
        {
            reply(nextSeq);

            using (Queue<Kept>.Enumerator line = logLines.GetEnumerator())
            using (Queue<Kept>.Enumerator problem = problems.GetEnumerator())
            {
                bool hasLine = line.MoveNext();
                bool hasProblem = problem.MoveNext();
                while (hasLine || hasProblem)
                {
                    if (hasLine && (!hasProblem || line.Current.Seq < problem.Current.Seq))
                    {
                        connection.SendNotification("event", line.Current.Event);
                        hasLine = line.MoveNext();
                    }
                    else
                    {
                        connection.SendNotification("event", problem.Current.Event);
                        hasProblem = problem.MoveNext();
                    }
                }
            }

            live = connection;
        }
    }

    public void Drop(Connection connection)
    {
        lock (gate)
        {
            if (live == connection)
                live = null;
        }
    }

    public Connection Live
    {
        get
        {
            lock (gate)
                return live;
        }
    }

    private void Keep(Kept logEvent)
    {
        if (!logEvent.IsProblem)
        {
            if (logLines.Count == LogLines)
                logLines.Dequeue();

            logLines.Enqueue(logEvent);
            return;
        }

        problems.Enqueue(logEvent);
        problemChars += logEvent.Chars;

        // The newest problem stays even when it alone is over the limit
        while (problemChars > ProblemChars && problems.Count > 1)
            problemChars -= problems.Dequeue().Chars;
    }

    private readonly struct Kept
    {
        public Kept(JsonObject logEvent, long seq, JsonObject fields)
        {
            Event = logEvent;
            Seq = seq;
            IsProblem = false;
            Chars = EventOverheadChars;
            foreach (KeyValuePair<string, object> field in fields)
            {
                if (field.Key == "level")
                    IsProblem = field.Value as string != "Log";
                else if (field.Key == "message" || field.Key == "stack")
                    Chars += (field.Value as string)?.Length ?? 0;
            }
        }

        public JsonObject Event { get; }

        public long Seq { get; }

        public bool IsProblem { get; }

        public int Chars { get; }
    }
}
