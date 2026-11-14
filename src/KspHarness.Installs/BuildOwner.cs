using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace KspHarness.Installs;

/// <summary>Names the process that owns a build folder: its id and start time, since ids are reused.</summary>
internal static class BuildOwner
{
    public static string Current()
    {
        using var self = Process.GetCurrentProcess();
        return Describe(self.Id, self.StartTime);
    }

    public static string Describe(int processId, DateTime startTime)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{processId} {startTime.ToUniversalTime().Ticks}");
    }

    /// <summary>Whether the process the text names still runs. Text that cannot be read counts as a live owner.</summary>
    public static bool IsAlive(string text)
    {
        string[] parts = text.Trim().Split(' ');
        if (parts.Length != 2
            || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out int processId)
            || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out long startTicks))
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // The process exited meanwhile, or belongs to someone whose start time this user may not read.
            return e is InvalidOperationException;
        }
    }
}
