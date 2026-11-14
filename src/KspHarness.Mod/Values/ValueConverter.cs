using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using KspHarness.Script;

namespace KspHarness.Mod.Values;

/// <summary>Converts a decoded argument (see <see cref="ValueDecoder"/>) to the type a snippet declared for it.</summary>
internal static class ValueConverter
{
    public static object Convert(object value, Type target)
    {
        if (value is UnresolvedHandle unresolved)
            throw unresolved.Error;

        Type underlying = Nullable.GetUnderlyingType(target);
        if (value == null)
        {
            if (target.IsValueType && underlying == null)
                throw new InvalidCastException($"null cannot be converted to {target}");

            return null;
        }

        if (underlying != null)
            target = underlying;

        if (target == typeof(object))
            return Plain(value);

        if (target.IsInstanceOfType(value))
            return value;

        if (target.IsEnum)
            return ToEnum(value, target);

        if (value is TaggedEnum taggedEnum)
            value = (object)taggedEnum.Value ?? taggedEnum.Name;

        if (target == typeof(string))
            throw Mismatch(value, target);

        if (target == typeof(char) && value is string text && text.Length == 1)
            return text[0];

        if (target.IsPrimitive || target == typeof(decimal))
            return System.Convert.ChangeType(value, target, CultureInfo.InvariantCulture);

        if (target == typeof(GameTime) && value is double seconds)
            return GameTime.Seconds(seconds);

        if (target == typeof(byte[]) && value is object[] numbers)
            return ToArray(numbers, typeof(byte));

        string[] vectorFields = ValueEncoder.VectorFields(target) ?? FieldsOf(target);
        if (vectorFields != null && (value is TaggedVector || value is object[]))
            return ToVector(value, target, vectorFields);

        if (target.IsArray && value is object[] items)
            return ToArray(items, target.GetElementType());

        if (value is object[] listItems && TryListType(target, out Type listType, out Type itemType))
        {
            var list = (IList)Activator.CreateInstance(listType);
            foreach (object item in listItems)
                list.Add(Convert(item, itemType));

            return list;
        }

        if (value is Dictionary<string, object> members)
            return ToObject(members, target);

        throw Mismatch(value, target);
    }

    // For an argument declared as object: tags become the closest plain value
    private static object Plain(object value)
    {
        switch (value)
        {
            case TaggedEnum taggedEnum:
                return taggedEnum.Name;
            case TaggedVector vector:
                return vector.Components;
            default:
                return value;
        }
    }

    private static object ToEnum(object value, Type target)
    {
        switch (value)
        {
            case TaggedEnum taggedEnum:
                if (taggedEnum.Name != null && TryParseEnum(target, taggedEnum.Name, out object byName))
                    return byName;

                if (taggedEnum.Value.HasValue)
                    return Enum.ToObject(target, taggedEnum.Value.Value);

                break;
            case string name:
                if (TryParseEnum(target, name, out object parsed))
                    return parsed;

                break;
            case double number:
                return Enum.ToObject(target, (long)number);
        }

        throw Mismatch(value, target);
    }

    private static bool TryParseEnum(Type target, string name, out object result)
    {
        try
        {
            result = Enum.Parse(target, name, true);
            return true;
        }
        catch (ArgumentException)
        {
            result = null;
            return false;
        }
    }

    private static object ToVector(object value, Type target, string[] fields)
    {
        double[] components = value is TaggedVector vector ? vector.Components : ToDoubles((object[])value);
        if (components.Length > fields.Length)
            throw new InvalidCastException($"{components.Length} components do not fit {target}");

        object result = Activator.CreateInstance(target);
        for (int i = 0; i < components.Length; i++)
        {
            FieldInfo field = target.GetField(fields[i]);
            field.SetValue(result, System.Convert.ChangeType(components[i], field.FieldType, CultureInfo.InvariantCulture));
        }

        return result;
    }

    // Any struct with x/y[/z[/w]] or r/g/b[/a] fields takes a vector, as KSP and mods declare their own
    private static string[] FieldsOf(Type target)
    {
        if (!target.IsValueType)
            return null;

        foreach (string[] names in new[] { new[] { "x", "y", "z", "w" }, new[] { "r", "g", "b", "a" } })
        {
            int count = 0;
            while (count < names.Length && target.GetField(names[count]) != null)
                count++;

            if (count >= 2)
            {
                var found = new string[count];
                Array.Copy(names, found, count);
                return found;
            }
        }

        return null;
    }

    private static double[] ToDoubles(object[] items)
    {
        var numbers = new double[items.Length];
        for (int i = 0; i < items.Length; i++)
            numbers[i] = (double)Convert(items[i], typeof(double));

        return numbers;
    }

    private static Array ToArray(object[] items, Type elementType)
    {
        Array array = Array.CreateInstance(elementType, items.Length);
        for (int i = 0; i < items.Length; i++)
            array.SetValue(Convert(items[i], elementType), i);

        return array;
    }

    private static bool TryListType(Type target, out Type listType, out Type itemType)
    {
        listType = null;
        itemType = null;
        if (!target.IsGenericType)
            return false;

        Type definition = target.GetGenericTypeDefinition();
        bool acceptsList = definition == typeof(List<>)
            || definition == typeof(IList<>)
            || definition == typeof(ICollection<>)
            || definition == typeof(IEnumerable<>)
            || definition == typeof(IReadOnlyList<>)
            || definition == typeof(IReadOnlyCollection<>);
        if (!acceptsList)
            return false;

        itemType = target.GetGenericArguments()[0];
        listType = typeof(List<>).MakeGenericType(itemType);
        return true;
    }

    private static object ToObject(Dictionary<string, object> members, Type target)
    {
        if (TryDictionaryType(target, out Type dictionaryType, out Type valueType))
        {
            var dictionary = (IDictionary)Activator.CreateInstance(dictionaryType);
            foreach (KeyValuePair<string, object> member in members)
                dictionary[member.Key] = Convert(member.Value, valueType);

            return dictionary;
        }

        if (target.IsAbstract || target.IsInterface)
            throw Mismatch(members, target);

        object result = Activator.CreateInstance(target, true);
        foreach (KeyValuePair<string, object> member in members)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
            FieldInfo field = target.GetField(member.Key, Flags);
            if (field != null)
            {
                field.SetValue(result, Convert(member.Value, field.FieldType));
                continue;
            }

            PropertyInfo property = target.GetProperty(member.Key, Flags);
            if (property != null && property.CanWrite)
                property.SetValue(result, Convert(member.Value, property.PropertyType), null);
        }

        return result;
    }

    private static bool TryDictionaryType(Type target, out Type dictionaryType, out Type valueType)
    {
        dictionaryType = null;
        valueType = null;
        if (!target.IsGenericType)
            return false;

        Type definition = target.GetGenericTypeDefinition();
        Type[] arguments = target.GetGenericArguments();
        bool accepts = (definition == typeof(Dictionary<,>)
                || definition == typeof(IDictionary<,>)
                || definition == typeof(IReadOnlyDictionary<,>))
            && arguments[0] == typeof(string);
        if (!accepts)
            return false;

        valueType = arguments[1];
        dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType);
        return true;
    }

    private static InvalidCastException Mismatch(object value, Type target) =>
        new InvalidCastException($"A {Describe(value)} cannot be converted to {target}");

    private static string Describe(object value) => value switch
    {
        TaggedEnum taggedEnum => "enum " + taggedEnum.Type,
        TaggedVector vector => "vector " + vector.Type,
        Dictionary<string, object> => "JSON object",
        object[] => "JSON array",
        _ => value.GetType().Name,
    };
}
