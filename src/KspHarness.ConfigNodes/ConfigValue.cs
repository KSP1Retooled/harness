using System.Collections.Generic;

namespace KspHarness.ConfigNodes;

/// <summary>A value a query selected: its key, its text and the node that holds it.</summary>
public readonly struct ConfigValue<TNode>
{
    public ConfigValue(TNode node, int index, string key, string value)
    {
        Node = node;
        Index = index;
        Key = key;
        Value = value;
    }

    public TNode Node { get; }

    /// <summary>The position of the value among all of the node's values.</summary>
    public int Index { get; }

    public string Key { get; }

    public string Value { get; }

    public T As<T>()
    {
        return ValueParser.Parse<T>(Value);
    }

    public override string ToString()
    {
        return Key + " = " + Value;
    }
}

/// <summary>A value a query selected from a <see cref="CfgNode"/> tree.</summary>
public sealed class ConfigValue
{
    public ConfigValue(CfgNode node, int index)
    {
        Node = node;
        Index = index;
    }

    public CfgNode Node { get; }

    /// <summary>The position of the value in <see cref="CfgNode.ValueList"/>.</summary>
    public int Index { get; }

    public string Key => Node.ValueList[Index].Key;

    public string Value => Node.ValueList[Index].Value;

    public T As<T>()
    {
        return ValueParser.Parse<T>(Value);
    }

    public override string ToString()
    {
        return Key + " = " + Value;
    }

    internal static List<ConfigValue> From(List<ConfigValue<CfgNode>> values)
    {
        var result = new List<ConfigValue>(values.Count);
        foreach (var value in values)
            result.Add(new ConfigValue(value.Node, value.Index));

        return result;
    }
}
