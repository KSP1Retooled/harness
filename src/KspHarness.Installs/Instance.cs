using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KspHarness.Installs;

/// <summary>The content of <c>.harness/instance.json</c>, written last: its presence marks a finished instance.</summary>
public sealed class InstanceRecord
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public int Format { get; init; } = 1;

    public required string Name { get; init; }

    public required string Definition { get; init; }

    public required string Hash { get; init; }

    /// <summary>The hash of the included definition's instance; null when the definition includes none.</summary>
    public string? Parent { get; init; }

    public required string StartRoot { get; init; }

    public required string StartKind { get; init; }

    public required string StartIdentity { get; init; }

    public required List<string> Steps { get; init; }

    public required List<LockedModule> Ckan { get; init; }

    public DateTime CreatedUtc { get; init; }
}

public sealed class Instance
{
    public const string HarnessFolder = ".harness";

    public const string RecordFileName = "instance.json";

    private readonly GameLayout layout;

    private Instance(string root, InstanceRecord record, string? lockFile)
    {
        layout = GameLayout.Require(root);
        Record = record;
        LockFile = lockFile;
    }

    public string Root => layout.Root;

    /// <summary>The folder name: the definition name and the first 12 hex digits of the instance hash.</summary>
    public string Name => System.IO.Path.GetFileName(Root);

    public string Hash => Record.Hash;

    public InstanceRecord Record { get; }

    public GameLayout Layout => layout;

    public string ManagedDir => layout.ManagedDir;

    public string GameDataDir => layout.GameDataDir;

    /// <summary>The lock file the instance was built with, if any.</summary>
    public string? LockFile { get; }

    public string HarnessDir => System.IO.Path.Combine(Root, HarnessFolder);

    public string Path(string relative) => System.IO.Path.Combine(Root, relative);

    public static bool IsReady(string root) => File.Exists(System.IO.Path.Combine(root, HarnessFolder, RecordFileName));

    /// <summary>Opens a finished instance.</summary>
    public static Instance Open(string root, string? lockFile = null)
    {
        string recordPath = System.IO.Path.Combine(root, HarnessFolder, RecordFileName);
        if (!File.Exists(recordPath))
        {
            throw new InstallException($"{root} is not a finished instance: it has no {HarnessFolder}/{RecordFileName}");
        }

        var record = JsonSerializer.Deserialize<InstanceRecord>(File.ReadAllText(recordPath), InstanceRecord.JsonOptions)
            ?? throw new InstallException($"{recordPath} is empty");
        return new Instance(System.IO.Path.GetFullPath(root), record, lockFile);
    }

    /// <summary>
    /// Copies a mod folder (such as <c>GameData/KspHarness</c> from a build) to <c>GameData/&lt;folder name&gt;</c>.
    /// The existing copy is replaced only when some file differs, so a game that already loaded it is not disturbed
    /// for nothing. Returns whether anything changed.
    /// </summary>
    /// <param name="requiredFiles">
    /// Paths relative to <paramref name="sourceFolder"/> that must exist, such as the mod's DLL. A source without them
    /// (an empty or half-written build output) is refused before the instance is touched, so a deploy never replaces
    /// a working mod with a broken one. A source with no files at all is always refused.
    /// </param>
    /// <exception cref="InstallException">
    /// The source lacks a required file, or its copy came out without one; or the folder would change while a game
    /// runs in the instance.
    /// </exception>
    public bool DeployMod(string sourceFolder, params string[] requiredFiles)
    {
        sourceFolder = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(sourceFolder));
        if (!System.IO.Directory.Exists(sourceFolder))
        {
            throw new DirectoryNotFoundException($"mod folder not found: {sourceFolder}");
        }

        RequireFiles(sourceFolder, requiredFiles, $"the mod folder {sourceFolder}");

        string name = System.IO.Path.GetFileName(sourceFolder);
        string dest = System.IO.Path.Combine(GameDataDir, name);
        if (SameContent(sourceFolder, dest))
        {
            return false;
        }

        EnsureNoGameRunning($"replace GameData/{name}");

        // Copied outside GameData and swapped in, so a source that changes during the copy (a build writing the same
        // folder) fails here instead of leaving the instance without the mod, and the game never loads a partial copy.
        // Each deploy has its own staging folder, so two deploying at once do not delete each other's.
        string deploying = System.IO.Path.Combine(HarnessDir, "deploying");
        string id = Guid.NewGuid().ToString("N")[..8];
        string staging = System.IO.Path.Combine(deploying, $"{name}-{id}");
        System.IO.Directory.CreateDirectory(staging);
        try
        {
            TreeCopier.Overlay(sourceFolder, staging);
            RequireFiles(staging, requiredFiles, $"the copy of {sourceFolder}");
        }
        catch
        {
            IoRetry.DeleteTree(staging);
            throw;
        }

        // The old copy is renamed aside rather than deleted in place: a rename fails as a whole when a file in it is
        // open, where a delete would have removed the files before that one.
        string? old = null;
        if (System.IO.Directory.Exists(dest))
        {
            old = System.IO.Path.Combine(deploying, $"{name}-{id}.old");
            try
            {
                IoRetry.Run(() => System.IO.Directory.Move(dest, old));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                IoRetry.DeleteTree(staging);
                throw new InstallException($"cannot replace {dest}, a file in it is in use: {e.Message}", e);
            }
        }

        try
        {
            IoRetry.Run(() => System.IO.Directory.Move(staging, dest));
        }
        catch
        {
            if (old is not null)
            {
                System.IO.Directory.Move(old, dest);
            }

            throw;
        }

        if (old is not null)
        {
            IoRetry.DeleteTree(old);
        }

        return true;
    }

    /// <summary>
    /// The ids of game processes whose executable is in this instance, launched by the harness or not. Processes whose
    /// executable this user may not read are not listed.
    /// </summary>
    public IReadOnlyList<int> RunningGameProcessIds()
    {
        // Linux and macOS report the program's path with links resolved, so the instance's resolved path counts too.
        string[] roots = new[] { Root, UnixPaths.Resolve(Root) }
            .OfType<string>()
            .Select(root => System.IO.Path.TrimEndingDirectorySeparator(root) + System.IO.Path.DirectorySeparatorChar)
            .Distinct()
            .ToArray();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool InInstance(string? file) => file is not null && roots.Any(root => file.StartsWith(root, comparison));

        if (OperatingSystem.IsLinux())
        {
            return LinuxProcessIds(InInstance);
        }

        var running = new List<int>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(layout.ProcessName))
        {
            using (process)
            {
                try
                {
                    if (InInstance(process.MainModule?.FileName))
                    {
                        running.Add(process.Id);
                    }
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Exited meanwhile, or not this user's to inspect.
                }
            }
        }

        return running;
    }

    // Linux names a process after its main thread, which a program may rename and which is cut at 15 characters, so
    // the program each process runs is read from /proc instead.
    private static List<int> LinuxProcessIds(Func<string?, bool> runsFromInstance)
    {
        var running = new List<int>();
        foreach (string dir in System.IO.Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(System.IO.Path.GetFileName(dir), out int pid))
            {
                continue;
            }

            try
            {
                var program = File.ResolveLinkTarget(System.IO.Path.Combine(dir, "exe"), returnFinalTarget: false);
                if (runsFromInstance(program?.FullName))
                {
                    running.Add(pid);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Exited meanwhile, or not this user's to inspect.
            }
        }

        return running;
    }

    /// <summary>
    /// Throws when a game runs in this instance. A second game there would write the same saves and settings, and
    /// files the running game has loaded cannot be replaced.
    /// </summary>
    /// <param name="action">What would have been done, for the message: "launch a game", "replace GameData/X".</param>
    /// <exception cref="InstallException">A game runs in the instance.</exception>
    public void EnsureNoGameRunning(string action)
    {
        var running = RunningGameProcessIds();
        if (running.Count > 0)
        {
            throw new InstallException(
                $"cannot {action}: a game is running in {Root} (pid {string.Join(", ", running)}); quit it first");
        }
    }

    private static void RequireFiles(string folder, IReadOnlyList<string> requiredFiles, string what)
    {
        if (!System.IO.Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any())
        {
            throw new InstallException($"{what} has no files; refusing to deploy it");
        }

        var missing = requiredFiles.Where(file => !File.Exists(System.IO.Path.Combine(folder, file))).ToList();
        if (missing.Count > 0)
        {
            throw new InstallException($"{what} lacks {string.Join(", ", missing)}; refusing to deploy it");
        }
    }

    private static bool SameContent(string source, string dest)
    {
        if (!System.IO.Directory.Exists(dest))
        {
            return false;
        }

        static List<string> RelativeFiles(string root) => System.IO.Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => System.IO.Path.GetRelativePath(root, file))
            .Order(StringComparer.Ordinal)
            .ToList();

        var sourceFiles = RelativeFiles(source);
        if (!sourceFiles.SequenceEqual(RelativeFiles(dest), StringComparer.Ordinal))
        {
            return false;
        }

        foreach (string relative in sourceFiles)
        {
            var a = new FileInfo(System.IO.Path.Combine(source, relative));
            var b = new FileInfo(System.IO.Path.Combine(dest, relative));
            if (a.Length != b.Length || !File.ReadAllBytes(a.FullName).AsSpan().SequenceEqual(File.ReadAllBytes(b.FullName)))
            {
                return false;
            }
        }

        return true;
    }
}
