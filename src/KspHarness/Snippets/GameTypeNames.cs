using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace KspHarness.Snippets;

/// <summary>Spells types as C# that compiles against the game's references.</summary>
internal static class GameTypeNames
{
    /// <summary>
    /// The C# spelling of a type the game reported by its reflection full name (for example
    /// <c>System.Collections.Generic.List`1[[Part, Assembly-CSharp, Version=...]]</c>), or null when the references
    /// do not declare it.
    /// </summary>
    /// <remarks>
    /// Where several references declare the name, snippet code gets the earliest one's (see
    /// <see cref="SnippetCompiler"/>), so that is the type a name means. A value the game reported from a later one
    /// (<paramref name="assembly"/>, the simple name the game gave) has no name a snippet can use, and gets null.
    /// </remarks>
    public static string? FromGameName(CSharpCompilation references, string fullName, string? assembly = null)
    {
        var symbol = Resolve(references, fullName.Trim(), assembly);
        return symbol is not null && IsSpeakable(symbol)
            ? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : null;
    }

    /// <summary>
    /// False for a type C# cannot name, such as a compiler-generated iterator (<c>Foo+&lt;GetParts&gt;d__12</c>), or
    /// one built from such a type.
    /// </summary>
    private static bool IsSpeakable(ITypeSymbol type)
    {
        return type switch
        {
            IArrayTypeSymbol array => IsSpeakable(array.ElementType),
            IPointerTypeSymbol pointer => IsSpeakable(pointer.PointedAtType),
            INamedTypeSymbol named => named.CanBeReferencedByName
                && (named.ContainingType is null || IsSpeakable(named.ContainingType))
                && named.TypeArguments.All(IsSpeakable),
            _ => true,
        };
    }

    /// <summary>The C# spelling of a driver-side type, or null when the game's references lack it.</summary>
    public static string? FromDriverType(CSharpCompilation references, Type type)
    {
        if (type.IsArray)
        {
            var element = FromDriverType(references, type.GetElementType()!);
            var commas = new string(',', type.GetArrayRank() - 1);
            return element is null ? null : $"{element}[{commas}]";
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            var inner = FromDriverType(references, underlying);
            return inner is null ? null : inner + "?";
        }

        if (type.IsGenericType)
        {
            var definition = Find(references, type.GetGenericTypeDefinition().FullName!, null);
            var arguments = type.GetGenericArguments().Select(a => FromDriverType(references, a)).ToArray();
            if (definition is null || arguments.Any(a => a is null))
            {
                return null;
            }

            var name = definition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var bare = name[..name.IndexOf('<')];
            return $"{bare}<{string.Join(", ", arguments)}>";
        }

        return Find(references, type.FullName!, null)?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    /// <summary>
    /// The type a snippet means by this metadata name: the one in the earliest reference that declares it. Null when
    /// none does, or when <paramref name="assembly"/> names a later one.
    /// </summary>
    private static INamedTypeSymbol? Find(CSharpCompilation references, string metadataName, string? assembly)
    {
        var candidates = references.GetTypesByMetadataName(metadataName);
        if (candidates.Length <= 1)
        {
            return candidates.FirstOrDefault();
        }

        var priority = references.References
            .Select((reference, index) => (Symbol: references.GetAssemblyOrModuleSymbol(reference), Index: index))
            .Where(entry => entry.Symbol is IAssemblySymbol)
            .ToDictionary(entry => (IAssemblySymbol)entry.Symbol!, entry => entry.Index, SymbolEqualityComparer.Default);
        var first = candidates.OrderBy(c => priority.GetValueOrDefault(c.ContainingAssembly, int.MaxValue)).First();
        return assembly is null || string.Equals(first.ContainingAssembly.Name, assembly, StringComparison.OrdinalIgnoreCase)
            ? first
            : null;
    }

    private static ITypeSymbol? Resolve(CSharpCompilation references, string name, string? assembly)
    {
        if (name.EndsWith("[]", StringComparison.Ordinal))
        {
            var element = Resolve(references, name[..^2], assembly);
            return element is null ? null : references.CreateArrayTypeSymbol(element);
        }

        var open = name.IndexOf('[');
        if (open < 0)
        {
            return Find(references, name, assembly);
        }

        var definition = Find(references, name[..open], null);
        if (definition is null)
        {
            return null;
        }

        var arguments = SplitGenericArguments(name[open..]);
        if (arguments is null || arguments.Count != definition.Arity)
        {
            return null;
        }

        var symbols = new ITypeSymbol[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = Resolve(references, arguments[i], null);
            if (argument is null)
            {
                return null;
            }

            symbols[i] = argument;
        }

        return definition.Construct(symbols);
    }

    /// <summary>
    /// Splits <c>[[A, asm],[B, asm]]</c> into the type names A and B, dropping assembly qualifications.
    /// </summary>
    private static List<string>? SplitGenericArguments(string text)
    {
        if (!text.StartsWith('[') || !text.EndsWith(']'))
        {
            return null;
        }

        var inner = text[1..^1];
        var result = new List<string>();
        var depth = 0;
        var start = -1;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '[')
            {
                if (depth == 0)
                {
                    start = i + 1;
                }

                depth++;
            }
            else if (c == ']')
            {
                depth--;
                if (depth == 0)
                {
                    result.Add(StripAssembly(inner[start..i]));
                }
            }
        }

        return result;
    }

    private static string StripAssembly(string qualified)
    {
        // The type name ends at the first comma outside brackets; the rest names the assembly.
        var depth = 0;
        for (var i = 0; i < qualified.Length; i++)
        {
            switch (qualified[i])
            {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    return qualified[..i].Trim();
            }
        }

        return qualified.Trim();
    }
}
