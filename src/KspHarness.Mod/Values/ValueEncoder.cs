using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using KspHarness.Mod.Protocol;
using KspHarness.Mod.Runtime;
using KspHarness.Script;

namespace KspHarness.Mod.Values;

/// <summary>Turns results into the protocol's value encoding, which the driver's <c>GameValue</c> decodes.</summary>
internal static class ValueEncoder
{
    public const int MaxHandleText = 200;

    // Past this depth a value is more likely a cycle than data, so it goes back as a handle
    private const int MaxDepth = 16;

    private const double MaxExactInteger = 9007199254740992d;

    private static readonly string[] XyzwFields = { "x", "y", "z", "w" };
    private static readonly string[] RgbaFields = { "r", "g", "b", "a" };

    public static object Encode(object value, HandleRegistry handles) => Encode(value, handles, 0);

    public static object EncodeHandle(object value, HandleRegistry handles)
    {
        if (IsNull(value))
            return null;

        return Handle(value, handles);
    }

    private static object Encode(object value, HandleRegistry handles, int depth)
    {
        if (IsNull(value))
            return null;

        Type type = value.GetType();
        switch (value)
        {
            case bool or string:
                return value;
            case char c:
                return c.ToString();
            case double real:
                return Number(real);
            case float single:
                return float.IsNaN(single) || float.IsInfinity(single) ? Number(single) : single;
            case decimal exact:
                return exact;
            case long whole:
                return Math.Abs((double)whole) <= MaxExactInteger ? whole : (object)(double)whole;
            case ulong whole:
                return whole <= (ulong)MaxExactInteger ? whole : (object)(double)whole;
            case sbyte or byte or short or ushort or int or uint:
                return value;
            case Enum enumValue:
                return new JsonObject
                {
                    { "$", "enum" },
                    { "type", TypeName(type) },
                    { "name", enumValue.ToString() },
                    { "value", Convert.ToInt64(enumValue, CultureInfo.InvariantCulture) },
                };
            case byte[] bytes:
                return new JsonObject { { "$", "bytes" }, { "v", Convert.ToBase64String(bytes) } };
            case GameTime time:
                return new JsonObject { { "$", "gametime" }, { "seconds", Number(time.TotalSeconds) } };
        }

        if (value is UnityEngine.Object || value is Delegate || depth >= MaxDepth)
            return Handle(value, handles);

        string[] vectorFields = VectorFields(type);
        if (vectorFields != null)
            return Vector(value, type, vectorFields);

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
        {
            return Object(type, new JsonObject
            {
                { "Key", Encode(type.GetProperty("Key").GetValue(value, null), handles, depth + 1) },
                { "Value", Encode(type.GetProperty("Value").GetValue(value, null), handles, depth + 1) },
            });
        }

        if (IsValueTuple(type))
        {
            var fields = new JsonObject();
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                fields.Add(field.Name, Encode(field.GetValue(value), handles, depth + 1));

            return Object(type, fields);
        }

        if (value is IDictionary dictionary && HasStringKeys(type))
        {
            var fields = new JsonObject();
            foreach (DictionaryEntry entry in dictionary)
                fields.Add((string)entry.Key, Encode(entry.Value, handles, depth + 1));

            return Object(type, fields);
        }

        if (value is IList list)
        {
            var items = new List<object>(list.Count);
            foreach (object item in list)
                items.Add(Encode(item, handles, depth + 1));

            return items;
        }

        if (IsPlainData(type))
            return Object(type, Members(value, type, handles, depth));

        return Handle(value, handles);
    }

    /// <summary>The <c>{ "$": "handle" }</c> encoding, registering <paramref name="value"/>.</summary>
    public static JsonObject Handle(object value, HandleRegistry handles)
    {
        Type type = value.GetType();
        return new JsonObject
        {
            { "$", "handle" },
            { "id", handles.Add(value) },
            { "type", TypeName(type) },
            { "assembly", type.Assembly.GetName().Name },
            { "text", Describe(value) },
        };
    }

    /// <summary>Type names as <see cref="Type.ToString"/> writes them: <c>Ns.Outer+Inner</c>, <c>List`1[System.Int32]</c>.</summary>
    public static string TypeName(Type type) => type.ToString();

    public static object Number(double value)
    {
        if (double.IsNaN(value))
            return new JsonObject { { "$", "num" }, { "v", "NaN" } };

        if (double.IsPositiveInfinity(value))
            return new JsonObject { { "$", "num" }, { "v", "Infinity" } };

        if (double.IsNegativeInfinity(value))
            return new JsonObject { { "$", "num" }, { "v", "-Infinity" } };

        return value;
    }

    /// <summary>The fields a vector-like type is encoded from, or null if <paramref name="type"/> is not one.</summary>
    public static string[] VectorFields(Type type)
    {
        switch (type.FullName)
        {
            case "UnityEngine.Vector2":
            case "Vector2d":
                return new[] { "x", "y" };
            case "UnityEngine.Vector3":
            case "Vector3d":
                return new[] { "x", "y", "z" };
            case "UnityEngine.Vector4":
            case "UnityEngine.Quaternion":
            case "QuaternionD":
                return XyzwFields;
            case "UnityEngine.Color":
            case "UnityEngine.Color32":
                return RgbaFields;
            default:
                return null;
        }
    }

    private static bool IsNull(object value) =>
        value == null || (value is UnityEngine.Object unityObject && unityObject == null);

    private static JsonObject Vector(object value, Type type, string[] fieldNames)
    {
        var components = new List<object>(fieldNames.Length);
        foreach (string name in fieldNames)
        {
            object component = type.GetField(name).GetValue(value);
            components.Add(Number(Convert.ToDouble(component, CultureInfo.InvariantCulture)));
        }

        return new JsonObject { { "$", "vec" }, { "type", type.Name }, { "v", components } };
    }

    private static JsonObject Object(Type type, JsonObject fields) =>
        new JsonObject { { "$", "obj" }, { "type", TypeName(type) }, { "fields", fields } };

    private static JsonObject Members(object value, Type type, HandleRegistry handles, int depth)
    {
        var fields = new JsonObject();
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            fields.Add(field.Name, Encode(field.GetValue(value), handles, depth + 1));

        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;

            object member;
            try
            {
                member = property.GetValue(value, null);
            }
            catch (TargetInvocationException e)
            {
                member = "<" + e.InnerException?.GetType().Name + ">";
            }

            fields.Add(property.Name, Encode(member, handles, depth + 1));
        }

        return fields;
    }

    // Types the snippet itself declared, and the script API's own data types, travel by value
    private static bool IsPlainData(Type type)
    {
        if (type.IsDefined(typeof(CompilerGeneratedAttribute), false) && type.Name.Contains("AnonymousType"))
            return true;

        if (SnippetLoader.IsSnippetAssembly(type.Assembly))
            return true;

        // Of the script API only the data structs qualify; awaitables and contexts are live objects
        return type.IsValueType && type.Assembly == typeof(GameTime).Assembly && type.Namespace == "KspHarness.Script";
    }

    private static bool IsValueTuple(Type type) =>
        type.IsValueType && type.IsGenericType && type.FullName != null
        && type.FullName.StartsWith("System.ValueTuple`", StringComparison.Ordinal);

    private static bool HasStringKeys(Type type)
    {
        foreach (Type implemented in type.GetInterfaces())
        {
            if (implemented.IsGenericType
                && implemented.GetGenericTypeDefinition() == typeof(IDictionary<,>)
                && implemented.GetGenericArguments()[0] == typeof(string))
                return true;
        }

        return false;
    }

    private static string Describe(object value)
    {
        string text;
        try
        {
            text = value.ToString();
        }
        catch (Exception e)
        {
            text = $"<ToString threw {e.GetType().Name}>";
        }

        if (text == null)
            return "";

        return text.Length <= MaxHandleText ? text : text.Substring(0, MaxHandleText);
    }
}
