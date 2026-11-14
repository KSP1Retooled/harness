using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace KspHarness.Mod.Runtime;

/// <summary>Loads snippet assemblies by hash and keeps them for the rest of the game's life.</summary>
internal static class SnippetLoader
{
    private static readonly Dictionary<string, Assembly> ByHash = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<Assembly> Snippets = new HashSet<Assembly>();
    private static readonly object Gate = new object();
    private static bool resolverInstalled;

    public static void Load(string hash, byte[] assembly, byte[] symbols)
    {
        lock (Gate)
        {
            if (ByHash.ContainsKey(hash))
                return;

            InstallResolver();
            Assembly loaded;
            try
            {
                loaded = symbols != null ? Assembly.Load(assembly, symbols) : Assembly.Load(assembly);
            }
            catch (Exception e) when (symbols != null && !(e is BadImageFormatException))
            {
                // A runtime that cannot read the symbol format still runs the code, just without line numbers
                Debug.LogWarning($"[KspHarness] Loading snippet {hash} without its symbols: {e.Message}");
                loaded = Assembly.Load(assembly);
            }

            ByHash[hash] = loaded;
            Snippets.Add(loaded);
        }
    }

    public static Assembly Find(string hash)
    {
        lock (Gate)
            return ByHash.TryGetValue(hash, out Assembly assembly) ? assembly : null;
    }

    public static bool IsSnippetAssembly(Assembly assembly)
    {
        lock (Gate)
            return Snippets.Contains(assembly);
    }

    // Snippets are compiled against the game's assemblies and this mod by simple name; an assembly loaded from bytes
    // has no folder to probe, so point its references at what is already loaded
    private static void InstallResolver()
    {
        if (resolverInstalled)
            return;

        resolverInstalled = true;
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            string name = new AssemblyName(args.Name).Name;
            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(loaded.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                    return loaded;
            }

            return null;
        };
    }
}
