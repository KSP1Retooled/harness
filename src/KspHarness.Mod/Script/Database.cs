using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using KspHarness.ConfigNodes;

namespace KspHarness.Script;

/// <summary>
/// Queries the live <c>GameDatabase</c> (after ModuleManager has patched it) with the config query language of
/// <c>KspHarness.ConfigNodes</c>, whose README has the grammar. The top-level configs are the root list, so
/// <c>PART[mk1pod_v2]/MODULE[ModuleCommand]/minimumCrew</c> reads a part's value.
/// </summary>
/// <remarks>
/// Once loaded, a part's config in the database no longer has its <c>name</c> value; only its <c>UrlConfig.name</c>
/// keeps it. Top-level nodes without a <c>name</c> value are queried and written as if they had that one first, so
/// queries match the cfg files and ModuleManager's view of them.
/// </remarks>
public static class Database
{
    /// <summary>The matching nodes, each written as cfg text.</summary>
    public static List<string> Query(string query)
    {
        TopLevelAdapter adapter = TopLevel();
        return ConfigQuery.Parse(query).SelectNodes(adapter.Roots, adapter)
            .Select(node => ConfigWriter.WriteNode(ToCfgNode(node, adapter)))
            .ToList();
    }

    /// <summary>The matching values' text; the query's last segment names the key.</summary>
    public static List<string> Values(string query)
    {
        TopLevelAdapter adapter = TopLevel();
        return ConfigQuery.Parse(query).SelectValues(adapter.Roots, adapter).Select(v => v.Value).ToList();
    }

    /// <summary>The matching live nodes; changing them changes the game's database.</summary>
    public static List<ConfigNode> Nodes(string query)
    {
        TopLevelAdapter adapter = TopLevel();
        return ConfigQuery.Parse(query).SelectNodes(adapter.Roots, adapter);
    }

    private static CfgNode ToCfgNode(ConfigNode node, INodeAdapter<ConfigNode> adapter)
    {
        var copy = new CfgNode(adapter.GetName(node));
        int values = adapter.GetValueCount(node);
        for (int i = 0; i < values; i++)
            copy.AddValue(adapter.GetValueKey(node, i), adapter.GetValueText(node, i));

        int nodes = adapter.GetNodeCount(node);
        for (int i = 0; i < nodes; i++)
            copy.AddNode(ToCfgNode(adapter.GetNode(node, i), adapter));

        return copy;
    }

    private static TopLevelAdapter TopLevel()
    {
        if (GameDatabase.Instance == null || GameDatabase.Instance.root == null)
            throw new InvalidOperationException("The game database is not loaded yet");

        return new TopLevelAdapter(GameDatabase.Instance.root.AllConfigs);
    }

    /// <summary><see cref="ConfigNodeAdapter"/>, plus the <c>name</c> value that loading took off top-level nodes.</summary>
    private sealed class TopLevelAdapter : INodeAdapter<ConfigNode>
    {
        private readonly Dictionary<ConfigNode, string> missingNames =
            new Dictionary<ConfigNode, string>(ReferenceComparer.Instance);

        public TopLevelAdapter(IEnumerable<UrlDir.UrlConfig> configs)
        {
            var roots = new List<ConfigNode>();
            foreach (UrlDir.UrlConfig config in configs)
            {
                roots.Add(config.config);

                // A config without a name has its type as UrlConfig.name, which is no name at all
                if (!config.config.HasValue("name") && config.name != config.type)
                    missingNames[config.config] = config.name;
            }

            Roots = roots;
        }

        public List<ConfigNode> Roots { get; }

        public string GetName(ConfigNode node) => node.name;

        public int GetValueCount(ConfigNode node) => node.values.Count + (missingNames.ContainsKey(node) ? 1 : 0);

        public string GetValueKey(ConfigNode node, int index) =>
            Shift(node, ref index) ? "name" : node.values[index].name;

        public string GetValueText(ConfigNode node, int index) =>
            Shift(node, ref index) ? missingNames[node] : node.values[index].value;

        public int GetNodeCount(ConfigNode node) => node.nodes.Count;

        public ConfigNode GetNode(ConfigNode node, int index) => node.nodes[index];

        // True for the restored name, which comes first; otherwise moves the index past it
        private bool Shift(ConfigNode node, ref int index)
        {
            if (!missingNames.ContainsKey(node))
                return false;

            if (index == 0)
                return true;

            index--;
            return false;
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<ConfigNode>
    {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();

        public bool Equals(ConfigNode x, ConfigNode y) => ReferenceEquals(x, y);

        public int GetHashCode(ConfigNode node) => RuntimeHelpers.GetHashCode(node);
    }
}

/// <summary>Lets <see cref="ConfigQuery"/> run over KSP's <c>ConfigNode</c> in place.</summary>
public sealed class ConfigNodeAdapter : INodeAdapter<ConfigNode>
{
    public static readonly ConfigNodeAdapter Instance = new ConfigNodeAdapter();

    private ConfigNodeAdapter()
    {
    }

    public string GetName(ConfigNode node) => node.name;

    public int GetValueCount(ConfigNode node) => node.values.Count;

    public string GetValueKey(ConfigNode node, int index) => node.values[index].name;

    public string GetValueText(ConfigNode node, int index) => node.values[index].value;

    public int GetNodeCount(ConfigNode node) => node.nodes.Count;

    public ConfigNode GetNode(ConfigNode node, int index) => node.nodes[index];
}
