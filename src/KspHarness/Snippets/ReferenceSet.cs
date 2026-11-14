using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using KspHarness.Installs;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace KspHarness.Snippets;

/// <summary>
/// The assemblies a snippet compiles against: the instance's own managed DLLs, its GameData plugins and the driver
/// mod, one file per assembly name.
/// </summary>
public sealed class ReferenceSet
{
    private static readonly ConcurrentDictionary<string, ReferenceSet> Loaded = new();

    private readonly Lazy<IReadOnlyList<MetadataReference>> references;
    private readonly Lazy<CSharpCompilation> baseCompilation;

    private ReferenceSet(IReadOnlyList<ReferenceFile> files)
    {
        Files = files;

        var identity = new StringBuilder();
        foreach (var file in files)
        {
            identity.Append(file.Path).Append('|').Append(file.Size).Append('|').Append(file.ModifiedUtc.Ticks).Append('\n');
        }

        Identity = identity.ToString();
        references = new Lazy<IReadOnlyList<MetadataReference>>(
            () => files.Select(f => (MetadataReference)MetadataReference.CreateFromFile(f.Path)).ToArray());
        baseCompilation = new Lazy<CSharpCompilation>(
            () => CSharpCompilation.Create("references", references: References, options: SnippetCompiler.CreateOptions()));
    }

    /// <summary>The references in priority order: where two declare the same type, snippets get the earlier one's.</summary>
    public IReadOnlyList<ReferenceFile> Files { get; }

    /// <summary>Path, size and modification time of every file, which is what the snippet hash covers.</summary>
    public string Identity { get; }

    internal IReadOnlyList<MetadataReference> References => references.Value;

    /// <summary>A compilation with no sources, to look up types in the game's assemblies.</summary>
    internal CSharpCompilation BaseCompilation => baseCompilation.Value;

    /// <summary>
    /// The references of a KSP install: its data folder's <c>Managed</c> (<c>KSP_x64_Data</c>, <c>KSP_Data</c> or
    /// <c>KSP.app/Contents/Resources/Data</c>), then <paramref name="modAssemblyPath"/> when given, then every .NET
    /// assembly under <c>GameData</c>.
    /// </summary>
    public static ReferenceSet FromInstance(string instanceDir, string? modAssemblyPath = null)
    {
        var managed = GameLayout.FindByManagedDir(instanceDir)?.ManagedDir
            ?? throw new DirectoryNotFoundException(
                $"{instanceDir} is not a KSP install: it has no KSP_x64_Data/Managed, KSP_Data/Managed or "
                + "KSP.app/Contents/Resources/Data/Managed");

        var paths = new List<string>();
        paths.AddRange(SortedDlls(managed, SearchOption.TopDirectoryOnly));
        if (modAssemblyPath is not null)
        {
            paths.Add(Path.GetFullPath(modAssemblyPath));
        }

        var gameData = Path.Combine(instanceDir, "GameData");
        if (Directory.Exists(gameData))
        {
            // KSP loads no assemblies from PluginData folders; a copy there must not shadow the one the game runs.
            paths.AddRange(SortedDlls(gameData, SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(gameData, path)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Contains("PluginData", StringComparer.OrdinalIgnoreCase)));
        }

        return FromFiles(paths);
    }

    /// <summary>A reference set over these files, in priority order: the first file with an assembly name wins.</summary>
    public static ReferenceSet FromFiles(IEnumerable<string> paths)
    {
        var files = new List<ReferenceFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new FileNotFoundException("a snippet reference does not exist", path);
            }

            var name = ReadAssemblyName(path);
            if (name is null || !names.Add(name))
            {
                continue;
            }

            files.Add(new ReferenceFile(info.FullName, name, info.Length, info.LastWriteTimeUtc));
        }

        var set = new ReferenceSet(files);

        // Reference sets are reused so that Roslyn reads each set of DLLs once per process.
        return Loaded.GetOrAdd(set.Identity, set);
    }

    /// <summary>
    /// The extern alias an assembly gets when a snippet names a type it shares with an earlier reference: its name
    /// with every character other than a letter or digit as <c>_</c>, after a leading <c>_</c> that keeps it an
    /// identifier (<c>Unity.TextMeshPro</c> is <c>_Unity_TextMeshPro</c>).
    /// </summary>
    public static string AliasFor(string assemblyName)
    {
        var alias = new StringBuilder("_");
        foreach (var c in assemblyName)
        {
            alias.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return alias.ToString();
    }

    /// <summary>The references with the named assemblies moved out of the global namespace under their aliases.</summary>
    internal IReadOnlyList<MetadataReference> ReferencesWithAliases(IReadOnlyCollection<string> aliasedAssemblies)
    {
        if (aliasedAssemblies.Count == 0)
        {
            return References;
        }

        return References
            .Select((reference, i) => aliasedAssemblies.Contains(Files[i].AssemblyName)
                ? reference.WithAliases([AliasFor(Files[i].AssemblyName)])
                : reference)
            .ToArray();
    }

    /// <summary>The position of an assembly in <see cref="Files"/>, or <see cref="int.MaxValue"/> when absent.</summary>
    internal int PriorityOf(string assemblyName)
    {
        for (var i = 0; i < Files.Count; i++)
        {
            if (string.Equals(Files[i].AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private static IEnumerable<string> SortedDlls(string directory, SearchOption option)
    {
        return Directory.EnumerateFiles(directory, "*.dll", option).Order(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The assembly's simple name, or null for native DLLs and modules without an assembly manifest.</summary>
    private static string? ReadAssemblyName(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return null;
            }

            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                return null;
            }

            return reader.GetString(reader.GetAssemblyDefinition().Name);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }
}

public sealed record ReferenceFile(string Path, string AssemblyName, long Size, DateTime ModifiedUtc);
