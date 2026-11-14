namespace KspHarness.Installs;

/// <param name="Refused">Linkable files the file system would not link (access denied or the link limit), copied instead.</param>
public sealed record CopyStats(int Linked, int Copied, long CopiedBytes, int Refused);

/// <summary>
/// Copies a starting install, hard-linking the large files that the game and mods only read. A hard link shares the
/// bytes with the source, so a link is only safe for a file nothing opens for writing: anything written in place would
/// change the starting install, and with it every instance linked to it. Files are replaced by deleting them first for
/// the same reason.
/// </summary>
internal sealed class TreeCopier
{
    private static readonly HashSet<string> LinkedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".assets", ".resS", ".resource", ".bundle", ".dds", ".png", ".mu", ".mbm", ".tga", ".jpg", ".ogg", ".wav",
        ".ksp",
    };

    // The player and Unity's libraries beside it: KSP_x64.exe and UnityPlayer.dll on Windows, KSP.x86_64 and
    // UnityPlayer.so on Linux. A link shares the source's mode, so the Linux player stays executable.
    private static readonly HashSet<string> TopLevelBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".so", ".x86_64",
    };

    // The game writes into these: craft thumbnails under Ships/@thumbs and thumbs, save banners, mission files,
    // screenshots and logs.
    private static readonly HashSet<string> WrittenTopFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "saves", "Ships", "thumbs", "Missions", "Screenshots", "Logs", "CKAN", ".harness",
    };

    // At any depth. Mods write their data into PluginData. The editor rewrites part icons in Parts/@thumbs (stock ships
    // them under GameData/Squad and SquadExpansion) and craft thumbnails in the expansions' Ships/@thumbs with
    // File.WriteAllBytes, which writes into the existing file.
    private static readonly HashSet<string> WrittenFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "PluginData", "@thumbs",
    };

    private readonly string dataDirRelative;
    private bool linksAllowed;

    public TreeCopier(string dataDirRelative, bool allowLinks)
    {
        this.dataDirRelative = dataDirRelative.TrimEnd('/') + "/";
        linksAllowed = allowLinks;
    }

    public bool LinksAllowed => linksAllowed;

    /// <summary>Whether a file at this path (relative to the install root, forward slashes) may be a hard link.</summary>
    public bool IsLinkable(string relative)
    {
        string[] segments = relative.Split('/');
        if (segments.Length > 1 && WrittenTopFolders.Contains(segments[0]))
        {
            return false;
        }

        if (segments.SkipLast(1).Any(WrittenFolders.Contains))
        {
            return false;
        }

        // Unity reads its data folder and the DLC asset bundles (which have no extension) and never writes them; the
        // DLC bundles alone are about 450 MB.
        bool unityData = relative.StartsWith(dataDirRelative, StringComparison.OrdinalIgnoreCase);
        bool assetBundle = segments.Length > 1
            && segments[^2].Equals("AssetBundles", StringComparison.OrdinalIgnoreCase);
        string extension = Path.GetExtension(relative);
        bool topLevelBinary = segments.Length == 1 && TopLevelBinaryExtensions.Contains(extension);

        return unityData || assetBundle || topLevelBinary || LinkedExtensions.Contains(extension);
    }

    /// <summary>Copies <paramref name="source"/> into <paramref name="dest"/>, skipping excluded top-level names.</summary>
    public CopyStats Copy(string source, string dest, IReadOnlySet<string> excludedTopLevel, CancellationToken cancel)
    {
        var files = new List<string>();
        Collect(source, "", excludedTopLevel, dest, files);

        int linked = 0;
        int refused = 0;
        int copied = 0;
        long copiedBytes = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancel };

        Parallel.ForEach(files, options, relative =>
        {
            string from = Path.Combine(source, relative);
            string to = Path.Combine(dest, relative);

            if (linksAllowed && IsLinkable(relative))
            {
                switch (HardLinks.TryCreate(from, to))
                {
                    case LinkResult.Linked:
                        Interlocked.Increment(ref linked);
                        return;
                    case LinkResult.FileRefused:
                        Interlocked.Increment(ref refused);
                        break;
                    case LinkResult.VolumeRefused:
                        linksAllowed = false;
                        break;
                }
            }

            CopyFile(from, to);
            Interlocked.Increment(ref copied);
            Interlocked.Add(ref copiedBytes, new FileInfo(to).Length);
        });

        return new CopyStats(linked, copied, copiedBytes, refused);
    }

    private static void Collect(string sourceRoot, string relative, IReadOnlySet<string> excludedTopLevel,
        string destRoot, List<string> files)
    {
        string sourceDir = relative.Length == 0 ? sourceRoot : Path.Combine(sourceRoot, relative);
        Directory.CreateDirectory(relative.Length == 0 ? destRoot : Path.Combine(destRoot, relative));

        foreach (var entry in new DirectoryInfo(sourceDir).EnumerateFileSystemInfos())
        {
            if (relative.Length == 0 && excludedTopLevel.Contains(entry.Name))
            {
                continue;
            }

            string child = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;

            // A junction or symbolic link would be followed: files outside the install (often a mod's source checkout)
            // would be linked into the instance for the game and CKAN to change, and a link to an ancestor never ends.
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InstallException(
                    $"{entry.FullName} is a junction or symbolic link; a starting install must hold only plain files");
            }

            if (entry is DirectoryInfo)
            {
                Collect(sourceRoot, child, excludedTopLevel, destRoot, files);
            }
            else
            {
                files.Add(child);
            }
        }
    }

    /// <summary>Copies a folder over existing content, deleting each file it replaces so links are broken.</summary>
    public static void Overlay(string source, string dest)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(dest, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            ReplaceFile(file, to);
        }
    }

    /// <summary>Copies a file, deleting the target first so an existing hard link is not written through.</summary>
    public static void ReplaceFile(string source, string dest)
    {
        if (File.Exists(dest))
        {
            File.Delete(dest);
        }

        CopyFile(source, dest);
    }

    // A copied player (without hard links, or KSP.app/Contents/MacOS/KSP, which is never linked) that lost its execute
    // bit would not start. File.Copy keeps the mode on Unix, but the instance should not depend on that detail.
    private static void CopyFile(string source, string dest)
    {
        File.Copy(source, dest);
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(source);
            if (File.GetUnixFileMode(dest) != mode)
            {
                File.SetUnixFileMode(dest, mode);
            }
        }
    }
}
