using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace KspHarness;

/// <summary>
/// Lets test code that references the game's assemblies at compile time (with <c>Private=false</c>) load them at run
/// time. A typed lambda such as <c>() =&gt; HighLogic.LoadedScene</c> is a delegate over game types in the test
/// itself, and decoding its result into <c>GameScenes</c> needs the type. Only metadata is used: no game code runs in
/// the driver.
/// </summary>
/// <remarks>
/// An assembly binds each reference once, so code loaded normally sees one game's types for the whole process. To
/// drive a stock and a Retooled game from one process, each with its own types, load the test code into the game's
/// <see cref="ContextFor"/> with <see cref="LoadInto(string, Assembly)"/>; the suite runner does this per install.
/// </remarks>
public static class GameAssemblies
{
    // Two spellings of one folder must share a context. Windows and macOS file systems ignore case by default; Linux
    // file systems do not, and two folders that differ only in case are different games there.
    private static readonly StringComparer PathComparer = OperatingSystem.IsLinux()
        ? StringComparer.Ordinal
        : StringComparer.OrdinalIgnoreCase;

    private static readonly ConcurrentQueue<string> Directories = new();
    private static readonly ConcurrentDictionary<string, InstanceLoadContext> Contexts = new(PathComparer);
    private static readonly Lazy<HashSet<string>> PlatformNames = new(ReadPlatformNames);
    private static int hooked;

    /// <summary>
    /// Resolves assemblies the default load context cannot find from <paramref name="managedDir"/>. Directories added
    /// earlier win, and an assembly stays loaded once resolved, so code in the default context that drives stock and
    /// Retooled games uses the types of whichever game it met first.
    /// </summary>
    public static void AddDirectory(string managedDir)
    {
        managedDir = Path.GetFullPath(managedDir);
        if (!Directories.Contains(managedDir, PathComparer))
        {
            Directories.Enqueue(managedDir);
        }

        if (Interlocked.Exchange(ref hooked, 1) == 0)
        {
            AssemblyLoadContext.Default.Resolving += Resolve;
        }
    }

    /// <summary>
    /// The load context for one game's <c>Managed</c> folder: the game's assemblies come from that folder, and
    /// everything the application ships (this library, the runtime) from the default context, so code in it shares
    /// the harness's types. One context per folder for the life of the process.
    /// </summary>
    public static AssemblyLoadContext ContextFor(string managedDir)
    {
        managedDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedDir));
        return Contexts.GetOrAdd(managedDir, dir => new InstanceLoadContext(dir));
    }

    /// <summary>
    /// A copy of <paramref name="assembly"/> (test code) whose game references bind to <paramref name="managedDir"/>.
    /// The copy is loaded once per folder, and its types are distinct from the original's.
    /// </summary>
    public static Assembly LoadInto(string managedDir, Assembly assembly)
    {
        if (string.IsNullOrEmpty(assembly.Location))
        {
            throw new ArgumentException($"{assembly.GetName().Name} was not loaded from a file", nameof(assembly));
        }

        return LoadInto(managedDir, assembly.Location);
    }

    /// <inheritdoc cref="LoadInto(string, Assembly)"/>
    public static Assembly LoadInto(string managedDir, string assemblyPath)
    {
        var context = ContextFor(managedDir);
        var name = AssemblyName.GetAssemblyName(assemblyPath).Name;
        lock (context)
        {
            return context.Assemblies.FirstOrDefault(a => a.GetName().Name == name)
                ?? context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        }
    }

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        foreach (var directory in Directories)
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(path))
            {
                return context.LoadFromAssemblyPath(path);
            }
        }

        return null;
    }

    // The runtime and the application's own assemblies always come from the default context, even where the game's
    // folder has a file of the same name (its mscorlib, System, netstandard).
    private static HashSet<string> ReadPlatformNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
        foreach (var path in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            names.Add(Path.GetFileNameWithoutExtension(path));
        }

        return names;
    }

    private sealed class InstanceLoadContext(string managedDir) : AssemblyLoadContext($"KSP game {managedDir}")
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var name = assemblyName.Name;
            if (name is null || PlatformNames.Value.Contains(name))
            {
                return null;
            }

            var path = Path.Combine(managedDir, name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
