using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KspHarness.ConfigNodes;

namespace KspHarness.Approvals;

/// <summary>
/// Turns a value into text that diffs well and does not change between runs: a string as itself, config nodes as cfg
/// text, anything else as indented JSON with object keys sorted.
/// </summary>
public static class ApprovalText
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter(), new CfgNodeConverter(), new HandleConverter() },
    };

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(object? value)
    {
        var text = value switch
        {
            string s => s,
            CfgNode node => ConfigWriter.WriteNode(node),
            IEnumerable<CfgNode> nodes => string.Concat(nodes.Select(node => ConfigWriter.WriteNode(node))),
            _ => Json(value),
        };

        text = Normalize(text);
        return text.EndsWith('\n') ? text : text + "\n";
    }

    internal static string Normalize(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static string Json(object? value)
    {
        var node = JsonSerializer.SerializeToNode(value, value?.GetType() ?? typeof(object), Options);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            Sorted(node)?.WriteTo(writer);
            if (node is null)
            {
                writer.WriteNullValue();
            }
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static JsonNode? Sorted(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var (key, child) in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToList())
                {
                    obj.Remove(key);
                    sorted[key] = Sorted(child);
                }

                return sorted;

            case JsonArray array:
                var items = array.ToList();
                array.Clear();
                return new JsonArray(items.Select(Sorted).ToArray());

            default:
                return node;
        }
    }

    /// <summary>A node nested in a JSON value is written as its cfg text, one string per line, which reads better in a diff.</summary>
    private sealed class CfgNodeConverter : JsonConverter<CfgNode>
    {
        public override CfgNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            throw new NotSupportedException();
        }

        public override void Write(Utf8JsonWriter writer, CfgNode value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var line in ConfigWriter.WriteNode(value).TrimEnd('\n').Split('\n'))
            {
                writer.WriteStringValue(line);
            }

            writer.WriteEndArray();
        }
    }

    /// <summary>A handle's id changes from run to run, so only its type and text are written.</summary>
    private sealed class HandleConverter : JsonConverter<Handle>
    {
        public override Handle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            throw new NotSupportedException();
        }

        public override void Write(Utf8JsonWriter writer, Handle value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("handle", value.Type);
            writer.WriteString("text", value.Text);
            writer.WriteEndObject();
        }
    }
}
