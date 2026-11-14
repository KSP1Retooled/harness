using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace KspHarness.Mod.Events;

/// <summary>
/// Names the mod assemblies whose code appears in a stack trace, as <c>name@version</c>, innermost frame first.
/// </summary>
internal static class ModAttribution
{
    public const int MaxMods = 8;

    // Swapped whole, so the logging threads read a complete index without a lock
    private static volatile Dictionary<string, string> modTypes = new Dictionary<string, string>();
    private static int indexedAssemblies = -1;

    /// <summary>Re-indexes when more plugins have loaded; call on the main thread.</summary>
    public static void Refresh()
    {
        AssemblyLoader.LoadedAssembyList loaded = AssemblyLoader.loadedAssemblies;
        if (loaded == null || loaded.Count == indexedAssemblies)
            return;

        var mods = new List<Assembly>(loaded.Count);
        foreach (AssemblyLoader.LoadedAssembly entry in loaded)
        {
            if (entry.assembly != null && !IsStock(entry))
                mods.Add(entry.assembly);
        }

        modTypes = IndexTypes(mods);
        indexedAssemblies = loaded.Count;
    }

    // The game's own plugins live in GameData/Squad and GameData/SquadExpansion
    private static bool IsStock(AssemblyLoader.LoadedAssembly entry) =>
        entry.url != null && entry.url.StartsWith("Squad", StringComparison.OrdinalIgnoreCase);

    public static List<string> Find(string stackTrace)
    {
        var mods = new List<string>();
        Dictionary<string, string> index = modTypes;
        if (string.IsNullOrEmpty(stackTrace) || index.Count == 0)
            return mods;

        foreach (string line in stackTrace.Split('\n'))
        {
            string key = FrameTypeKey(line);
            if (key == null || !index.TryGetValue(key, out string mod) || mods.Contains(mod))
                continue;

            mods.Add(mod);
            if (mods.Count == MaxMods)
                break;
        }

        return mods;
    }

    /// <summary>
    /// Maps each type to its assembly's <c>name@version</c>, keyed by namespace and name. A nested type is keyed as
    /// if it were a top-level type of its outer type's namespace, which is how Unity's stack traces name it.
    /// </summary>
    public static Dictionary<string, string> IndexTypes(IEnumerable<Assembly> assemblies)
    {
        var index = new Dictionary<string, string>();
        foreach (Assembly assembly in assemblies)
        {
            AssemblyName name = assembly.GetName();
            string mod = $"{name.Name}@{name.Version}";
            foreach (Type type in SafeGetTypes(assembly))
            {
                // Compiler-generated names such as <>c repeat in every assembly, so they identify nothing
                if (type == null || type.Name.StartsWith("<", StringComparison.Ordinal))
                    continue;

                string key = string.IsNullOrEmpty(type.Namespace) ? type.Name : $"{type.Namespace}.{type.Name}";
                if (!index.ContainsKey(key))
                    index.Add(key, mod);
            }
        }

        return index;
    }

    /// <summary>
    /// The index key of the type that declares the method of one stack frame, or null for a line that is not a
    /// method frame. Reads Mono's format (<c>at Ns.Outer+Inner.Method (args) [0x0] in file:0</c>) and Unity's
    /// (<c>Ns.Inner:Method (args) (at file:0)</c>).
    /// </summary>
    public static string FrameTypeKey(string line)
    {
        string frame = line.Trim();
        if (frame.StartsWith("at ", StringComparison.Ordinal))
            frame = frame.Substring(3);

        // Runtime wrappers name the method they wrap in their own syntax
        if (frame.StartsWith("(", StringComparison.Ordinal))
            return null;

        int arguments = frame.IndexOf('(');
        if (arguments <= 0)
            return null;

        string method = StripGenericArguments(frame.Substring(0, arguments).TrimEnd());
        int colon = method.LastIndexOf(':');
        int split = colon >= 0 ? colon : method.LastIndexOf('.');
        if (split <= 0)
            return null;

        // Mono writes a constructor as Type..ctor
        string type = method.Substring(0, split).TrimEnd('.');

        // Mono separates nested types with '+', IL with '/'; Unity's format names only the innermost type
        int nested = type.IndexOfAny(new[] { '+', '/' });
        return nested >= 0 ? type.Substring(0, nested) : type;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types;
        }
        catch (Exception)
        {
            return Type.EmptyTypes;
        }
    }

    // Removes [T] and, after a `N arity, <T>, leaving Type`1 as Type.Name spells it
    private static string StripGenericArguments(string name)
    {
        if (name.IndexOf('[') < 0 && name.IndexOf('`') < 0)
            return name;

        var text = new StringBuilder(name.Length);
        int depth = 0;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (depth > 0)
            {
                if (c == '[' || c == '<')
                    depth++;
                else if (c == ']' || c == '>')
                    depth--;

                continue;
            }

            if (c == '[' || (c == '<' && FollowsArity(name, i)))
            {
                depth = 1;
                continue;
            }

            text.Append(c);
        }

        return text.ToString();
    }

    private static bool FollowsArity(string name, int index)
    {
        int i = index - 1;
        while (i >= 0 && char.IsDigit(name[i]))
            i--;

        return i < index - 1 && i >= 0 && name[i] == '`';
    }
}
