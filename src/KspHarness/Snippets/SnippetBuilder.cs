using KspHarness.Values;
using Microsoft.CodeAnalysis.CSharp;

namespace KspHarness.Snippets;

/// <summary>A snippet ready to compile, and the argument values for its captured variables.</summary>
internal sealed record PreparedSnippet(SnippetSource Source, IReadOnlyList<object?> Arguments);

/// <summary>Turns what a test passed to <c>Run</c> into a <see cref="SnippetSource"/>.</summary>
internal static class SnippetBuilder
{
    public static PreparedSnippet FromString(
        string code,
        bool returnsValue,
        IReadOnlyList<string> usings,
        string? file,
        int line
    )
    {
        var position = SourceLocator.LocateCode(file, line, code);
        var form = returnsValue ? SnippetForm.BodyWithReturn : SnippetForm.BodyWithoutReturn;
        return new PreparedSnippet(new SnippetSource(form, code, [], usings, position), []);
    }

    public static PreparedSnippet FromLambda(
        Delegate lambda,
        string? text,
        bool dynamicParameter,
        IReadOnlyList<string> usings,
        string? file,
        int line,
        CSharpCompilation references
    )
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new SnippetCompileException(
                "the lambda's source text is missing; call Run with a lambda written at the call",
                file,
                line
            );
        }

        var position = SourceLocator.Locate(file, line, text);
        var code = dynamicParameter
            ? LambdaText.RemoveDynamicParameter(text, position)
            : LambdaText.ParameterlessLambda(text, position);

        var captures = new List<Capture>();
        var arguments = new List<object?>();
        foreach (var variable in ClosureReader.Read(lambda, code, position))
        {
            captures.Add(new Capture(variable.Name, DeclaredType(variable, references, position), variable.Value));
            arguments.Add(variable.Value);
        }

        return new PreparedSnippet(new SnippetSource(SnippetForm.Lambda, code, captures, usings, position), arguments);
    }

    private static string DeclaredType(CapturedVariable variable, CSharpCompilation references, SourcePosition position)
    {
        if (variable.Value is Handle handle)
        {
            // In the game the handle is the object itself, so the local gets the object's type.
            return GameTypeNames.FromGameName(references, handle.Type, handle.Assembly) ?? "object";
        }

        var type = variable.Type;
        if (type == typeof(object))
        {
            // A dynamic or object variable: what it holds now decides what the game gets.
            if (variable.Value is null)
            {
                return "object";
            }

            type = variable.Value.GetType();
        }

        if (!ValueEncoder.IsEncodable(type))
        {
            throw new SnippetCompileException(
                $"the lambda captures '{variable.Name}', a {type}, which can't be sent to the game. Capture numbers, "
                    + "strings, enums, vectors, arrays, tuples, GameTime or handles, and build other objects in the lambda.",
                position.File,
                position.Line
            );
        }

        return GameTypeNames.FromDriverType(references, type)
            ?? throw new SnippetCompileException(
                $"the lambda captures '{variable.Name}', a {type}, but the game has no such type",
                position.File,
                position.Line
            );
    }
}
