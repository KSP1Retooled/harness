using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KspHarness.Mod.Protocol;

/// <summary>
/// A JSON object whose members are written in the order they were added. Values are null, bool, string, any .NET
/// number, <see cref="JsonObject"/>, <see cref="IDictionary{TKey,TValue}"/> with string keys, or a list.
/// </summary>
internal sealed class JsonObject : IEnumerable<KeyValuePair<string, object>>
{
    private readonly List<KeyValuePair<string, object>> members = new List<KeyValuePair<string, object>>();

    public void Add(string name, object value) => members.Add(new KeyValuePair<string, object>(name, value));

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => members.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Reads JSON into <c>null</c>, <c>bool</c>, <c>long</c> (integers that fit), <c>double</c>, <c>string</c>,
/// <c>List&lt;object&gt;</c> and <c>Dictionary&lt;string, object&gt;</c>.
/// </summary>
internal sealed class JsonReader
{
    private const int MaxDepth = 256;

    private readonly string text;
    private int position;

    private JsonReader(string text)
    {
        this.text = text;
    }

    public static object Parse(string text)
    {
        var reader = new JsonReader(text);
        reader.SkipWhitespace();
        object value = reader.ReadValue(0);
        reader.SkipWhitespace();
        if (reader.position != text.Length)
            throw reader.Error("unexpected text after the value");

        return value;
    }

    private object ReadValue(int depth)
    {
        if (depth > MaxDepth)
            throw Error("nested too deeply");

        if (position >= text.Length)
            throw Error("unexpected end");

        char c = text[position];
        switch (c)
        {
            case '{':
                return ReadObject(depth);
            case '[':
                return ReadArray(depth);
            case '"':
                return ReadString();
            case 't':
                Expect("true");
                return true;
            case 'f':
                Expect("false");
                return false;
            case 'n':
                Expect("null");
                return null;
            default:
                if (c == '-' || (c >= '0' && c <= '9'))
                    return ReadNumber();

                throw Error($"unexpected character '{c}'");
        }
    }

    private Dictionary<string, object> ReadObject(int depth)
    {
        var result = new Dictionary<string, object>();
        position++;
        SkipWhitespace();
        if (TryConsume('}'))
            return result;

        while (true)
        {
            SkipWhitespace();
            if (position >= text.Length || text[position] != '"')
                throw Error("expected a member name");

            string name = ReadString();
            SkipWhitespace();
            if (!TryConsume(':'))
                throw Error("expected ':'");

            SkipWhitespace();
            result[name] = ReadValue(depth + 1);
            SkipWhitespace();
            if (TryConsume(','))
                continue;

            if (TryConsume('}'))
                return result;

            throw Error("expected ',' or '}'");
        }
    }

    private List<object> ReadArray(int depth)
    {
        var result = new List<object>();
        position++;
        SkipWhitespace();
        if (TryConsume(']'))
            return result;

        while (true)
        {
            SkipWhitespace();
            result.Add(ReadValue(depth + 1));
            SkipWhitespace();
            if (TryConsume(','))
                continue;

            if (TryConsume(']'))
                return result;

            throw Error("expected ',' or ']'");
        }
    }

    private string ReadString()
    {
        position++;
        var builder = new StringBuilder();
        while (true)
        {
            if (position >= text.Length)
                throw Error("unterminated string");

            char c = text[position++];
            if (c == '"')
                return builder.ToString();

            if (c != '\\')
            {
                builder.Append(c);
                continue;
            }

            if (position >= text.Length)
                throw Error("unterminated escape");

            char escape = text[position++];
            switch (escape)
            {
                case '"':
                case '\\':
                case '/':
                    builder.Append(escape);
                    break;
                case 'b':
                    builder.Append('\b');
                    break;
                case 'f':
                    builder.Append('\f');
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case 'u':
                    if (position + 4 > text.Length)
                        throw Error("short \\u escape");

                    builder.Append((char)int.Parse(text.Substring(position, 4), NumberStyles.HexNumber));
                    position += 4;
                    break;
                default:
                    throw Error($"unknown escape '\\{escape}'");
            }
        }
    }

    private object ReadNumber()
    {
        int start = position;
        bool integral = true;
        while (position < text.Length)
        {
            char c = text[position];
            if (c == '.' || c == 'e' || c == 'E')
                integral = false;
            else if (!(c == '-' || c == '+' || (c >= '0' && c <= '9')))
                break;

            position++;
        }

        string number = text.Substring(start, position - start);
        if (
            integral
            && long.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long whole)
        )
            return whole;

        if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double real))
            return real;

        throw Error($"bad number '{number}'");
    }

    private void Expect(string literal)
    {
        if (string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0)
            throw Error($"expected '{literal}'");

        position += literal.Length;
    }

    private bool TryConsume(char c)
    {
        if (position < text.Length && text[position] == c)
        {
            position++;
            return true;
        }

        return false;
    }

    private void SkipWhitespace()
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
            position++;
    }

    private FormatException Error(string message) => new FormatException($"Invalid JSON at {position}: {message}");
}

internal static class JsonWriter
{
    public static string Write(object value)
    {
        var builder = new StringBuilder();
        WriteValue(builder, value);
        return builder.ToString();
    }

    private static void WriteValue(StringBuilder builder, object value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case bool flag:
                builder.Append(flag ? "true" : "false");
                break;
            case string text:
                WriteString(builder, text);
                break;
            case double real:
                WriteDouble(builder, real);
                break;
            case float single:
                // "R" on the float itself keeps 0.1f as 0.1 rather than its widened double digits
                if (float.IsNaN(single) || float.IsInfinity(single))
                    builder.Append("null");
                else
                    AppendReal(builder, single.ToString("R", CultureInfo.InvariantCulture));
                break;
            case decimal exact:
                builder.Append(exact.ToString(CultureInfo.InvariantCulture));
                break;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                builder.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                break;
            case JsonObject members:
                WriteMembers(builder, members);
                break;
            case IDictionary<string, object> dictionary:
                WriteMembers(builder, dictionary);
                break;
            case IList list:
                builder.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0)
                        builder.Append(',');

                    WriteValue(builder, list[i]);
                }

                builder.Append(']');
                break;
            default:
                throw new ArgumentException($"Cannot write {value.GetType()} as JSON");
        }
    }

    private static void WriteMembers(StringBuilder builder, IEnumerable<KeyValuePair<string, object>> members)
    {
        builder.Append('{');
        bool first = true;
        foreach (KeyValuePair<string, object> member in members)
        {
            if (!first)
                builder.Append(',');

            first = false;
            WriteString(builder, member.Key);
            builder.Append(':');
            WriteValue(builder, member.Value);
        }

        builder.Append('}');
    }

    private static void WriteDouble(StringBuilder builder, double value)
    {
        // JSON has no NaN or infinities; the value encoder spells those out before they get here
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            builder.Append("null");
            return;
        }

        AppendReal(builder, value.ToString("R", CultureInfo.InvariantCulture));
    }

    // The driver decodes a number without a decimal point or exponent as an integer, so a whole double must keep
    // one ("3.0", not "3") to come back as a double.
    private static void AppendReal(StringBuilder builder, string text)
    {
        builder.Append(text);
        if (text.IndexOf('.') < 0 && text.IndexOf('E') < 0 && text.IndexOf('e') < 0)
            builder.Append(".0");
    }

    private static void WriteString(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        builder.Append(c);
                    break;
            }
        }

        builder.Append('"');
    }
}
