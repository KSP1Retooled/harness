using System.Collections.Concurrent;

namespace KspHarness.Snippets;

/// <summary>
/// Finds where a snippet's text sits in the caller's source file. <c>[CallerLineNumber]</c> gives the line of the
/// call, while the code often starts lines later; reading the file finds the exact line and column.
/// </summary>
internal static class SourceLocator
{
    // Calls split over lines report a line at or a little before the argument; this many lines back covers chains
    // such as `await session\n    .Run(...)`.
    private const int LinesBefore = 3;

    private static readonly ConcurrentDictionary<string, (DateTime Modified, string Text)> Files = new();

    /// <summary>The position of <paramref name="text"/> nearest after the call, or the call's line if not found.</summary>
    public static SourcePosition Locate(string? file, int callerLine, string text)
    {
        if (string.IsNullOrEmpty(file))
        {
            return new SourcePosition(null, Math.Max(callerLine, 1), 1);
        }

        var fallback = new SourcePosition(file, Math.Max(callerLine, 1), 1);
        var content = Read(file);
        var needle = text.ReplaceLineEndings("\n");
        if (content is null || needle.Length == 0)
        {
            return fallback;
        }

        var lineStarts = LineStarts(content);
        var searchFrom = lineStarts[Math.Clamp(callerLine - 1 - LinesBefore, 0, lineStarts.Count - 1)];
        var index = content.IndexOf(needle, searchFrom, StringComparison.Ordinal);
        if (index < 0)
        {
            return fallback;
        }

        var line = LineOf(lineStarts, index);
        return new SourcePosition(file, line + 1, index - lineStarts[line] + 1);
    }

    /// <summary>
    /// The position of a source string's first non-blank line, which is where the code in a string literal starts
    /// whatever the literal's syntax (regular, verbatim or raw).
    /// </summary>
    public static SourcePosition LocateCode(string? file, int callerLine, string code)
    {
        var lines = code.ReplaceLineEndings("\n").Split('\n');
        var firstIndex = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (firstIndex < 0)
        {
            return new SourcePosition(file, Math.Max(callerLine, 1), 1);
        }

        var found = Locate(file, callerLine, lines[firstIndex].Trim());

        // The code's own first line comes before its first non-blank line by this many lines.
        return found with
        {
            Line = Math.Max(found.Line - firstIndex, 1),
            Column = 1,
        };
    }

    private static string? Read(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                return null;
            }

            var modified = info.LastWriteTimeUtc;
            if (Files.TryGetValue(file, out var cached) && cached.Modified == modified)
            {
                return cached.Text;
            }

            var text = File.ReadAllText(file).ReplaceLineEndings("\n");
            Files[file] = (modified, text);
            return text;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<int> LineStarts(string content)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }

    private static int LineOf(List<int> lineStarts, int index)
    {
        var found = lineStarts.BinarySearch(index);
        return found >= 0 ? found : ~found - 1;
    }
}
