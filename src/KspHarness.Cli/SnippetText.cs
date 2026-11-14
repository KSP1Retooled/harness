using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace KspHarness.Cli;

internal enum SnippetShape
{
    /// <summary>Nothing but whitespace and usings.</summary>
    Empty,

    /// <summary>Open brackets or a statement cut short at the end: more lines are coming.</summary>
    Incomplete,

    /// <summary>One expression with no <c>;</c>, which is returned.</summary>
    Expression,

    /// <summary>Statements, or text that does not parse (the compiler then reports why).</summary>
    Statements,
}

/// <summary>
/// Typed or file input turned into a snippet body: leading <c>using</c> lines become the snippet's usings, and a bare
/// expression becomes <c>return expr;</c>. Line numbers stay those of the input, so compile errors map back to it.
/// </summary>
internal sealed partial class SnippetText
{
    private const string ReturnPrefix = "return ";

    private SnippetText(string input, string body, IReadOnlyList<string> usings, SnippetShape shape)
    {
        Input = input;
        Body = body;
        Usings = usings;
        Shape = shape;
    }

    public string Input { get; }

    /// <summary>The input with its usings blanked out (lines kept), before any wrapping.</summary>
    public string Body { get; }

    public IReadOnlyList<string> Usings { get; }

    public SnippetShape Shape { get; }

    /// <summary>A <c>return</c> with a value somewhere in the body's own code (not in a lambda or local function).</summary>
    public bool ReturnsValue => Shape == SnippetShape.Expression || HasReturn(Body);

    /// <summary>
    /// The last top-level statement is a <c>return</c> or <c>throw</c>, so typed input can run without a blank line.
    /// </summary>
    public bool EndsWithExit =>
        Shape == SnippetShape.Statements
        && Block(Body).Statements.LastOrDefault() is ReturnStatementSyntax or ThrowStatementSyntax;

    /// <summary>For an expression, where <c>return </c> goes: 1-based line and column of its first character.</summary>
    public (int Line, int Column) WrapPosition
    {
        get
        {
            var index = FirstCodeIndex(Body);
            var before = Body[..index];
            var line = before.Count(c => c == '\n') + 1;
            var column = index - (before.LastIndexOf('\n') + 1) + 1;
            return (line, column);
        }
    }

    public static SnippetText Parse(string input)
    {
        input = input.ReplaceLineEndings("\n");
        var (body, usings) = TakeUsings(input);
        return new SnippetText(input, body, usings, Classify(body));
    }

    /// <summary>
    /// Statements that return a value on some paths, made to return on all of them: a body such as
    /// <c>if (v == null) return "none"; v.Stage();</c> would otherwise fail with CS0161 in the generated method. The
    /// added <c>return null;</c> follows the input's last line, so its line numbers do not move.
    /// </summary>
    public string ValueBody()
    {
        return EndsWithExit ? Body : Body + "\nreturn null;";
    }

    /// <summary>The body with <c>return </c> before the expression, and the <c>;</c> on a line of its own.</summary>
    public string ReturningBody()
    {
        var index = FirstCodeIndex(Body);

        // On its own line, the semicolon survives a trailing // comment.
        return Body[..index] + ReturnPrefix + Body[index..].TrimEnd() + "\n;";
    }

    /// <summary>Moves a compile error's column back over the inserted <c>return </c>.</summary>
    public SnippetCompileError Unwrap(SnippetCompileError error)
    {
        var (line, column) = WrapPosition;
        return error.Line == line && error.Column >= column + ReturnPrefix.Length
            ? error with
            {
                Column = error.Column - ReturnPrefix.Length,
            }
            : error;
    }

    internal static SnippetShape Classify(string body)
    {
        if (!SyntaxFactory.ParseExpression(body.Trim(), options: ParseOptions).ContainsDiagnostics)
        {
            return SnippetShape.Expression;
        }

        // The block's closing brace follows the body, so an error there means the body stopped mid-way (an open
        // bracket, a missing semicolon, an unterminated comment that swallowed the brace).
        var text = "{" + body + "\n}";
        var block = SyntaxFactory.ParseStatement(text, options: ParseOptions);
        var end = text.Length - 2;
        if (
            block
                .GetDiagnostics()
                .Any(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceSpan.Start >= end)
        )
        {
            return SnippetShape.Incomplete;
        }

        return string.IsNullOrWhiteSpace(StripComments(body)) ? SnippetShape.Empty : SnippetShape.Statements;
    }

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, kind: SourceCodeKind.Regular);

    private static BlockSyntax Block(string body)
    {
        return SyntaxFactory.ParseStatement("{" + body + "\n}", options: ParseOptions) as BlockSyntax
            ?? SyntaxFactory.Block();
    }

    private static bool HasReturn(string body)
    {
        return Block(body)
            .DescendantNodes(node => node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .OfType<ReturnStatementSyntax>()
            .Any(r => r.Expression is not null);
    }

    private static int FirstCodeIndex(string body)
    {
        var index = 0;
        while (index < body.Length && char.IsWhiteSpace(body[index]))
        {
            index++;
        }

        return index;
    }

    /// <summary>Leading <c>using X;</c> lines (not <c>using var</c> or <c>using (...)</c>), each replaced by a blank line.</summary>
    private static (string Body, List<string> Usings) TakeUsings(string input)
    {
        var lines = input.Split('\n');
        var usings = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var match = UsingDirective().Match(line);
            if (!match.Success)
            {
                break;
            }

            usings.Add(match.Groups["name"].Value.Trim());
            lines[i] = "";
        }

        return (string.Join('\n', lines), usings);
    }

    private static string StripComments(string body)
    {
        var tokens = SyntaxFactory.ParseTokens(body, options: ParseOptions);
        return string.Concat(tokens.Select(t => t.Text));
    }

    [GeneratedRegex(@"^using\s+(?!var\b)(?<name>(static\s+)?[A-Za-z_][\w.]*(\s*=\s*[\w.<>,\s]+)?)\s*;$")]
    private static partial Regex UsingDirective();
}
