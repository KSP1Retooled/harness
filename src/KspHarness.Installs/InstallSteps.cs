using System.Security.Cryptography;
using System.Text;

namespace KspHarness.Installs;

/// <summary>One thing a definition adds. The fingerprint goes into the instance hash.</summary>
public abstract record InstallStep
{
    public abstract string Fingerprint { get; }
}

public sealed record CkanStep(string Identifier, string? Version) : InstallStep
{
    /// <summary>The argument CKAN takes: <c>identifier</c> or <c>identifier=version</c>.</summary>
    public string Spec => Version is null ? Identifier : $"{Identifier}={Version}";

    public override string Fingerprint => $"ckan {Spec}";
}

public sealed record ArchiveStep(string Name, string Url, string Sha256, string? Subfolder) : InstallStep
{
    public override string Fingerprint => $"archive {Name} {Url} {Sha256} {Subfolder}";
}

/// <summary>
/// Copies a folder into the instance. The fingerprint covers the folder's content, so changing test data rebuilds.
/// </summary>
public sealed record DirectoryStep(string SourceDir, string DestRelative, string ContentHash) : InstallStep
{
    public override string Fingerprint => $"directory {DestRelative} {ContentHash}";

    internal static string HashContent(string sourceDir)
    {
        if (!System.IO.Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"install data folder not found: {sourceDir}");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = System
            .IO.Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories)
            .Select(file => (Full: file, Relative: Path.GetRelativePath(sourceDir, file).Replace('\\', '/')))
            .OrderBy(file => file.Relative, StringComparer.Ordinal);

        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.Relative + "\n"));
            hash.AppendData(File.ReadAllBytes(file.Full));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

public sealed record SettingStep(string Key, string Value) : InstallStep
{
    public override string Fingerprint => $"setting {Key}={Value}";
}

public sealed record CompatibleVersionStep(string Version) : InstallStep
{
    public override string Fingerprint => $"compat {Version}";
}
