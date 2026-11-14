namespace KspHarness.ConfigNodes;

/// <summary>
/// Read access to some other config node type, so that <see cref="ConfigQuery"/> can run over it in place. The mod
/// implements this for KSP's <c>ConfigNode</c> (over <c>name</c>, <c>values[i].name</c>, <c>values[i].value</c> and
/// <c>nodes[i]</c>) to query the live game database without copying it.
/// </summary>
public interface INodeAdapter<TNode>
{
    string GetName(TNode node);

    int GetValueCount(TNode node);

    string GetValueKey(TNode node, int index);

    string GetValueText(TNode node, int index);

    int GetNodeCount(TNode node);

    TNode GetNode(TNode node, int index);
}

/// <summary>The adapter for this library's own <see cref="CfgNode"/>.</summary>
public sealed class CfgNodeAdapter : INodeAdapter<CfgNode>
{
    public static readonly CfgNodeAdapter Instance = new CfgNodeAdapter();

    private CfgNodeAdapter() { }

    public string GetName(CfgNode node)
    {
        return node.Name;
    }

    public int GetValueCount(CfgNode node)
    {
        return node.ValueList.Count;
    }

    public string GetValueKey(CfgNode node, int index)
    {
        return node.ValueList[index].Key;
    }

    public string GetValueText(CfgNode node, int index)
    {
        return node.ValueList[index].Value;
    }

    public int GetNodeCount(CfgNode node)
    {
        return node.Nodes.Count;
    }

    public CfgNode GetNode(CfgNode node, int index)
    {
        return node.Nodes[index];
    }
}
