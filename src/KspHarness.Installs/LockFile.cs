using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KspHarness.Installs;

public enum LockMode
{
    /// <summary>Use the lock file where it matches the definition; resolve and rewrite the layers that do not.</summary>
    Auto,

    /// <summary>Fail when the lock file is missing or does not match the definition. For unattended runs.</summary>
    Frozen,

    /// <summary>Resolve every layer again with CKAN and rewrite the lock file.</summary>
    Update,
}

public sealed record LockedModule(string Identifier, string Version)
{
    /// <summary>The argument CKAN takes to install exactly this version.</summary>
    [JsonIgnore]
    public string Spec => $"{Identifier}={Version}";
}

public sealed record LockedArchive(string Name, string Url, string Sha256);

/// <summary>What one definition added: the CKAN modules new in that layer, with the versions CKAN chose.</summary>
public sealed class LockLayer
{
    public required string Definition { get; init; }

    /// <summary>The CKAN steps as written in the definition, to tell when the definition changed.</summary>
    public List<string> Requested { get; init; } = [];

    public List<LockedModule> Ckan { get; init; } = [];

    public List<LockedArchive> Archives { get; init; } = [];

    internal bool Matches(InstallLayer layer)
    {
        var archives = layer.ArchiveSteps.Select(step => new LockedArchive(step.Name, step.Url, step.Sha256));
        return Definition == layer.DefinitionId
            && Requested.SequenceEqual(layer.CkanSteps.Select(step => step.Spec))
            && Archives.SequenceEqual(archives);
    }
}

public sealed class LockFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // The file is read by people in diffs; '+' in nested type names would otherwise be +.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public int Format { get; init; } = 1;

    /// <summary>The starting install's kind (<c>stock</c> or <c>retooled</c>) the modules were resolved for.</summary>
    public required string Start { get; init; }

    public List<LockLayer> Layers { get; init; } = [];

    public static LockFile? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        LockFile? lockFile;
        try
        {
            lockFile = JsonSerializer.Deserialize<LockFile>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException e)
        {
            throw new InstallException(
                $"lock file {path} is not valid ({e.Message}); fix it or build with LockMode.Update to rewrite it",
                e
            );
        }

        if (lockFile is null)
        {
            throw new InstallException($"{path} is empty");
        }

        if (lockFile.Format != 1)
        {
            throw new InstallException($"{path} has lock format {lockFile.Format}; this library reads format 1");
        }

        return lockFile;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions).ReplaceLineEndings("\n") + "\n";

    /// <summary>
    /// Writes the file only when its text changes, so an unchanged lock keeps its timestamp. The text goes to a
    /// temporary file that replaces the lock, so a run reading it meanwhile never sees half of it.
    /// </summary>
    public void Save(string path)
    {
        string json = ToJson();
        if (File.Exists(path) && File.ReadAllText(path) == json)
        {
            return;
        }

        string full = Path.GetFullPath(path);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = $"{full}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, json);
            // Fails while another run has the lock open for reading, which it does only for a moment.
            IoRetry.Run(() => File.Move(temp, full, overwrite: true));
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
