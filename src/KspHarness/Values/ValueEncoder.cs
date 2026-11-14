using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using KspHarness.Script;

namespace KspHarness.Values;

/// <summary>Turns driver-side values into the wire encoding that the mod's <c>ValueDecoder</c> reads.</summary>
internal static class ValueEncoder
{
    // Doubles carry integers exactly up to 2^53; beyond that the game would read a different number.
    private const long MaxExactInteger = 1L << 53;

    internal static readonly HashSet<string> VectorTypeNames =
    [
        "Vector2", "Vector3", "Vector4", "Vector2d", "Vector3d", "Quaternion", "QuaternionD", "Color", "Color32",
    ];

    public static JsonNode? Encode(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case bool b:
                return JsonValue.Create(b);
            case string s:
                return JsonValue.Create(s);
            case char c:
                return JsonValue.Create(c.ToString());
            case byte[] bytes:
                return Tagged("bytes", new JsonObject { ["v"] = Convert.ToBase64String(bytes) });
            case GameTime time:
                return Tagged("gametime", new JsonObject { ["seconds"] = EncodeDouble(time.TotalSeconds) });
            case Handle handle:
                return Tagged("handle", new JsonObject
                {
                    ["id"] = handle.Id,
                    ["type"] = handle.Type,
                    ["assembly"] = handle.Assembly,
                });
            case Enum e:
                return EncodeEnum(e);
            case double d:
                return EncodeDouble(d);
            case float f:
                return EncodeDouble(f);
            case decimal m:
                return JsonValue.Create(m);
        }

        var type = value.GetType();
        if (IsInteger(type))
        {
            return EncodeInteger(value);
        }

        if (VectorTypeNames.Contains(type.Name) && TryEncodeVector(value, type) is { } vector)
        {
            return vector;
        }

        if (value is ITuple tuple)
        {
            var fields = new JsonObject();
            for (var i = 0; i < tuple.Length; i++)
            {
                fields["Item" + (i + 1)] = Encode(tuple[i]);
            }

            return Obj(type, fields);
        }

        if (value is IDictionary dictionary)
        {
            var fields = new JsonObject();
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string key)
                {
                    throw new ArgumentException($"only dictionaries with string keys can be sent to the game, not {type}");
                }

                fields[key] = Encode(entry.Value);
            }

            return Obj(type, fields);
        }

        if (value is IEnumerable items)
        {
            var array = new JsonArray();
            foreach (var item in items)
            {
                array.Add(Encode(item));
            }

            return array;
        }

        throw new ArgumentException(
            $"a {type} can't be sent to the game. Send numbers, strings, enums, vectors, arrays, tuples, "
            + "GameTime or handles, and build other objects inside the snippet.");
    }

    public static bool IsInteger(Type type)
    {
        return type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(sbyte)
            || type == typeof(byte) || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong);
    }

    /// <summary>Whether <see cref="Encode"/> can send a value of this static type.</summary>
    public static bool IsEncodable(Type type)
    {
        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type.IsEnum
            || type == typeof(GameTime) || type == typeof(Handle) || type == typeof(byte[]))
        {
            return true;
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return IsEncodable(underlying);
        }

        if (type.IsArray)
        {
            return IsEncodable(type.GetElementType()!);
        }

        return VectorTypeNames.Contains(type.Name) || typeof(ITuple).IsAssignableFrom(type);
    }

    private static JsonNode EncodeInteger(object value)
    {
        if (value is ulong big)
        {
            return big <= MaxExactInteger ? JsonValue.Create((long)big) : JsonValue.Create(big);
        }

        return JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }

    internal static JsonNode EncodeDouble(double d)
    {
        if (double.IsNaN(d))
        {
            return Tagged("num", new JsonObject { ["v"] = "NaN" });
        }

        if (double.IsInfinity(d))
        {
            return Tagged("num", new JsonObject { ["v"] = d > 0 ? "Infinity" : "-Infinity" });
        }

        return JsonValue.Create(d);
    }

    private static JsonObject EncodeEnum(Enum e)
    {
        var type = e.GetType();
        return Tagged("enum", new JsonObject
        {
            ["type"] = type.FullName,
            ["name"] = e.ToString(),
            ["value"] = Convert.ToInt64(e, CultureInfo.InvariantCulture),
        });
    }

    private static JsonObject? TryEncodeVector(object value, Type type)
    {
        string[] names = type.Name.StartsWith("Color", StringComparison.Ordinal)
            ? ["r", "g", "b", "a"]
            : ["x", "y", "z", "w"];

        var components = new JsonArray();
        foreach (var name in names)
        {
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field is null)
            {
                break;
            }

            components.Add(EncodeDouble(Convert.ToDouble(field.GetValue(value), CultureInfo.InvariantCulture)));
        }

        if (components.Count < 2)
        {
            return null;
        }

        return Tagged("vec", new JsonObject { ["type"] = type.Name, ["v"] = components });
    }

    private static JsonObject Obj(Type type, JsonObject fields)
    {
        // Generic arguments would name the driver's runtime assemblies, which mean nothing in the game.
        var name = type.IsGenericType ? type.GetGenericTypeDefinition().FullName : type.FullName;
        return Tagged("obj", new JsonObject { ["type"] = name, ["fields"] = fields });
    }

    private static JsonObject Tagged(string tag, JsonObject body)
    {
        var result = new JsonObject { ["$"] = tag };
        foreach (var (key, node) in body.ToList())
        {
            body.Remove(key);
            result[key] = node;
        }

        return result;
    }
}
