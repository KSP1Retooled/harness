using System.ComponentModel;
using System.Runtime.InteropServices;

namespace KspHarness.Installs;

public enum LinkResult
{
    Linked,

    /// <summary>
    /// This file cannot be linked (too many links already, or no write-attributes access to it, which creating a link
    /// needs); others may still link.
    /// </summary>
    FileRefused,

    /// <summary>The volume cannot link to that source at all: different volumes or no hard link support.</summary>
    VolumeRefused,
}

internal static partial class HardLinks
{
    private const int WindowsNotSameDevice = 17;
    private const int WindowsInvalidFunction = 1;
    private const int WindowsAccessDenied = 5;
    private const int WindowsNotSupported = 50;
    private const int WindowsTooManyLinks = 1142;

    private const int UnixOperationNotPermitted = 1;
    private const int UnixAccessDenied = 13;
    private const int UnixTooManyLinks = 31;
    private const int UnixCrossDevice = 18;
    private const int LinuxNotSupported = 95;
    private const int MacNotSupported = 45;
    private const int MacOperationNotSupported = 102;

    public static LinkResult TryCreate(string existing, string link)
    {
        if (OperatingSystem.IsWindows())
        {
            if (CreateHardLinkW(LongPath(link), LongPath(existing), IntPtr.Zero))
            {
                return LinkResult.Linked;
            }

            int error = Marshal.GetLastPInvokeError();
            return error switch
            {
                WindowsTooManyLinks or WindowsAccessDenied => LinkResult.FileRefused,
                WindowsNotSameDevice or WindowsInvalidFunction or WindowsNotSupported => LinkResult.VolumeRefused,
                _ => throw new IOException($"cannot link {link} to {existing}: {new Win32Exception(error).Message}"),
            };
        }

        if (UnixLink(existing, link) == 0)
        {
            return LinkResult.Linked;
        }

        int errno = Marshal.GetLastPInvokeError();
        return errno switch
        {
            UnixTooManyLinks or UnixAccessDenied => LinkResult.FileRefused,
            UnixCrossDevice or UnixOperationNotPermitted => LinkResult.VolumeRefused,
            LinuxNotSupported when OperatingSystem.IsLinux() => LinkResult.VolumeRefused,
            MacNotSupported or MacOperationNotSupported when OperatingSystem.IsMacOS() => LinkResult.VolumeRefused,
            _ => throw new IOException($"cannot link {link} to {existing}: errno {errno}"),
        };
    }

    // CreateHardLinkW is not covered by .NET's automatic long path handling.
    private static string LongPath(string path)
    {
        return path.Length < 240 || path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateHardLinkW",
        StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true
    )]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int UnixLink(string oldPath, string newPath);
}
