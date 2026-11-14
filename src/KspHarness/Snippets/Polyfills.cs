using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace KspHarness.Snippets;

/// <summary>
/// Types that newer C# features need and that the game's base library lacks. They are compiled into the snippet
/// assembly as internal types, only when no reference declares them.
/// </summary>
internal static class Polyfills
{
    private static readonly (string MetadataName, string Source)[] Types =
    [
        (
            "System.Runtime.CompilerServices.IsExternalInit",
            """
            namespace System.Runtime.CompilerServices
            {
                internal static class IsExternalInit
                {
                }
            }
            """
        ),
        (
            "System.Runtime.CompilerServices.RequiredMemberAttribute",
            """
            namespace System.Runtime.CompilerServices
            {
                [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
                internal sealed class RequiredMemberAttribute : Attribute
                {
                }
            }
            """
        ),
        (
            "System.Runtime.CompilerServices.CompilerFeatureRequiredAttribute",
            """
            namespace System.Runtime.CompilerServices
            {
                [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
                internal sealed class CompilerFeatureRequiredAttribute : Attribute
                {
                    public CompilerFeatureRequiredAttribute(string featureName)
                    {
                        FeatureName = featureName;
                    }

                    public string FeatureName { get; }

                    public bool IsOptional { get; set; }
                }
            }
            """
        ),
        (
            "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute",
            """
            namespace System.Diagnostics.CodeAnalysis
            {
                [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
                internal sealed class SetsRequiredMembersAttribute : Attribute
                {
                }
            }
            """
        ),
        (
            "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute",
            """
            namespace System.Runtime.CompilerServices
            {
                [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
                internal sealed class IgnoresAccessChecksToAttribute : Attribute
                {
                    public IgnoresAccessChecksToAttribute(string assemblyName)
                    {
                        AssemblyName = assemblyName;
                    }

                    public string AssemblyName { get; }
                }
            }
            """
        ),
        (
            "System.Index",
            """
            namespace System
            {
                internal readonly struct Index
                {
                    private readonly int value;

                    public Index(int value, bool fromEnd = false)
                    {
                        if (value < 0)
                        {
                            throw new ArgumentOutOfRangeException(nameof(value));
                        }

                        this.value = fromEnd ? ~value : value;
                    }

                    public static Index Start => new Index(0);

                    public static Index End => new Index(0, true);

                    public int Value => value < 0 ? ~value : value;

                    public bool IsFromEnd => value < 0;

                    public static Index FromStart(int value) => new Index(value);

                    public static Index FromEnd(int value) => new Index(value, true);

                    public int GetOffset(int length) => IsFromEnd ? length - Value : Value;

                    public static implicit operator Index(int value) => new Index(value);
                }
            }
            """
        ),
        (
            "System.Range",
            """
            namespace System
            {
                internal readonly struct Range
                {
                    public Range(Index start, Index end)
                    {
                        Start = start;
                        End = end;
                    }

                    public Index Start { get; }

                    public Index End { get; }

                    public static Range All => new Range(Index.Start, Index.End);

                    public static Range StartAt(Index start) => new Range(start, Index.End);

                    public static Range EndAt(Index end) => new Range(Index.Start, end);

                    public (int Offset, int Length) GetOffsetAndLength(int length)
                    {
                        int start = Start.GetOffset(length);
                        int end = End.GetOffset(length);
                        if ((uint)end > (uint)length || (uint)start > (uint)end)
                        {
                            throw new ArgumentOutOfRangeException(nameof(length));
                        }

                        return (start, end - start);
                    }
                }
            }
            """
        ),
    ];

    /// <summary>Source for the polyfills the references lack, or null when they have them all.</summary>
    /// <remarks>
    /// A plugin's own internal copy does not count: Harmony 2 declares an internal IgnoresAccessChecksToAttribute, and
    /// an install with Harmony as its only declaration would otherwise leave snippets without a usable one. Two
    /// declarations make the lookup ambiguous, which also reads as missing.
    /// </remarks>
    public static string? SourceFor(CSharpCompilation references)
    {
        var source = new StringBuilder();
        foreach (var (metadataName, typeSource) in Types)
        {
            if (references.GetTypeByMetadataName(metadataName) is not { DeclaredAccessibility: Accessibility.Public })
            {
                source.Append(typeSource).Append("\n\n");
            }
        }

        return source.Length == 0 ? null : "// <auto-generated/>\n#nullable disable\nusing System;\n\n" + source;
    }
}
