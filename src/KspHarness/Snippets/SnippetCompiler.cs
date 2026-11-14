using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace KspHarness.Snippets;

/// <summary>A compiled snippet, ready to load into the game.</summary>
public sealed record CompiledSnippet(string Hash, string TypeName, string MethodName, byte[] Assembly, byte[]? Symbols);

/// <summary>
/// Compiles snippets with Roslyn against one reference set, with access checks off so that snippets reach private
/// members. Results are cached in memory and on disk by hash.
/// </summary>
public sealed class SnippetCompiler
{
    // Part of the hash: a change to the generated code's shape or the compiler must not reuse old assemblies.
    private const string FormatVersion = "1";

    // Roslyn's internal BinderFlags.IgnoreAccessibility; the flag is what the scripting and debugger engines use.
    private const uint IgnoreAccessibility = 1u << 22;

    private static readonly PropertyInfo? TopLevelBinderFlags = typeof(CSharpCompilationOptions).GetProperty(
        "TopLevelBinderFlags",
        BindingFlags.Instance | BindingFlags.NonPublic
    );

    private static readonly string CompilerVersion =
        typeof(CSharpCompilation)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(CSharpCompilation).Assembly.GetName().Version!.ToString();

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, DocumentationMode.None);

    private static readonly EmitOptions EmitSettings = new(debugInformationFormat: DebugInformationFormat.PortablePdb);

    private readonly ConcurrentDictionary<string, CompiledSnippet> memoryCache = new();
    private readonly Lazy<SyntaxTree[]> supportTrees;
    private readonly Lazy<string> supportFingerprint;

    public SnippetCompiler(ReferenceSet references, string? cacheDirectory)
    {
        References = references;
        CacheDirectory = cacheDirectory;
        supportTrees = new Lazy<SyntaxTree[]>(CreateSupportTrees);
        supportFingerprint = new Lazy<string>(() => Fingerprint(supportTrees.Value, CreateOptions()));
    }

    public ReferenceSet References { get; }

    /// <summary>Where compiled snippets are kept between runs (<c>&lt;home&gt;/snippets</c>), or null for none.</summary>
    public string? CacheDirectory { get; }

    internal static CSharpCompilationOptions CreateOptions()
    {
        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true,
            optimizationLevel: OptimizationLevel.Debug,
            deterministic: true,
            nullableContextOptions: NullableContextOptions.Disable
        ).WithMetadataImportOptions(MetadataImportOptions.All);

        if (TopLevelBinderFlags is null)
        {
            throw new InvalidOperationException(
                $"Roslyn {CompilerVersion} has no TopLevelBinderFlags, which snippets need to reach private members"
            );
        }

        TopLevelBinderFlags.SetValue(options, IgnoreAccessibility);
        return options;
    }

    internal CompiledSnippet Compile(SnippetSource snippet)
    {
        var generated = snippet.Generate();
        var hash = Hash(generated);
        if (memoryCache.TryGetValue(hash, out var cached))
        {
            return cached;
        }

        var className = SnippetSource.ClassName(hash);
        var typeName = $"{SnippetSource.Namespace}.{className}";
        var compiled =
            ReadDiskCache(hash, typeName)
            ?? Emit(snippet, generated.Replace("S_" + SnippetSource.HashPlaceholder, className), hash, typeName);
        return memoryCache.GetOrAdd(hash, compiled);
    }

    private string Hash(string generated)
    {
        // Everything else that goes into the assembly is part of the key too, since the disk cache outlives builds of
        // this library: the support sources and the options.
        var text = new StringBuilder()
            .Append(FormatVersion)
            .Append('\n')
            .Append(CompilerVersion)
            .Append('\n')
            .Append(References.Identity)
            .Append('\n')
            .Append(supportFingerprint.Value)
            .Append('\n')
            .Append(generated);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private CompiledSnippet Emit(SnippetSource snippet, string source, string hash, string typeName)
    {
        // A name two references declare (KSP's TextMesh Pro in Assembly-CSharp and Unity's in Unity.TextMeshPro, both
        // in Retooled) is ambiguous to Roslyn. The game binds it to the earlier reference, the one stock-built code
        // names, so each later assembly an error names goes under an extern alias and the snippet compiles again.
        var aliased = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            // Extern aliases must come before every using directive, so they lead the file.
            var aliasLines = string.Concat(
                aliased.Order(StringComparer.Ordinal).Select(name => $"extern alias {ReferenceSet.AliasFor(name)};\n")
            );
            var text = aliasLines + source;
            var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, path: "snippet.g.cs", encoding: Encoding.UTF8);
            var compilation = CSharpCompilation.Create(
                "KspHarness.Snippet." + hash[..16],
                [tree, .. supportTrees.Value],
                References.ReferencesWithAliases(aliased),
                CreateOptions()
            );

            using var assembly = new MemoryStream();
            using var symbols = new MemoryStream();
            var result = compilation.Emit(assembly, symbols, options: EmitSettings);

            if (result.Success)
            {
                var compiled = new CompiledSnippet(
                    hash,
                    typeName,
                    SnippetSource.MethodName,
                    assembly.ToArray(),
                    symbols.ToArray()
                );
                WriteDiskCache(compiled);
                return compiled;
            }

            var shadowed = ShadowedAssemblies(result.Diagnostics).Where(aliased.Add).ToList();
            if (shadowed.Count == 0)
            {
                throw SnippetCompileException.From(result.Diagnostics, text, snippet.Position);
            }
        }
    }

    /// <summary>
    /// For each "exists in both" error, the referenced assemblies it names except the one with the highest priority.
    /// The assembly names are matched in the message text, which quotes them the same way in every language.
    /// </summary>
    private IEnumerable<string> ShadowedAssemblies(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics.Where(d => d.Id == "CS0433"))
        {
            var message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
            var named = References
                .Files.Select(file => file.AssemblyName)
                .Where(name => message.Contains($"'{name},", StringComparison.Ordinal))
                .OrderBy(References.PriorityOf)
                .ToList();

            foreach (var name in named.Skip(1))
            {
                yield return name;
            }
        }
    }

    private SyntaxTree[] CreateSupportTrees()
    {
        var trees = new List<SyntaxTree>();
        if (Polyfills.SourceFor(References.BaseCompilation) is { } polyfills)
        {
            trees.Add(
                CSharpSyntaxTree.ParseText(polyfills, ParseOptions, path: "polyfills.g.cs", encoding: Encoding.UTF8)
            );
        }

        // Mono reads this attribute when it checks member access, so private members stay in reach at run time too.
        var attributes = new StringBuilder("// <auto-generated/>\n");
        foreach (var file in References.Files)
        {
            attributes.Append(
                $"[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo(\"{file.AssemblyName}\")]\n"
            );
        }

        trees.Add(
            CSharpSyntaxTree.ParseText(
                attributes.ToString(),
                ParseOptions,
                path: "access.g.cs",
                encoding: Encoding.UTF8
            )
        );
        return trees.ToArray();
    }

    /// <summary>The support sources and the settings that shape the emitted assembly, as text.</summary>
    internal static string Fingerprint(IEnumerable<SyntaxTree> supportTrees, CSharpCompilationOptions options)
    {
        var text = new StringBuilder();
        foreach (var tree in supportTrees)
        {
            text.Append(tree.FilePath).Append('\n').Append(tree.GetText()).Append('\n');
        }

        // CompilationOptions.GetHashCode mixes in string hashes, which differ between processes, so the settings
        // this compiler sets are spelled out instead.
        text.Append(options.OutputKind)
            .Append(';')
            .Append(options.AllowUnsafe)
            .Append(';')
            .Append(options.OptimizationLevel)
            .Append(';')
            .Append(options.Deterministic)
            .Append(';')
            .Append(options.NullableContextOptions)
            .Append(';')
            .Append(options.MetadataImportOptions)
            .Append(';')
            .Append(TopLevelBinderFlags?.GetValue(options))
            .Append(';')
            .Append(ParseOptions.LanguageVersion)
            .Append(';')
            .Append(EmitSettings.DebugInformationFormat);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private CompiledSnippet? ReadDiskCache(string hash, string typeName)
    {
        if (CacheDirectory is null)
        {
            return null;
        }

        var dll = Path.Combine(CacheDirectory, hash + ".dll");
        if (!File.Exists(dll))
        {
            return null;
        }

        try
        {
            var pdb = Path.ChangeExtension(dll, ".pdb");
            var symbols = File.Exists(pdb) ? File.ReadAllBytes(pdb) : null;
            return new CompiledSnippet(hash, typeName, SnippetSource.MethodName, File.ReadAllBytes(dll), symbols);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteDiskCache(CompiledSnippet compiled)
    {
        if (CacheDirectory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(CacheDirectory);
            if (compiled.Symbols is not null)
            {
                WriteAtomically(Path.Combine(CacheDirectory, compiled.Hash + ".pdb"), compiled.Symbols);
            }

            // The .dll goes last: a reader that finds it can rely on the .pdb being complete.
            WriteAtomically(Path.Combine(CacheDirectory, compiled.Hash + ".dll"), compiled.Assembly);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The cache only saves time; another process writing the same hash is equally good. Windows refuses to
            // replace a file another process is reading with access denied, not a sharing violation.
        }
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }
}
