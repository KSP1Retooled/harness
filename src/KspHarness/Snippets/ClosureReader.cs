using System.Reflection;
using System.Runtime.CompilerServices;

namespace KspHarness.Snippets;

/// <summary>A variable a lambda captured, with its declared type and its value now.</summary>
internal sealed record CapturedVariable(string Name, Type Type, object? Value);

/// <summary>
/// Reads the variables a lambda captured from its closure object. The C# compiler keeps captured locals as fields of
/// a display class, with a <c>CS$&lt;&gt;8__locals</c> field per enclosing scope and <c>&lt;&gt;4__this</c> for
/// <c>this</c>.
/// </summary>
internal static class ClosureReader
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static IReadOnlyList<CapturedVariable> Read(Delegate lambda, string code, SourcePosition position)
    {
        var used = LambdaText.UsedNames(code);
        var target = lambda.Target;
        var result = new List<CapturedVariable>();
        if (target is null || IsStaticLambdaCache(target.GetType()))
        {
            return result;
        }

        if (!IsDisplayClass(target.GetType()))
        {
            // The lambda used only members of the enclosing instance, so the compiler made it an instance method.
            RejectThis(target, used, code, position);
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        Walk(target, used, seen, result, code, position);
        return result;
    }

    private static void Walk(
        object closure,
        HashSet<string> used,
        HashSet<string> seen,
        List<CapturedVariable> result,
        string code,
        SourcePosition position)
    {
        var outer = new List<object>();
        foreach (var field in closure.GetType().GetFields(InstanceFields))
        {
            var value = field.GetValue(closure);
            if (field.Name.StartsWith("CS$<>8__locals", StringComparison.Ordinal))
            {
                if (value is not null)
                {
                    outer.Add(value);
                }

                continue;
            }

            if (field.Name == "<>4__this")
            {
                if (value is not null)
                {
                    RejectThis(value, used, code, position);
                }

                continue;
            }

            // Inner scopes come first, so a name shadowed in an inner scope keeps the inner value.
            if (used.Contains(field.Name) && seen.Add(field.Name))
            {
                result.Add(new CapturedVariable(field.Name, field.FieldType, value));
            }
        }

        foreach (var parent in outer)
        {
            Walk(parent, used, seen, result, code, position);
        }
    }

    private static void RejectThis(object instance, HashSet<string> used, string code, SourcePosition position)
    {
        var members = instance.GetType()
            .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Select(m => m.Name)
            .Where(used.Contains)
            .ToArray();

        if (LambdaText.UsesThis(code) || members.Length > 0)
        {
            var named = members.Length > 0 ? $" ({string.Join(", ", members)})" : "";
            throw new SnippetCompileException(
                $"the lambda uses this{named}, which can't be sent to the game. Copy the values it needs into local "
                + "variables before Game.Run and use those.",
                position.File,
                position.Line);
        }
    }

    private static bool IsDisplayClass(Type type)
    {
        return type.Name.Contains("DisplayClass", StringComparison.Ordinal)
            && type.IsDefined(typeof(CompilerGeneratedAttribute));
    }

    private static bool IsStaticLambdaCache(Type type)
    {
        return type.Name == "<>c" && type.IsDefined(typeof(CompilerGeneratedAttribute));
    }
}
