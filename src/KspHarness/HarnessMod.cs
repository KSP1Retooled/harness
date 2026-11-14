using KspHarness.Installs;

namespace KspHarness;

/// <summary>Finds the driver mod that ships with this library, and deploys it into instances.</summary>
public static class HarnessMod
{
    public const string FolderName = "KspHarness";

    public const string AssemblyFileName = "KspHarness.Mod.dll";

    /// <summary>The mod's DLL relative to its <c>GameData/KspHarness</c> folder.</summary>
    public static readonly string AssemblyRelativePath = Path.Combine("Plugins", AssemblyFileName);

    /// <summary>
    /// The mod's <c>GameData/KspHarness</c> folder next to this library. It is there when the library was built with
    /// <c>KSP_DIR</c> set.
    /// </summary>
    public static string Folder { get; } = Path.Combine(
        Path.GetDirectoryName(typeof(HarnessMod).Assembly.Location) ?? AppContext.BaseDirectory,
        "mod",
        "GameData",
        FolderName);

    /// <summary>Copies the mod into the instance's GameData unless the same files are already there.</summary>
    public static bool DeployTo(Instance instance) => DeployTo(instance, Folder);

    /// <summary>
    /// Copies a mod folder (normally <see cref="Folder"/>) into the instance. A folder without the mod's DLL is
    /// refused, and the instance keeps the copy it had.
    /// </summary>
    /// <exception cref="FileNotFoundException">The folder has no <c>Plugins/KspHarness.Mod.dll</c>.</exception>
    public static bool DeployTo(Instance instance, string folder)
    {
        if (!File.Exists(Path.Combine(folder, AssemblyRelativePath)))
        {
            throw new FileNotFoundException(
                $"the driver mod is not in {folder}; build KspHarness with KSP_DIR set to a stock KSP 1.12.5 install",
                Path.Combine(folder, AssemblyRelativePath));
        }

        return instance.DeployMod(folder, AssemblyRelativePath);
    }

    /// <summary>
    /// The driver mod DLL in an instance's GameData, or null when there is none. The game cannot be driven without it.
    /// </summary>
    public static string? FindIn(string instanceDir)
    {
        var expected = Path.Combine(instanceDir, "GameData", FolderName, AssemblyRelativePath);
        if (File.Exists(expected))
        {
            return expected;
        }

        var gameData = Path.Combine(instanceDir, "GameData");
        if (!Directory.Exists(gameData))
        {
            return null;
        }

        return Directory.EnumerateFiles(gameData, AssemblyFileName, SearchOption.AllDirectories).FirstOrDefault();
    }
}
