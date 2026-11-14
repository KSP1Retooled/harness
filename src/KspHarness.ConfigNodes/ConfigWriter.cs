using System.IO;
using System.Text;

namespace KspHarness.ConfigNodes;

/// <summary>Writes config nodes in the format KSP's <c>ConfigNode.Save</c> produces.</summary>
/// <remarks>
/// Values are written as <c>key = value</c>, nodes as the name on one line and braces on their own lines, indented
/// with one tab per level. Like KSP's <c>Value.Sanitize</c>, braces in keys and values become square brackets and
/// <c>=</c> in keys becomes <c>-</c>, so that the text reads back into the same structure.
/// </remarks>
public static class ConfigWriter
{
    /// <summary>
    /// Writes a root node as a file: its values at the top level, then its child nodes. This is the form of
    /// <c>.cfg</c>, <c>.craft</c>, <c>.sfs</c> and <c>settings.cfg</c> files.
    /// </summary>
    public static void WriteRoot(CfgNode root, TextWriter writer)
    {
        foreach (var value in root.ValueList)
            WriteValue(value, string.Empty, writer);

        foreach (var node in root.Nodes)
            WriteNode(node, string.Empty, writer);
    }

    /// <summary>Writes one node with its name and braces.</summary>
    public static void WriteNode(CfgNode node, TextWriter writer)
    {
        WriteNode(node, string.Empty, writer);
    }

    public static string WriteRoot(CfgNode root, string newLine = "\n")
    {
        var writer = new StringWriter { NewLine = newLine };
        WriteRoot(root, writer);
        return writer.ToString();
    }

    public static string WriteNode(CfgNode node, string newLine = "\n")
    {
        var writer = new StringWriter { NewLine = newLine };
        WriteNode(node, string.Empty, writer);
        return writer.ToString();
    }

    private static void WriteNode(CfgNode node, string indent, TextWriter writer)
    {
        writer.WriteLine(indent + Sanitize(node.Name, isKey: true));
        writer.WriteLine(indent + "{");

        var inner = indent + "\t";
        foreach (var value in node.ValueList)
            WriteValue(value, inner, writer);

        foreach (var child in node.Nodes)
            WriteNode(child, inner, writer);

        writer.WriteLine(indent + "}");
    }

    private static void WriteValue(CfgValue value, string indent, TextWriter writer)
    {
        writer.WriteLine(indent + Sanitize(value.Key, isKey: true) + " = " + Sanitize(value.Value, isKey: false));
    }

    private static string Sanitize(string text, bool isKey)
    {
        if (text.IndexOfAny(isKey ? KeySpecials : ValueSpecials) < 0)
            return text;

        var builder = new StringBuilder(text);
        builder.Replace('{', '[').Replace('}', ']');
        if (isKey)
            builder.Replace('=', '-');

        return builder.ToString();
    }

    private static readonly char[] KeySpecials = { '{', '}', '=' };

    private static readonly char[] ValueSpecials = { '{', '}' };
}
