using System;
using System.Collections.Generic;

namespace KspHarness.ConfigNodes;

/// <summary>
/// A config node as KSP's <c>ConfigNode</c> holds it: a name, ordered values (keys may repeat) and ordered child
/// nodes. Comments are not kept.
/// </summary>
public sealed class CfgNode
{
    public CfgNode()
        : this(string.Empty)
    {
    }

    public CfgNode(string name)
    {
        Name = name ?? string.Empty;
    }

    public string Name { get; set; }

    public List<CfgValue> ValueList { get; } = new List<CfgValue>();

    public List<CfgNode> Nodes { get; } = new List<CfgNode>();

    /// <summary>The first value of the key <c>name</c>, which ModuleManager's <c>NODE[pattern]</c> matches.</summary>
    public string? NameValue => GetValue("name");

    public bool HasValue(string key)
    {
        return IndexOfValue(key) >= 0;
    }

    public bool HasNode(string name)
    {
        return GetNode(name) != null;
    }

    /// <summary>The first value with this key, or null.</summary>
    public string? GetValue(string key)
    {
        var index = IndexOfValue(key);
        return index >= 0 ? ValueList[index].Value : null;
    }

    public List<string> GetValues(string key)
    {
        var result = new List<string>();
        foreach (var value in ValueList)
        {
            if (value.Key == key)
                result.Add(value.Value);
        }

        return result;
    }

    public CfgNode? GetNode(string name)
    {
        foreach (var node in Nodes)
        {
            if (node.Name == name)
                return node;
        }

        return null;
    }

    public List<CfgNode> GetNodes(string name)
    {
        var result = new List<CfgNode>();
        foreach (var node in Nodes)
        {
            if (node.Name == name)
                result.Add(node);
        }

        return result;
    }

    public CfgNode AddValue(string key, string value)
    {
        ValueList.Add(new CfgValue(key, value));
        return this;
    }

    /// <summary>Replaces the first value with this key, or adds one when there is none.</summary>
    public CfgNode SetValue(string key, string value)
    {
        var index = IndexOfValue(key);
        if (index >= 0)
            ValueList[index].Value = value;
        else
            ValueList.Add(new CfgValue(key, value));

        return this;
    }

    public CfgNode AddNode(string name)
    {
        var node = new CfgNode(name);
        Nodes.Add(node);
        return node;
    }

    public CfgNode AddNode(CfgNode node)
    {
        Nodes.Add(node);
        return node;
    }

    /// <summary>The first value with this key, converted. Throws when the key is missing or does not parse.</summary>
    public T Value<T>(string key)
    {
        var text = GetValue(key);
        if (text == null)
            throw new KeyNotFoundException($"node '{Name}' has no value '{key}'");

        return ValueParser.Parse<T>(text);
    }

    /// <summary>The first value with this key, converted, or <paramref name="fallback"/> when it is missing or does not parse.</summary>
    public T ValueOr<T>(string key, T fallback)
    {
        return TryGetValue(key, out T result) ? result : fallback;
    }

    public bool TryGetValue<T>(string key, out T result)
    {
        var text = GetValue(key);
        if (text != null)
            return ValueParser.TryParse(text, out result);

        result = default!;
        return false;
    }

    /// <summary>Every value with this key, converted. Throws when one does not parse.</summary>
    public List<T> Values<T>(string key)
    {
        var result = new List<T>();
        foreach (var value in ValueList)
        {
            if (value.Key == key)
                result.Add(ValueParser.Parse<T>(value.Value));
        }

        return result;
    }

    /// <summary>The nodes a query selects, searching this node's children. See <see cref="ConfigQuery"/>.</summary>
    public List<CfgNode> Select(string query)
    {
        return ConfigQuery.Parse(query).SelectNodes(this, CfgNodeAdapter.Instance);
    }

    /// <summary>The first node a query selects, or null.</summary>
    public CfgNode? SelectFirst(string query)
    {
        var nodes = Select(query);
        return nodes.Count > 0 ? nodes[0] : null;
    }

    /// <summary>The values a query selects: its last segment is a key. See <see cref="ConfigQuery"/>.</summary>
    public List<ConfigValue> SelectValues(string query)
    {
        return ConfigValue.From(ConfigQuery.Parse(query).SelectValues(this, CfgNodeAdapter.Instance));
    }

    public CfgNode DeepCopy()
    {
        var copy = new CfgNode(Name);
        foreach (var value in ValueList)
            copy.ValueList.Add(new CfgValue(value.Key, value.Value));

        foreach (var node in Nodes)
            copy.Nodes.Add(node.DeepCopy());

        return copy;
    }

    /// <summary>The node in KSP's format, with its name and braces.</summary>
    public override string ToString()
    {
        return ConfigWriter.WriteNode(this);
    }

    private int IndexOfValue(string key)
    {
        for (var i = 0; i < ValueList.Count; i++)
        {
            if (ValueList[i].Key == key)
                return i;
        }

        return -1;
    }
}
