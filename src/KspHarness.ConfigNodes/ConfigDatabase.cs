using System;
using System.Collections.Generic;
using System.IO;

namespace KspHarness.ConfigNodes;

/// <summary>One top-level config of the game database, as KSP's <c>UrlDir.UrlConfig</c> describes it.</summary>
public sealed class UrlConfig
{
    public UrlConfig(string file, CfgNode node)
    {
        File = file;
        Node = node;
    }

    /// <summary>
    /// The file the config came from, relative to GameData with its extension (<c>Squad/Parts/Command/mk1pod_v2/mk1Pod_v2.cfg</c>).
    /// ModuleManager writes <c>/Physics.cfg</c> for the root's Physics.cfg.
    /// </summary>
    public string File { get; }

    public CfgNode Node { get; }

    /// <summary>The node's type: <c>PART</c>, <c>RESOURCE_DEFINITION</c> and so on.</summary>
    public string Type => Node.Name;

    /// <summary>The node's <c>name</c> value, or its type when it has none, as KSP's <c>UrlConfig.name</c>.</summary>
    public string Name => Node.NameValue ?? Node.Name;

    /// <summary>KSP's <c>UrlConfig.url</c>: the file's path without extension, a slash and <see cref="Name"/>.</summary>
    public string Url
    {
        get
        {
            var dot = File.LastIndexOf('.');
            var slash = File.LastIndexOf('/');
            var fileUrl = dot > slash ? File.Substring(0, dot) : File;
            return fileUrl + "/" + Name;
        }
    }

    public override string ToString()
    {
        return Url;
    }
}

/// <summary>
/// The patched game database ModuleManager saves in <c>GameData/ModuleManager.ConfigCache</c>: every top-level
/// config after patching, in load order, with the file it came from.
/// </summary>
public sealed class ConfigDatabase
{
    private readonly List<UrlConfig> configs;

    private readonly List<CfgNode> nodes;

    public ConfigDatabase(IEnumerable<UrlConfig> configs)
    {
        this.configs = new List<UrlConfig>(configs);
        nodes = this.configs.ConvertAll(config => config.Node);
    }

    public IReadOnlyList<UrlConfig> Configs => configs;

    /// <summary>The top-level nodes, in the same order as <see cref="Configs"/>.</summary>
    public IReadOnlyList<CfgNode> Nodes => nodes;

    /// <summary>ModuleManager's count of nodes its patches changed, when the cache records it.</summary>
    public int? PatchedNodeCount { get; private set; }

    /// <summary>Loads <c>ModuleManager.ConfigCache</c>: <c>UrlConfig { parentUrl = file  NODE { ... } }</c> entries.</summary>
    public static ConfigDatabase LoadModuleManagerCache(string path)
    {
        return FromModuleManagerCache(ConfigFile.Load(path));
    }

    public static ConfigDatabase FromModuleManagerCache(CfgNode root)
    {
        var result = new List<UrlConfig>();
        foreach (var entry in root.Nodes)
        {
            if (entry.Name != "UrlConfig")
                continue;

            var file =
                entry.GetValue("parentUrl")
                ?? throw new FormatException("a UrlConfig entry in the ModuleManager cache has no parentUrl");

            foreach (var node in entry.Nodes)
                result.Add(new UrlConfig(file, node));
        }

        var database = new ConfigDatabase(result);
        if (root.TryGetValue("patchedNodeCount", out int patched))
            database.PatchedNodeCount = patched;

        return database;
    }

    /// <summary>The top-level nodes a query selects; its first segment matches them. See <see cref="ConfigQuery"/>.</summary>
    public List<CfgNode> Select(string query)
    {
        return ConfigQuery.Parse(query).SelectNodes(Nodes, CfgNodeAdapter.Instance);
    }

    /// <summary>The values a query selects; its first segment matches the top-level nodes, its last is a key.</summary>
    public List<ConfigValue> SelectValues(string query)
    {
        return ConfigValue.From(ConfigQuery.Parse(query).SelectValues(Nodes, CfgNodeAdapter.Instance));
    }

    /// <summary>The configs whose node a query selects as a top-level node.</summary>
    public List<UrlConfig> SelectConfigs(string query)
    {
        var selected = new HashSet<CfgNode>(Select(query));
        return configs.FindAll(config => selected.Contains(config.Node));
    }

    /// <summary>
    /// Builds an unpatched database from the <c>.cfg</c> files under a GameData folder, in ordinal path order
    /// (KSP's own load order may differ).
    /// </summary>
    public static ConfigDatabase LoadGameData(string gameDataDir)
    {
        var result = new List<UrlConfig>();
        var root = Path.GetFullPath(gameDataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var files = Directory.GetFiles(root, "*.cfg", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            var relative = path.Substring(root.Length + 1).Replace('\\', '/');
            foreach (var node in ConfigFile.Load(path).Nodes)
                result.Add(new UrlConfig(relative, node));
        }

        return new ConfigDatabase(result);
    }
}
