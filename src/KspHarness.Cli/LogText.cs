using System.Text.Json.Nodes;

namespace KspHarness.Cli;

/// <summary>Log events as text lines and as JSON for the console and the MCP bridge.</summary>
internal static class LogText
{
    public static string Line(LogEvent entry)
    {
        var mods = entry.Mods.Count > 0 ? $" [{string.Join(", ", entry.Mods)}]" : "";
        return $"#{entry.Seq} {entry.Level}: {entry.Message}{mods}";
    }

    public static string Indent(string text)
    {
        var lines = text.ReplaceLineEndings("\n").TrimEnd().Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line => "    " + line));
    }

    public static JsonObject Json(LogEvent entry, bool withStack)
    {
        var json = new JsonObject
        {
            ["seq"] = entry.Seq,
            ["level"] = entry.Level.ToString(),
            ["message"] = entry.Message,
            ["frame"] = entry.Frame,
            ["realTime"] = Math.Round(entry.RealTime, 3),
        };

        if (entry.Mods.Count > 0)
        {
            json["mods"] = new JsonArray(entry.Mods.Select(m => (JsonNode)m).ToArray());
        }

        if (withStack && entry.Stack.Length > 0)
        {
            json["stack"] = entry.Stack;
        }

        return json;
    }

    /// <summary>
    /// One page of the <c>logs</c> tool: without <paramref name="since"/> the latest <paramref name="limit"/> matching
    /// entries, with it the oldest from there. <paramref name="cursorBefore"/> must be read before
    /// <paramref name="snapshot"/> is taken, because the reader thread keeps adding entries: a cursor read afterwards
    /// could pass entries that arrived in between, which would then be in neither this page nor the next.
    /// </summary>
    public static (List<LogEvent> Page, long Next) Page(
        long cursorBefore,
        IReadOnlyList<LogEvent> snapshot,
        long? since,
        IReadOnlyCollection<string> levels,
        string? mod,
        int limit)
    {
        limit = Math.Max(limit, 1);
        var matching = snapshot.Where(e => Matches(e, levels, mod)).ToList();
        if (since is not null && matching.Count > limit)
        {
            var page = matching.Take(limit).ToList();
            return (page, page[^1].Seq + 1);
        }

        // Entries that arrived during the read are in the snapshot, so the next page starts after them.
        var next = snapshot.Count == 0 ? cursorBefore : Math.Max(cursorBefore, snapshot[^1].Seq + 1);
        return (matching.TakeLast(limit).ToList(), next);
    }

    /// <summary>
    /// Whether an entry passes the filters: <paramref name="levels"/> by name (<c>Error</c>, or <c>errors</c> for
    /// Error, Exception and Assert), <paramref name="mod"/> as a case-insensitive part of a mod tag in its stack.
    /// </summary>
    public static bool Matches(LogEvent entry, IReadOnlyCollection<string> levels, string? mod)
    {
        if (levels.Count > 0)
        {
            var levelMatches = levels.Any(level =>
                level.Equals(entry.Level.ToString(), StringComparison.OrdinalIgnoreCase)
                || (level.Equals("errors", StringComparison.OrdinalIgnoreCase) && entry.IsError));
            if (!levelMatches)
            {
                return false;
            }
        }

        return string.IsNullOrEmpty(mod) || entry.Mods.Any(tag => tag.Contains(mod, StringComparison.OrdinalIgnoreCase));
    }
}
