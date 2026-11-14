using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace KspHarness.Installs.Tests;

/// <summary>A temporary folder deleted at the end of the test.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "ksph-tests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    public string Write(string relative, string content)
    {
        string path = Combine(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
    }
}

internal static class FakeInstall
{
    /// <summary>The layout of the players this machine runs.</summary>
    public static GamePlatform HostPlatform =>
        OperatingSystem.IsWindows() ? GamePlatform.Windows
        : OperatingSystem.IsMacOS() ? GamePlatform.MacOS
        : GamePlatform.Linux;

    /// <summary>The smallest folder StartingInstall accepts, with one file of each kind the copier treats apart.</summary>
    public static string Create(
        TempDir dir,
        string name = "start",
        bool retooled = false,
        GamePlatform platform = GamePlatform.Windows
    )
    {
        string root = dir.Combine(name);
        void Write(string relative, string content)
        {
            string path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        var (executable, player, data) = Player(platform);
        Write(executable, "exe");
        Write(player, "player");
        Write(data + "/Managed/Assembly-CSharp.dll", "game code");
        Write(data + "/sharedassets0.assets", "assets");
        Write(data + "/level0", "level");
        Write("GameData/Squad/Parts/part.cfg", "PART {}");
        Write("GameData/Squad/Parts/texture.dds", "texture");
        Write("GameData/Squad/Plugins/Squad.dll", "plugin");
        Write("Ships/@thumbs/VAB/craft.png", "thumbnail");
        Write("Ships/VAB/craft.craft", "ship = craft");
        Write("readme.txt", retooled ? "Version 1.13.0\n" : "Version 1.12.5\n");
        string build = retooled
            ? "[config]\nbuild id = 10837\nretooled-version = 1.13.0-rc8\n"
            : "[config]\nbuild id = 03190\n";
        Write("buildID64.txt", build);
        if (retooled)
        {
            Write("GameData/Retooled/Localization/en.cfg", "Localization {}");
        }

        return root;
    }

    /// <summary>The executable to start, Unity's player library and the data folder, relative to the root.</summary>
    public static (string Executable, string PlayerLibrary, string DataDir) Player(GamePlatform platform)
    {
        return platform switch
        {
            GamePlatform.Windows => ("KSP_x64.exe", "UnityPlayer.dll", "KSP_x64_Data"),
            GamePlatform.Linux => ("KSP.x86_64", "UnityPlayer.so", "KSP_Data"),
            _ => (
                "KSP.app/Contents/MacOS/KSP",
                "KSP.app/Contents/Frameworks/UnityPlayer.dylib",
                "KSP.app/Contents/Resources/Data"
            ),
        };
    }
}

internal static class FileIdentity
{
    /// <summary>Volume serial and file index: equal for two names of the same file. Windows only.</summary>
    public static (uint Volume, ulong Index, uint Links) Get(string path)
    {
        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.NumberOfLinks);
    }

    public static bool SameFile(string a, string b)
    {
        var x = Get(a);
        var y = Get(b);
        return x.Volume == y.Volume && x.Index == y.Index;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle file,
        out ByHandleFileInformation information
    );
}

internal static class Zips
{
    public static string Create(string path, params (string Name, string Content)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }

        return path;
    }
}
