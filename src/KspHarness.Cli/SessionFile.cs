using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KspHarness.Cli;

/// <summary>
/// What <c>ksph launch</c> leaves in <c>&lt;instance&gt;/.harness/session.json</c> so that later commands can attach to
/// the running game.
/// </summary>
public sealed record SessionFile
{
    public const string FileName = "session.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public required int Pid { get; init; }

    public required int Port { get; init; }

    public required string Token { get; init; }

    public required string Instance { get; init; }

    public DateTime StartedUtc { get; init; }

    /// <summary>Unity's player log the game writes, when the launch chose one.</summary>
    public string? PlayerLog { get; init; }

    public static string PathFor(string instanceDir)
    {
        return Path.Combine(Path.GetFullPath(instanceDir), Installs.Instance.HarnessFolder, FileName);
    }

    /// <summary>
    /// The instance's session file, or null when there is none. A file that does not parse (a write cut short, or one
    /// edited by hand) also reads as null, so it cannot block a launch, quit or listing; <paramref name="warn"/> hears
    /// why.
    /// </summary>
    public static SessionFile? Read(string instanceDir, Action<string>? warn = null)
    {
        var path = PathFor(instanceDir);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(path), Json)
                ?? throw new JsonException("it holds null");
        }
        catch (JsonException e)
        {
            warn?.Invoke($"ignoring {path}, which does not parse: {e.Message}");
            return null;
        }
    }

    public static void Delete(string instanceDir)
    {
        // File.Delete accepts a missing file but not a missing folder, as for an instance path with a typo.
        var path = PathFor(instanceDir);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void Write()
    {
        // Written beside the target and moved over it, so a reader never sees half a file and a write cut short
        // leaves the old one; the move also replaces a hard link instead of writing through it.
        var path = PathFor(Instance);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, ToJson());
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The game process is still running. A reused pid is told apart by its start time.</summary>
    public bool IsAlive()
    {
        try
        {
            using var process = Process.GetProcessById(Pid);
            return !process.HasExited && process.StartTime.ToUniversalTime() <= StartedUtc.AddMinutes(1);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            // A pid reused by a process this user cannot open is not the game that was started.
            return false;
        }
    }
}
