using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace KspHarness;

/// <summary>One compile error, at its position in the test's source file when the snippet came from there.</summary>
public sealed record SnippetCompileError(string File, int Line, int Column, string Id, string Message)
{
    public override string ToString() => $"{File}({Line},{Column}): error {Id}: {Message}";
}

/// <summary>A snippet did not compile. Nothing was sent to the game.</summary>
public sealed class SnippetCompileException : Exception
{
    internal SnippetCompileException(IReadOnlyList<SnippetCompileError> errors, string? generatedSource)
        : base(Describe(errors))
    {
        Errors = errors;
        GeneratedSource = generatedSource;
    }

    internal SnippetCompileException(string message, string? file, int line)
        : this([new SnippetCompileError(file ?? "snippet", line, 1, "KSPH0001", message)], null)
    {
    }

    public IReadOnlyList<SnippetCompileError> Errors { get; }

    /// <summary>The first error's file.</summary>
    public string File => Errors[0].File;

    /// <summary>The first error's line (1-based).</summary>
    public int Line => Errors[0].Line;

    /// <summary>The complete generated source, to see what was compiled.</summary>
    public string? GeneratedSource { get; }

    internal static SnippetCompileException From(
        ImmutableArray<Diagnostic> diagnostics,
        string generatedSource,
        Snippets.SourcePosition position)
    {
        var errors = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d =>
            {
                var span = d.Location.GetMappedLineSpan();
                if (!span.HasMappedPath && position.File is not null && d.Location.IsInSource)
                {
                    // An error in the generated part, outside the user's code: point at the call, where it came from.
                    return new SnippetCompileError(
                        position.File,
                        position.Line,
                        position.Column,
                        d.Id,
                        d.GetMessage() + $" (in generated code at {span.Path}:{span.StartLinePosition.Line + 1})");
                }

                return new SnippetCompileError(
                    span.Path,
                    span.StartLinePosition.Line + 1,
                    span.StartLinePosition.Character + 1,
                    d.Id,
                    d.GetMessage());
            })
            .ToArray();

        return new SnippetCompileException(errors, generatedSource);
    }

    private static string Describe(IReadOnlyList<SnippetCompileError> errors)
    {
        var shown = errors.Take(10).Select(e => e.ToString());
        var more = errors.Count > 10 ? $"{Environment.NewLine}... and {errors.Count - 10} more" : "";
        return "the snippet does not compile:" + Environment.NewLine + string.Join(Environment.NewLine, shown) + more;
    }
}
