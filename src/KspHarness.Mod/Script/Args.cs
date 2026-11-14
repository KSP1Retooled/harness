using System;
using KspHarness.Mod.Values;

namespace KspHarness.Script;

/// <summary>Reads a snippet's arguments; generated code declares one local per captured variable with it.</summary>
public static class Args
{
    /// <summary>
    /// Converts argument <paramref name="index"/> to <typeparamref name="T"/>: numbers between numeric types, enums
    /// by name or value, vectors, arrays and lists, objects by member name, handles checked against the type.
    /// </summary>
    public static T Get<T>(object[] args, int index)
    {
        if (args == null || index < 0 || index >= args.Length)
            throw new ArgumentOutOfRangeException(nameof(index), $"The snippet has no argument {index}");

        try
        {
            return (T)ValueConverter.Convert(args[index], typeof(T));
        }
        catch (InvalidCastException e)
        {
            throw new InvalidCastException($"Argument {index}: {e.Message}", e);
        }
    }
}
