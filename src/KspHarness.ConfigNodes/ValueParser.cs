using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace KspHarness.ConfigNodes;

/// <summary>Converts config value text to typed values, reading the text forms KSP writes.</summary>
/// <remarks>
/// <list type="bullet">
/// <item>Numbers use the invariant culture, as KSP writes them.</item>
/// <item><c>bool</c> accepts <c>True</c>/<c>False</c> in any case.</item>
/// <item>Enums go through <c>Enum.Parse</c> like KSP's <c>ConfigNode.ParseEnum</c>: names are case-sensitive and
/// flag combinations are comma lists.</item>
/// <item>Arrays and lists of numbers or bools split on commas, spaces and tabs, as KSP's vector parsers do
/// (<c>"1, 2, 3"</c> is a <c>double[]</c> or <c>float[]</c> of three). Arrays and lists of strings split on commas
/// only and trim each item.</item>
/// <item><see cref="CfgColor"/> takes three or four components, like KSP's <c>ParseColor</c>.</item>
/// </list>
/// </remarks>
public static class ValueParser
{
    private static readonly char[] VectorSeparators = { ',', ' ', '\t' };

    private const NumberStyles IntegerStyle = NumberStyles.Integer;

    private const NumberStyles FloatStyle = NumberStyles.Float | NumberStyles.AllowThousands;

    public static T Parse<T>(string text)
    {
        return (T)Parse(text, typeof(T));
    }

    public static bool TryParse<T>(string text, out T result)
    {
        if (TryParse(text, typeof(T), out var value))
        {
            result = (T)value!;
            return true;
        }

        result = default!;
        return false;
    }

    public static object Parse(string text, Type type)
    {
        if (TryParse(text, type, out var value))
            return value!;

        throw new FormatException($"'{text}' is not a valid {type.Name}");
    }

    public static bool TryParse(string text, Type type, out object? result)
    {
        if (text == null)
            throw new ArgumentNullException(nameof(text));

        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
            type = underlying;

        if (type.IsArray)
            return TryParseArray(text, type.GetElementType()!, out result);

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            return TryParseList(text, type, out result);

        return TryParseScalar(text, type, out result);
    }

    private static bool TryParseScalar(string text, Type type, out object? result)
    {
        result = null;
        var invariant = CultureInfo.InvariantCulture;

        if (type == typeof(string))
        {
            result = text;
            return true;
        }

        if (type.IsEnum)
            return TryParseEnum(text, type, out result);

        if (type == typeof(CfgColor))
        {
            if (!TryParseColor(text, out var color))
                return false;

            result = color;
            return true;
        }

        if (type == typeof(Guid))
        {
            if (!Guid.TryParse(text, out var guid))
                return false;

            result = guid;
            return true;
        }

        switch (Type.GetTypeCode(type))
        {
            case TypeCode.Boolean:
                return Box(bool.TryParse(text, out var boolean), boolean, out result);
            case TypeCode.Int32:
                return Box(int.TryParse(text, IntegerStyle, invariant, out var int32), int32, out result);
            case TypeCode.Int64:
                return Box(long.TryParse(text, IntegerStyle, invariant, out var int64), int64, out result);
            case TypeCode.UInt32:
                return Box(uint.TryParse(text, IntegerStyle, invariant, out var uint32), uint32, out result);
            case TypeCode.UInt64:
                return Box(ulong.TryParse(text, IntegerStyle, invariant, out var uint64), uint64, out result);
            case TypeCode.Int16:
                return Box(short.TryParse(text, IntegerStyle, invariant, out var int16), int16, out result);
            case TypeCode.UInt16:
                return Box(ushort.TryParse(text, IntegerStyle, invariant, out var uint16), uint16, out result);
            case TypeCode.Byte:
                return Box(byte.TryParse(text, IntegerStyle, invariant, out var uint8), uint8, out result);
            case TypeCode.SByte:
                return Box(sbyte.TryParse(text, IntegerStyle, invariant, out var int8), int8, out result);
            case TypeCode.Single:
                return Box(float.TryParse(text, FloatStyle, invariant, out var single), single, out result);
            case TypeCode.Double:
                return Box(double.TryParse(text, FloatStyle, invariant, out var real), real, out result);
            case TypeCode.Decimal:
                return Box(decimal.TryParse(text, FloatStyle, invariant, out var money), money, out result);
            default:
                throw new NotSupportedException($"config values cannot be read as {type.FullName}");
        }
    }

    private static bool Box<T>(bool parsed, T value, out object? result)
    {
        result = parsed ? value : null;
        return parsed;
    }

    private static bool TryParseEnum(string text, Type type, out object? result)
    {
        // netstandard2.0 has no non-generic Enum.TryParse; Enum.Parse is what KSP itself calls.
        try
        {
            result = Enum.Parse(type, text);
            return true;
        }
        catch (ArgumentException)
        {
        }
        catch (OverflowException)
        {
        }

        result = null;
        return false;
    }

    private static bool TryParseColor(string text, out CfgColor color)
    {
        color = default;
        var parts = text.Split(VectorSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts.Length > 4)
            return false;

        var components = new float[] { 0f, 0f, 0f, 1f };
        for (var i = 0; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], FloatStyle, CultureInfo.InvariantCulture, out components[i]))
                return false;
        }

        color = new CfgColor(components[0], components[1], components[2], components[3]);
        return true;
    }

    private static bool TryParseArray(string text, Type elementType, out object? result)
    {
        result = null;
        var items = SplitItems(text, elementType);
        var array = Array.CreateInstance(elementType, items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            if (!TryParseScalar(items[i], elementType, out var item))
                return false;

            array.SetValue(item, i);
        }

        result = array;
        return true;
    }

    private static bool TryParseList(string text, Type listType, out object? result)
    {
        result = null;
        var elementType = listType.GetGenericArguments()[0];
        var items = SplitItems(text, elementType);
        var list = (IList)Activator.CreateInstance(listType)!;
        foreach (var itemText in items)
        {
            if (!TryParseScalar(itemText, elementType, out var item))
                return false;

            list.Add(item);
        }

        result = list;
        return true;
    }

    private static string[] SplitItems(string text, Type elementType)
    {
        if (elementType == typeof(string))
        {
            if (text.Trim().Length == 0)
                return new string[0];

            var items = text.Split(',');
            for (var i = 0; i < items.Length; i++)
                items[i] = items[i].Trim();

            return items;
        }

        return text.Split(VectorSeparators, StringSplitOptions.RemoveEmptyEntries);
    }
}
