namespace KspHarness.ConfigNodes;

/// <summary>One <c>key = value</c> line of a config node. Keys may repeat within a node.</summary>
public sealed class CfgValue
{
    public CfgValue(string key, string value)
    {
        Key = key ?? string.Empty;
        Value = value ?? string.Empty;
    }

    public string Key { get; set; }

    public string Value { get; set; }

    public T As<T>()
    {
        return ValueParser.Parse<T>(Value);
    }

    public override string ToString()
    {
        return Key + " = " + Value;
    }
}
