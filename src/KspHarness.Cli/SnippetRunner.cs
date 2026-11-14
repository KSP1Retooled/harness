using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using KspHarness.Values;

namespace KspHarness.Cli;

/// <summary>Compile errors with positions in the typed or file input, after undoing the CLI's own wrapping.</summary>
internal sealed class InputCompileException(IReadOnlyList<SnippetCompileError> errors)
    : Exception(SnippetRunner.Describe(errors))
{
    public IReadOnlyList<SnippetCompileError> Errors { get; } = errors;
}

/// <summary>Runs typed or file input as a source-string snippet and returns the game's encoded result.</summary>
internal static class SnippetRunner
{
    /// <summary>The name compile errors carry for typed input; no file of that name can exist, so lines stay 1-based.</summary>
    public const string InputName = "<input>";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Runs <paramref name="code"/>: a bare expression is returned, a body with a <c>return</c> gives its value, any
    /// other body gives null. Compile errors are reported against <paramref name="file"/> (or <see cref="InputName"/>)
    /// with the input's own lines and columns.
    /// </summary>
    public static async Task<JsonNode?> RunAsync(
        GameSession game,
        string code,
        string? file = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var text = SnippetText.Parse(code);
        if (text.Shape == SnippetShape.Empty)
        {
            return null;
        }

        var options = new RunOptions
        {
            Usings = text.Usings,
            RealTimeout = timeout ?? RunOptions.Default.RealTimeout,
        };

        file ??= InputName;
        if (text.Shape == SnippetShape.Expression)
        {
            try
            {
                var value = await game.Run<GameValue>(text.ReturningBody(), options, cancellationToken, file, 1);
                return value.Json;
            }
            catch (SnippetCompileException e) when (IsVoidResult(e))
            {
                // A call or await with no value, such as Debug.Log("x") or await Tick.Update: run it as a statement.
                await game.Run(text.Body + "\n;", options, cancellationToken, file, 1);
                return null;
            }
            catch (SnippetCompileException e)
            {
                throw new InputCompileException(e.Errors.Select(text.Unwrap).ToList());
            }
        }

        if (text.ReturnsValue)
        {
            var value = await game.Run<GameValue>(text.ValueBody(), options, cancellationToken, file, 1);
            return value.Json;
        }

        await game.Run(text.Body, options, cancellationToken, file, 1);
        return null;
    }

    public static string Format(JsonNode? value)
    {
        return value is null ? "null" : value.ToJsonString(JsonOptions);
    }

    /// <summary>Compile errors as <c>file(line,col): error id: message</c>, one per line.</summary>
    public static string Describe(IEnumerable<SnippetCompileError> errors)
    {
        return string.Join(Environment.NewLine, errors.Select(e => e.ToString()));
    }

    /// <summary>An exception from the game or the connection, as a short text for a person or an agent.</summary>
    public static string Describe(Exception error)
    {
        return error switch
        {
            SnippetCompileException compile => Describe(compile.Errors),
            InputCompileException compile => Describe(compile.Errors),
            SnippetException thrown => $"{thrown.GameType}: {thrown.GameMessage}{Environment.NewLine}{UsefulFrames(thrown.GameStackTrace)}".TrimEnd(),
            _ => $"{error.GetType().Name}: {error.Message}",
        };
    }

    /// <summary>The stack without the frames every awaited exception passes through on its way out.</summary>
    internal static string UsefulFrames(string stack)
    {
        string[] noise =
        [
            "System.Runtime.ExceptionServices.ExceptionDispatchInfo",
            "System.Runtime.CompilerServices.TaskAwaiter",
            "--- End of stack trace from previous location",
        ];

        var lines = stack.ReplaceLineEndings("\n").Split('\n')
            .Where(line => line.Trim().Length > 0 && !noise.Any(n => line.Contains(n, StringComparison.Ordinal)));
        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsVoidResult(SnippetCompileException error)
    {
        return error.Errors.All(e => e.Message.Contains("'void'", StringComparison.Ordinal));
    }
}
