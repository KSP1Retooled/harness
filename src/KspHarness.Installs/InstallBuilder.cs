using System.Text.RegularExpressions;

namespace KspHarness.Installs;

/// <summary>Collects the steps of one definition. Steps keep the order they were added in.</summary>
public sealed partial class InstallBuilder
{
    private readonly List<InstallStep> steps = [];

    internal Type? Included { get; private set; }

    public IReadOnlyList<InstallStep> Steps => steps;

    /// <summary>
    /// Applies everything that definition adds before this definition's own steps. A definition includes at most one
    /// other: the include chain is a list of layers, and there is no way to merge two.
    /// </summary>
    public InstallBuilder Include<TDefinition>()
        where TDefinition : InstallDefinition, new()
    {
        if (Included is not null)
        {
            throw new InvalidOperationException(
                $"a definition can include only one other; it already includes {Included.Name}");
        }

        Included = typeof(TDefinition);
        return this;
    }

    public InstallBuilder Ckan(string identifier, string? version = null)
    {
        if (!CkanIdentifier().IsMatch(identifier))
        {
            throw new ArgumentException($"not a CKAN identifier: '{identifier}'", nameof(identifier));
        }

        if (version is not null && (version.Length == 0 || version.Any(char.IsWhiteSpace)))
        {
            throw new ArgumentException($"not a CKAN version: '{version}'", nameof(version));
        }

        steps.Add(new CkanStep(identifier, version));
        return this;
    }

    /// <param name="subfolder">Where under GameData the archive's content goes; null for GameData itself.</param>
    public InstallBuilder Archive(string name, string url, string sha256, string? subfolder = null)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException($"archive {name}: sha256 must be 64 hex digits", nameof(sha256));
        }

        if (subfolder is not null)
        {
            RelativePaths.Check(subfolder, nameof(subfolder));
        }

        steps.Add(new ArchiveStep(name, url, sha256.ToLowerInvariant(), subfolder));
        return this;
    }

    /// <summary>Copies a folder into the instance, replacing files with the same names.</summary>
    public InstallBuilder Directory(string sourceDir, string destRelative)
    {
        RelativePaths.Check(destRelative, nameof(destRelative));
        string full = Path.GetFullPath(sourceDir);
        steps.Add(new DirectoryStep(full, RelativePaths.Normalize(destRelative), DirectoryStep.HashContent(full)));
        return this;
    }

    /// <summary>Copies a save folder to <c>saves/&lt;folder name&gt;</c>.</summary>
    public InstallBuilder Saves(string sourceDir)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDir)));
        return Directory(sourceDir, "saves/" + name);
    }

    /// <summary>Merges a folder laid out like <c>Ships</c> (<c>VAB</c>, <c>SPH</c>) into the instance's Ships.</summary>
    public InstallBuilder Ships(string sourceDir) => Directory(sourceDir, "Ships");

    public InstallBuilder Setting(string key, string value)
    {
        SettingsFile.CheckEntry(key, value);
        steps.Add(new SettingStep(key, value));
        return this;
    }

    /// <summary>Adds a game version CKAN treats as compatible, as <c>ckan compat add</c> does.</summary>
    public InstallBuilder CompatibleVersion(string version)
    {
        if (!GameVersion().IsMatch(version))
        {
            throw new ArgumentException($"not a game version: '{version}'", nameof(version));
        }

        steps.Add(new CompatibleVersionStep(version));
        return this;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_.]*$")]
    private static partial Regex CkanIdentifier();

    [GeneratedRegex(@"^\d+(\.\d+){1,3}$")]
    private static partial Regex GameVersion();
}
