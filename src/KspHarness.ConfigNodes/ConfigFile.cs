using System.IO;
using System.Text;

namespace KspHarness.ConfigNodes;

/// <summary>Loads and saves config files: <c>.cfg</c>, <c>.craft</c>, <c>.sfs</c> and <c>settings.cfg</c>.</summary>
/// <remarks>
/// Every one of these is a root node, as KSP's <c>ConfigNode.Load</c> returns it: a part <c>.cfg</c> has only
/// child nodes (<c>PART</c>), a <c>.craft</c> has values (<c>ship</c>, <c>version</c>) and <c>PART</c> nodes, an
/// <c>.sfs</c> has a <c>GAME</c> node and <c>settings.cfg</c> has only values.
/// </remarks>
public static class ConfigFile
{
    public static CfgNode Load(string path)
    {
        // ReadAllText detects and drops a byte order mark, as KSP's File.ReadAllLines does.
        return ConfigParser.Parse(File.ReadAllText(path));
    }

    /// <summary>Saves a root node with KSP's layout and line endings of <paramref name="newLine"/>.</summary>
    public static void Save(CfgNode root, string path, string newLine = "\r\n")
    {
        using var writer = new StreamWriter(
            path,
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
        writer.NewLine = newLine;
        ConfigWriter.WriteRoot(root, writer);
    }
}
