using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KspHarness.Installs;

/// <summary>
/// A KSP install that instances are copied from. Nothing in this library writes to it.
/// </summary>
public sealed partial class StartingInstall
{
    private StartingInstall(GameLayout layout, string buildIdFile)
    {
        Layout = layout;
        BuildIdFile = buildIdFile;

        string buildText = File.ReadAllText(buildIdFile);
        BuildId = BuildIdLine().Match(buildText) is { Success: true } build
            ? build.Groups[1].Value.Trim()
            : throw new InstallException($"{buildIdFile} has no 'build id' line");

        RetooledVersion = RetooledVersionLine().Match(buildText) is { Success: true } retooled
            ? retooled.Groups[1].Value.Trim()
            : null;

        IsRetooled =
            System.IO.Directory.Exists(Path.Combine(layout.GameDataDir, "Retooled"))
            || File.Exists(Path.Combine(layout.ManagedDir, "KSP.Retooled.Modding.TextMeshPro.dll"));

        KspVersion = ReadReadmeVersion(Path.Combine(layout.Root, "readme.txt"));
        Identity = BuildIdentity(buildText);
        IdentityHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Identity)));
    }

    public GameLayout Layout { get; }

    public string Root => Layout.Root;

    public string BuildIdFile { get; }

    public string BuildId { get; }

    /// <summary>The version in readme.txt's first "Version x.y.z" line: 1.12.5 for stock, 1.13.0 for Retooled.</summary>
    public string? KspVersion { get; }

    public bool IsRetooled { get; }

    /// <summary>The <c>retooled-version</c> line of the buildID file, when Retooled wrote one.</summary>
    public string? RetooledVersion { get; }

    /// <summary><c>stock</c> or <c>retooled</c>: what lock files record, since CKAN resolves differently for each.</summary>
    public string Kind => IsRetooled ? "retooled" : "stock";

    /// <summary>
    /// Text that changes when the install does: sizes and times of the files that a game update or patch rewrites,
    /// and the buildID file. It does not read whole files, so it is cheap to compute on every build.
    /// </summary>
    public string Identity { get; }

    public string IdentityHash { get; }

    public static StartingInstall Open(string root)
    {
        var layout = GameLayout.Require(root);
        string? buildIdFile = new[] { "buildID64.txt", "buildID.txt" }
            .Select(name => Path.Combine(layout.Root, name))
            .FirstOrDefault(File.Exists);

        if (buildIdFile is null)
        {
            throw new InstallException($"not a KSP install: {layout.Root} has no buildID64.txt or buildID.txt");
        }

        return new StartingInstall(layout, buildIdFile);
    }

    private string BuildIdentity(string buildText)
    {
        var text = new StringBuilder();
        text.Append("kind ").Append(Kind).Append('\n');
        text.Append("buildid ").Append(buildText.ReplaceLineEndings("\n").Trim()).Append('\n');

        string[] keyFiles =
        [
            Layout.LaunchPath,
            Path.Combine(Layout.ManagedDir, "Assembly-CSharp.dll"),
            Path.Combine(Layout.ManagedDir, "Assembly-CSharp-firstpass.dll"),
            Path.Combine(Layout.ManagedDir, "UnityEngine.CoreModule.dll"),
            Layout.PlayerLibrary,
            Path.Combine(Layout.DataDir, "globalgamemanagers"),
            Path.Combine(Layout.DataDir, "resources.assets"),
        ];

        foreach (string file in keyFiles)
        {
            var info = new FileInfo(file);
            string relative = Path.GetRelativePath(Layout.Root, file).Replace('\\', '/');
            text.Append("file ").Append(relative).Append(' ');
            text.Append(info.Exists ? $"{info.Length} {info.LastWriteTimeUtc.Ticks}" : "absent").Append('\n');
        }

        // Folders a patch adds or removes at the top of GameData.
        foreach (
            string dir in System.IO.Directory.EnumerateDirectories(Layout.GameDataDir).Order(StringComparer.Ordinal)
        )
        {
            text.Append("gamedata ").Append(Path.GetFileName(dir)).Append('\n');
        }

        return text.ToString();
    }

    private static string? ReadReadmeVersion(string readme)
    {
        if (!File.Exists(readme))
        {
            return null;
        }

        foreach (string line in File.ReadLines(readme))
        {
            if (ReadmeVersionLine().Match(line) is { Success: true } match)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    [GeneratedRegex(@"^\s*build id\s*=\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BuildIdLine();

    [GeneratedRegex(@"^\s*retooled-version\s*=\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex RetooledVersionLine();

    [GeneratedRegex(@"^\s*Version\s+(\d+\.\d+\.\d+)\s*$")]
    private static partial Regex ReadmeVersionLine();
}
