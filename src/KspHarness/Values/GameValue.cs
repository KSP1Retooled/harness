using System.Collections;
using System.Dynamic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using KspHarness.Script;

namespace KspHarness;

/// <summary>A value the game sent, in the wire encoding, convertible to a requested type.</summary>
public sealed class GameValue
{
    private readonly GameSession? session;

    internal GameValue(JsonNode? json, GameSession? session)
    {
        Json = json;
        this.session = session;
    }

    public JsonNode? Json { get; }

    public T As<T>() => (T)As(typeof(T))!;

    public object? As(Type type) => Convert(Json, type, session);

    /// <summary>
    /// Primitives, typed arrays, an <see cref="ExpandoObject"/> for objects, <see cref="Handle"/> for handles, the
    /// enum name for enums, and a <c>double[]</c> for vectors.
    /// </summary>
    public dynamic? ToDynamic() => ToDynamic(Json, session);

    public override string ToString() => Json?.ToJsonString() ?? "null";

    internal static object? Convert(JsonNode? node, Type type, GameSession? session)
    {
        if (type == typeof(GameValue))
        {
            return new GameValue(node, session);
        }

        if (type == typeof(object))
        {
            return ToDynamic(node, session);
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return node is null ? null : Convert(node, underlying, session);
        }

        switch (node)
        {
            case null:
                if (type.IsValueType)
                {
                    throw new InvalidCastException($"the game returned null, which is not a {type}");
                }

                return null;
            case JsonValue value:
                return ConvertScalar(value, type);
            case JsonArray array:
                return ConvertArray(array, type, session);
            case JsonObject obj:
                return ConvertTagged(obj, type, session);
            default:
                throw new InvalidCastException($"unexpected value {node.ToJsonString()}");
        }
    }

    private static object? ConvertScalar(JsonValue value, Type type)
    {
        var element = ElementOf(value);
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
            {
                var text = element.GetString()!;
                if (type == typeof(string))
                {
                    return text;
                }

                if (type == typeof(char) && text.Length == 1)
                {
                    return text[0];
                }

                if (type.IsEnum)
                {
                    return Enum.Parse(type, text);
                }

                break;
            }
            case JsonValueKind.True:
            case JsonValueKind.False:
                if (type == typeof(bool))
                {
                    return element.GetBoolean();
                }

                break;
            case JsonValueKind.Number:
                if (type.IsEnum)
                {
                    return Enum.ToObject(type, element.GetInt64());
                }

                if (type == typeof(GameTime))
                {
                    return GameTime.Seconds(element.GetDouble());
                }

                if (IsNumeric(type))
                {
                    return ConvertNumber(element, type);
                }

                if (type == typeof(string))
                {
                    return element.GetRawText();
                }

                break;
        }

        throw new InvalidCastException($"the game returned {element.GetRawText()}, which is not a {type}");
    }

    private static object ConvertNumber(JsonElement element, Type type)
    {
        if (type == typeof(double))
        {
            return element.GetDouble();
        }

        if (type == typeof(float))
        {
            return element.GetSingle();
        }

        if (type == typeof(decimal))
        {
            return element.GetDecimal();
        }

        if (element.TryGetInt64(out var integer))
        {
            return System.Convert.ChangeType(integer, type, CultureInfo.InvariantCulture);
        }

        if (type == typeof(ulong) && element.TryGetUInt64(out var big))
        {
            return big;
        }

        // A whole double such as 3.0 still converts to an int, as a cast would.
        var d = element.GetDouble();
        if (Math.Floor(d) != d)
        {
            throw new InvalidCastException($"the game returned {d}, which is not a whole {type.Name}");
        }

        return System.Convert.ChangeType(d, type, CultureInfo.InvariantCulture);
    }

    private static bool IsNumeric(Type type)
    {
        return type == typeof(double) || type == typeof(float) || type == typeof(decimal)
            || Values.ValueEncoder.IsInteger(type);
    }

    private static object ConvertArray(JsonArray array, Type type, GameSession? session)
    {
        var elementType = ElementType(type)
            ?? throw new InvalidCastException($"the game returned an array, which is not a {type}");

        if (type.IsArray || type.IsInterface)
        {
            var result = Array.CreateInstance(elementType, array.Count);
            for (var i = 0; i < array.Count; i++)
            {
                result.SetValue(Convert(array[i], elementType, session), i);
            }

            if (type.IsArray || type.IsAssignableFrom(result.GetType()))
            {
                return result;
            }
        }

        var list = (IList)Activator.CreateInstance(type.IsInterface ? typeof(List<>).MakeGenericType(elementType) : type)!;
        foreach (var item in array)
        {
            list.Add(Convert(item, elementType, session));
        }

        return list;
    }

    private static Type? ElementType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(IEnumerable<>)
                || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>)
                || definition == typeof(ICollection<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        return null;
    }

    private static object? ConvertTagged(JsonObject obj, Type type, GameSession? session)
    {
        var tag = obj["$"]?.GetValue<string>();
        switch (tag)
        {
            case "num":
                return ConvertSpecialNumber(obj, type);
            case "enum":
                return ConvertEnum(obj, type);
            case "vec":
                return ConvertVector(obj, type);
            case "bytes":
                if (type == typeof(byte[]))
                {
                    return System.Convert.FromBase64String(obj["v"]!.GetValue<string>());
                }

                break;
            case "gametime":
                if (type == typeof(GameTime))
                {
                    return GameTime.Seconds(obj["seconds"]!.GetValue<double>());
                }

                if (type == typeof(double))
                {
                    return obj["seconds"]!.GetValue<double>();
                }

                break;
            case "handle":
                if (type == typeof(Handle))
                {
                    return ToHandle(obj, session);
                }

                throw new InvalidCastException(
                    $"the game returned a {obj["type"]}, which stays in the game and can't become a {type}. "
                    + "Return the values you need from the snippet, or ask for a Handle.");
            case "obj":
                return ConvertObject(obj["fields"] as JsonObject ?? [], type, session);
            case null:
                return ConvertObject(obj, type, session);
        }

        throw new InvalidCastException($"the game returned a {tag} value, which is not a {type}");
    }

    private static object ConvertSpecialNumber(JsonObject obj, Type type)
    {
        var d = obj["v"]!.GetValue<string>() switch
        {
            "NaN" => double.NaN,
            "Infinity" => double.PositiveInfinity,
            "-Infinity" => double.NegativeInfinity,
            var other => throw new InvalidCastException($"unknown number {other}"),
        };

        if (type == typeof(double))
        {
            return d;
        }

        if (type == typeof(float))
        {
            return (float)d;
        }

        throw new InvalidCastException($"the game returned {d}, which is not a {type}");
    }

    private static object ConvertEnum(JsonObject obj, Type type)
    {
        var name = obj["name"]!.GetValue<string>();
        var number = obj["value"]!.GetValue<long>();
        if (type.IsEnum)
        {
            return Enum.Parse(type, name);
        }

        if (type == typeof(string))
        {
            return name;
        }

        if (IsNumeric(type))
        {
            return System.Convert.ChangeType(number, type, CultureInfo.InvariantCulture);
        }

        throw new InvalidCastException($"the game returned the enum {obj["type"]}.{name}, which is not a {type}");
    }

    private static object ConvertVector(JsonObject obj, Type type)
    {
        var components = obj["v"]!.AsArray().Select(ReadDouble).ToArray();
        if (type == typeof(double[]))
        {
            return components;
        }

        if (type == typeof(float[]))
        {
            return components.Select(c => (float)c).ToArray();
        }

        var instance = Activator.CreateInstance(type)!;
        string[] names = obj["type"]?.GetValue<string>()?.StartsWith("Color", StringComparison.Ordinal) == true
            ? ["r", "g", "b", "a"]
            : ["x", "y", "z", "w"];

        for (var i = 0; i < components.Length; i++)
        {
            var member = FindMember(type, names[i])
                ?? throw new InvalidCastException($"the game returned a {obj["type"]}, but {type} has no '{names[i]}'");
            SetMember(instance, member, ConvertDoubleTo(components[i], MemberType(member)));
        }

        return instance;
    }

    private static object? ConvertObject(JsonObject fields, Type type, GameSession? session)
    {
        if (type == typeof(ExpandoObject))
        {
            return ToDynamic(fields, session);
        }

        if (DictionaryValueType(type) is { } valueType)
        {
            var dictionaryType = type.IsInterface ? typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType) : type;
            var dictionary = (IDictionary)Activator.CreateInstance(dictionaryType)!;
            foreach (var (key, value) in fields)
            {
                dictionary[key] = Convert(value, valueType, session);
            }

            return dictionary;
        }

        var lookup = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in fields)
        {
            lookup[key] = value;
        }

        // Records and tuples are built through their widest constructor; other members are set afterwards.
        var constructor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault(c => c.GetParameters().All(p => lookup.ContainsKey(p.Name!)));

        object instance;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (constructor is not null && constructor.GetParameters().Length > 0)
        {
            var parameters = constructor.GetParameters();
            var arguments = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                arguments[i] = Convert(lookup[parameters[i].Name!], parameters[i].ParameterType, session);
                used.Add(parameters[i].Name!);
            }

            instance = constructor.Invoke(arguments);
        }
        else if (type.IsValueType || type.GetConstructor(Type.EmptyTypes) is not null)
        {
            instance = Activator.CreateInstance(type)!;
        }
        else
        {
            throw new InvalidCastException($"can't build a {type} from the game's value: no matching constructor");
        }

        foreach (var (key, value) in lookup)
        {
            if (used.Contains(key))
            {
                continue;
            }

            var member = FindMember(type, key);
            if (member is null || !IsWritable(member))
            {
                continue;
            }

            SetMember(instance, member, Convert(value, MemberType(member), session));
        }

        return instance;
    }

    private static Type? DictionaryValueType(Type type)
    {
        if (!type.IsGenericType)
        {
            return null;
        }

        var definition = type.GetGenericTypeDefinition();
        var arguments = type.GetGenericArguments();
        if ((definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>)
                || definition == typeof(IReadOnlyDictionary<,>)) && arguments[0] == typeof(string))
        {
            return arguments[1];
        }

        return null;
    }

    private static MemberInfo? FindMember(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
        return (MemberInfo?)type.GetField(name, flags) ?? type.GetProperty(name, flags);
    }

    private static bool IsWritable(MemberInfo member)
    {
        return member is FieldInfo { IsInitOnly: false } || member is PropertyInfo { CanWrite: true };
    }

    private static Type MemberType(MemberInfo member)
    {
        return member is FieldInfo field ? field.FieldType : ((PropertyInfo)member).PropertyType;
    }

    private static void SetMember(object instance, MemberInfo member, object? value)
    {
        if (member is FieldInfo field)
        {
            field.SetValue(instance, value);
        }
        else
        {
            ((PropertyInfo)member).SetValue(instance, value);
        }
    }

    private static object ConvertDoubleTo(double value, Type type)
    {
        return type == typeof(double) ? value : System.Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
    }

    private static double ReadDouble(JsonNode? node)
    {
        return node switch
        {
            JsonObject special => (double)ConvertSpecialNumber(special, typeof(double)),
            _ => node!.GetValue<double>(),
        };
    }

    private static Handle ToHandle(JsonObject obj, GameSession? session)
    {
        return new Handle(
            obj["id"]!.GetValue<long>(),
            obj["type"]?.GetValue<string>() ?? "System.Object",
            obj["assembly"]?.GetValue<string>(),
            obj["text"]?.GetValue<string>(),
            session);
    }

    internal static object? ToDynamic(JsonNode? node, GameSession? session)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonValue value:
            {
                var element = ElementOf(value);
                return element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => DynamicNumber(element),
                    _ => throw new InvalidCastException($"unexpected value {element.GetRawText()}"),
                };
            }
            case JsonArray array:
            {
                var items = array.Select(item => ToDynamic(item, session)).ToArray();
                if (items.Length > 0 && items.All(item => item is int or long or double))
                {
                    return items.Select(item => System.Convert.ToDouble(item, CultureInfo.InvariantCulture)).ToArray();
                }

                if (items.Length > 0 && items.All(item => item is string))
                {
                    return items.Cast<string>().ToArray();
                }

                return items;
            }
            case JsonObject obj:
                return obj["$"]?.GetValue<string>() switch
                {
                    "num" => ConvertSpecialNumber(obj, typeof(double)),
                    "enum" => obj["name"]!.GetValue<string>(),
                    "vec" => ConvertVector(obj, typeof(double[])),
                    "bytes" => System.Convert.FromBase64String(obj["v"]!.GetValue<string>()),
                    "gametime" => GameTime.Seconds(obj["seconds"]!.GetValue<double>()),
                    "handle" => ToHandle(obj, session),
                    "obj" => ToExpando(obj["fields"] as JsonObject ?? [], session),
                    null => ToExpando(obj, session),
                    var tag => throw new InvalidCastException($"unknown value tag {tag}"),
                };
            default:
                throw new InvalidCastException($"unexpected value {node.ToJsonString()}");
        }
    }

    // A number written without a decimal point is an integer in the game (see the mod's Json.AppendReal), so it comes back
    // as one and integer arithmetic on a dynamic result behaves as it would in the game.
    private static object DynamicNumber(JsonElement element)
    {
        var raw = element.GetRawText();
        var isInteger = raw.IndexOfAny(['.', 'e', 'E']) < 0;
        if (isInteger && element.TryGetInt32(out var small))
        {
            return small;
        }

        if (isInteger && element.TryGetInt64(out var large))
        {
            return large;
        }

        return element.GetDouble();
    }

    // Parsed values carry a JsonElement; values built in code (JsonValue.Create) do not.
    private static JsonElement ElementOf(JsonValue value)
    {
        return value.TryGetValue<JsonElement>(out var element) ? element : JsonSerializer.SerializeToElement(value);
    }

    private static ExpandoObject ToExpando(JsonObject fields, GameSession? session)
    {
        var expando = new ExpandoObject();
        var members = (IDictionary<string, object?>)expando;
        foreach (var (key, value) in fields)
        {
            members[key] = ToDynamic(value, session);
        }

        return expando;
    }
}
