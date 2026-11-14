using System.Text.RegularExpressions;

namespace KspHarness.Installs;

public enum GamePlatform
{
    Windows,
    Linux,
    MacOS,
}

/// <summary>
/// Where the executable and the Unity data folder sit in a KSP install on each platform. KSP Retooled's players keep
/// stock's names (<c>KSP_x64.exe</c>, <c>KSP.x86_64</c>, <c>KSP.app</c>), so one table covers both games.
/// </summary>
public sealed partial class GameLayout
{
    /// <summary>The bundle's binary when its Info.plist does not name another; stock's app is built as <c>KSP</c>.</summary>
    private const string DefaultMacBinary = "KSP";

    private GameLayout(string root, GamePlatform platform, string executable, string dataDir)
    {
        Root = root;
        Platform = platform;
        Executable = executable;
        DataDir = dataDir;
    }

    public string Root { get; }

    public GamePlatform Platform { get; }

    /// <summary>The file the player ships as (on macOS the app bundle folder).</summary>
    public string Executable { get; }

    /// <summary>
    /// The file to start: <see cref="Executable"/>, or on macOS the binary inside the bundle, which takes command line
    /// arguments directly, where <c>open KSP.app</c> would need <c>--args</c> and would not hand back the game's process.
    /// </summary>
    public string LaunchPath => Platform == GamePlatform.MacOS ? MacBinary(Executable) : Executable;

    /// <summary>Unity's native player library, which a Retooled patch replaces along with the managed code.</summary>
    public string PlayerLibrary => Platform switch
    {
        GamePlatform.Windows => Path.Combine(Root, "UnityPlayer.dll"),
        GamePlatform.Linux => Path.Combine(Root, "UnityPlayer.so"),
        _ => Path.Combine(Executable, "Contents", "Frameworks", "UnityPlayer.dylib"),
    };

    /// <summary>
    /// The name the operating system lists the running game under: Windows drops the <c>.exe</c>, Linux and macOS
    /// use the binary's whole file name (<c>KSP.x86_64</c> keeps its suffix).
    /// </summary>
    public string ProcessName => Platform == GamePlatform.Windows
        ? Path.GetFileNameWithoutExtension(Executable)
        : Path.GetFileName(LaunchPath);

    /// <summary>Whether this machine can start the player: the harness does not run games under Wine or Proton.</summary>
    public bool RunsOnThisMachine => Platform switch
    {
        GamePlatform.Windows => OperatingSystem.IsWindows(),
        GamePlatform.Linux => OperatingSystem.IsLinux(),
        _ => OperatingSystem.IsMacOS(),
    };

    public string DataDir { get; }

    public string ManagedDir => Path.Combine(DataDir, "Managed");

    /// <summary>On every platform GameData sits at the install root, next to the executable or app bundle.</summary>
    public string GameDataDir => Path.Combine(Root, "GameData");

    /// <summary>The data folder relative to the root, with forward slashes.</summary>
    public string DataDirRelative => Path.GetRelativePath(Root, DataDir).Replace('\\', '/');

    /// <summary>Finds the layout, or returns null with the reason when the folder is not a KSP install.</summary>
    public static GameLayout? Detect(string root, out string? problem)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        problem = null;

        if (!System.IO.Directory.Exists(root))
        {
            problem = $"{root} does not exist";
            return null;
        }

        foreach (var layout in Candidates(root))
        {
            bool executable = File.Exists(layout.Executable) || System.IO.Directory.Exists(layout.Executable);
            if (!executable || !System.IO.Directory.Exists(layout.DataDir))
            {
                continue;
            }

            if (!File.Exists(Path.Combine(layout.ManagedDir, "Assembly-CSharp.dll")))
            {
                problem = $"{root} has no {layout.DataDirRelative}/Managed/Assembly-CSharp.dll";
                return null;
            }

            if (!System.IO.Directory.Exists(layout.GameDataDir))
            {
                problem = $"{root} has no GameData folder";
                return null;
            }

            return layout;
        }

        problem = $"{root} has no KSP_x64.exe, KSP.x86_64 or KSP.app with its data folder";
        return null;
    }

    public static GameLayout Require(string root)
    {
        return Detect(root, out string? problem) ?? throw new InstallException($"not a KSP install: {problem}");
    }

    /// <summary>
    /// The first layout whose <c>Managed</c> folder exists, without the checks <see cref="Detect"/> makes, for code
    /// that only reads the game's assemblies from a folder that may not be a complete install. Null when none has one.
    /// </summary>
    public static GameLayout? FindByManagedDir(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return Candidates(root).FirstOrDefault(layout => System.IO.Directory.Exists(layout.ManagedDir));
    }

    /// <summary>The first layout whose file to start exists, without the checks <see cref="Detect"/> makes.</summary>
    public static GameLayout? FindByLaunchPath(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return Candidates(root).FirstOrDefault(layout => File.Exists(layout.LaunchPath));
    }

    private static GameLayout[] Candidates(string root)
    {
        return
        [
            new(root, GamePlatform.Windows, Path.Combine(root, "KSP_x64.exe"), Path.Combine(root, "KSP_x64_Data")),
            new(root, GamePlatform.Linux, Path.Combine(root, "KSP.x86_64"), Path.Combine(root, "KSP_Data")),
            new(root, GamePlatform.MacOS, Path.Combine(root, "KSP.app"),
                Path.Combine(root, "KSP.app", "Contents", "Resources", "Data")),
        ];
    }

    // Unity names the binary after the product, which need not match the bundle's name; Info.plist says which it is.
    private static string MacBinary(string bundle)
    {
        string macOs = Path.Combine(bundle, "Contents", "MacOS");
        string standard = Path.Combine(macOs, DefaultMacBinary);
        if (File.Exists(standard))
        {
            return standard;
        }

        string plist = Path.Combine(bundle, "Contents", "Info.plist");
        try
        {
            if (File.Exists(plist) && BundleExecutable().Match(File.ReadAllText(plist)) is { Success: true } match)
            {
                string named = Path.Combine(macOs, match.Groups[1].Value.Trim());
                if (File.Exists(named))
                {
                    return named;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable plist leaves the standard name, and starting the game then reports that it does not exist.
        }

        return standard;
    }

    [GeneratedRegex(@"<key>\s*CFBundleExecutable\s*</key>\s*<string>([^<]+)</string>")]
    private static partial Regex BundleExecutable();
}
