namespace KspHarness.Cli;

/// <summary>What one line of console input completed: nothing yet, a snippet to run, or a <c>:command</c>.</summary>
internal abstract record ReplStep
{
    public sealed record Snippet(string Code) : ReplStep;

    public sealed record Command(string Name, string Argument) : ReplStep;
}

/// <summary>
/// Collects console lines into snippets. A line starting with <c>:</c> on an empty buffer is a command. A bare
/// expression, or statements ending in a top-level <c>return</c> or <c>throw</c>, run as soon as they are complete;
/// other input runs at a blank line, a line <c>;;</c> or the end of input.
/// </summary>
internal sealed class ReplInput
{
    public const string Terminator = ";;";

    private readonly List<string> lines = [];

    /// <summary>Lines are waiting for more input; the console shows a continuation prompt.</summary>
    public bool IsContinuing => lines.Count > 0;

    public ReplStep? Feed(string line)
    {
        var trimmed = line.Trim();
        if (lines.Count == 0 && trimmed.StartsWith(':'))
        {
            var space = trimmed.IndexOfAny([' ', '\t']);
            return space < 0
                ? new ReplStep.Command(trimmed[1..].ToLowerInvariant(), "")
                : new ReplStep.Command(trimmed[1..space].ToLowerInvariant(), trimmed[(space + 1)..].Trim());
        }

        if (trimmed == Terminator || trimmed.Length == 0)
        {
            return lines.Count == 0 ? null : Submit();
        }

        lines.Add(line);
        var text = SnippetText.Parse(Buffer);
        return text.Shape == SnippetShape.Expression || text.EndsWithExit ? Submit() : null;
    }

    /// <summary>What is still buffered when input ends, as a snippet to run, or null.</summary>
    public ReplStep? End() => lines.Count == 0 ? null : Submit();

    /// <summary>Drops a partly typed snippet.</summary>
    public void Clear() => lines.Clear();

    private string Buffer => string.Join('\n', lines);

    private ReplStep.Snippet Submit()
    {
        var code = Buffer;
        lines.Clear();
        return new ReplStep.Snippet(code);
    }
}
