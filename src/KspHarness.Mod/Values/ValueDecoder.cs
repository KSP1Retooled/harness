using System;
using System.Collections.Generic;
using System.Globalization;
using KspHarness.Script;

namespace KspHarness.Mod.Values;

/// <summary>
/// Turns JSON arguments into <c>null</c>, <c>bool</c>, <c>double</c>, <c>string</c>, <c>object[]</c>,
/// <c>Dictionary&lt;string, object&gt;</c>, <c>byte[]</c>, <see cref="GameTime"/> and the objects behind handles.
/// Enums and vectors stay tagged until <see cref="ValueConverter"/> knows the type they are wanted as.
/// </summary>
internal static class ValueDecoder
{
    public static object[] DecodeArguments(object json, HandleRegistry handles)
    {
        if (json == null)
            return new object[0];

        if (!(json is List<object> list))
            throw new FormatException("args must be an array");

        var arguments = new object[list.Count];
        for (int i = 0; i < list.Count; i++)
            arguments[i] = Decode(list[i], handles);

        return arguments;
    }

    public static object Decode(object json, HandleRegistry handles)
    {
        switch (json)
        {
            case null:
            case bool:
            case string:
            case double:
                return json;
            case long whole:
                return (double)whole;
            case List<object> list:
                var items = new object[list.Count];
                for (int i = 0; i < list.Count; i++)
                    items[i] = Decode(list[i], handles);

                return items;
            case Dictionary<string, object> members:
                return DecodeObject(members, handles);
            default:
                throw new FormatException($"unexpected JSON value {json.GetType()}");
        }
    }

    private static object DecodeObject(Dictionary<string, object> members, HandleRegistry handles)
    {
        if (!members.TryGetValue("$", out object tagValue) || !(tagValue is string tag))
            return DecodeMembers(members, handles);

        switch (tag)
        {
            case "num":
                return double.Parse(Member<string>(members, "v"), NumberStyles.Float, CultureInfo.InvariantCulture);
            case "bytes":
                return Convert.FromBase64String(Member<string>(members, "v"));
            case "gametime":
                return GameTime.Seconds(Number(members, "seconds"));
            case "enum":
                members.TryGetValue("value", out object enumValue);
                return new TaggedEnum(
                    Member<string>(members, "type"),
                    Member<string>(members, "name"),
                    enumValue is long whole ? whole : (long?)null
                );
            case "vec":
                var components = Member<List<object>>(members, "v");
                var vector = new double[components.Count];
                for (int i = 0; i < components.Count; i++)
                    vector[i] = (double)Decode(components[i], handles);

                return new TaggedVector(Member<string>(members, "type"), vector);
            case "obj":
                return DecodeMembers(Member<Dictionary<string, object>>(members, "fields"), handles);
            case "handle":
                long id = Member<long>(members, "id");
                try
                {
                    return handles.Resolve(id);
                }
                catch (Exception e) when (e is ArgumentException || e is ObjectDisposedException)
                {
                    return new UnresolvedHandle(id, e);
                }
            default:
                throw new FormatException($"unknown value tag \"{tag}\"");
        }
    }

    private static Dictionary<string, object> DecodeMembers(Dictionary<string, object> members, HandleRegistry handles)
    {
        var decoded = new Dictionary<string, object>(members.Count);
        foreach (KeyValuePair<string, object> member in members)
            decoded[member.Key] = Decode(member.Value, handles);

        return decoded;
    }

    private static double Number(Dictionary<string, object> members, string name)
    {
        members.TryGetValue(name, out object value);
        return value switch
        {
            long whole => whole,
            double real => real,
            _ => throw new FormatException($"\"{name}\" must be a number"),
        };
    }

    private static T Member<T>(Dictionary<string, object> members, string name)
    {
        if (members.TryGetValue(name, out object value) && value is T typed)
            return typed;

        throw new FormatException($"a tagged value needs \"{name}\" of type {typeof(T).Name}");
    }
}

internal sealed class TaggedEnum
{
    public TaggedEnum(string type, string name, long? value)
    {
        Type = type;
        Name = name;
        Value = value;
    }

    public string Type { get; }

    public string Name { get; }

    public long? Value { get; }
}

internal sealed class TaggedVector
{
    public TaggedVector(string type, double[] components)
    {
        Type = type;
        Components = components;
    }

    public string Type { get; }

    public double[] Components { get; }
}
