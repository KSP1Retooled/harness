using System.Runtime.InteropServices;

namespace KspHarness.Installs;

internal static partial class UnixPaths
{
    /// <summary>
    /// The path with every symbolic link resolved, or null when it cannot be resolved. Linux and macOS report a running
    /// program's path this way (on macOS temporary folders sit under the <c>/var</c> link to <c>/private/var</c>).
    /// </summary>
    public static string? Resolve(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        IntPtr resolved = RealPath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            Free(resolved);
        }
    }

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RealPath(string path, IntPtr resolved);

    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void Free(IntPtr pointer);
}
