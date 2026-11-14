using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace KspHarness;

/// <summary>The game started but cannot be driven: the driver mod did not load or did not start listening.</summary>
public sealed class GameBootException : Exception
{
    public GameBootException(string message)
        : base(message) { }
}

/// <summary>
/// Starts the game without this process's standard handles. Windows hands a child every inheritable handle, not
/// only the redirected ones, so a game left running would otherwise hold the caller's output pipe open (a shell
/// pipeline then never ends) until it exits. Linux and macOS need nothing more: .NET opens its descriptors
/// close-on-exec, so the child keeps only the three redirected ones.
/// </summary>
internal static class GameProcess
{
    private const int StdInput = -10;
    private const int StdOutput = -11;
    private const int StdError = -12;
    private const uint HandleFlagInherit = 1;

    private static readonly Lock StartLock = new();

    public static Process Start(ProcessStartInfo start)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Process.Start(start) ?? throw new InvalidOperationException($"could not start {start.FileName}");
        }

        // Only for the moment of the start: other children of this process may rely on inheriting these handles.
        lock (StartLock)
        {
            var handles = new[] { GetStdHandle(StdInput), GetStdHandle(StdOutput), GetStdHandle(StdError) };
            var inherited = handles.Select(IsInheritable).ToArray();
            try
            {
                foreach (var handle in handles)
                {
                    SetHandleInformation(handle, HandleFlagInherit, 0);
                }

                return Process.Start(start) ?? throw new InvalidOperationException($"could not start {start.FileName}");
            }
            finally
            {
                for (var i = 0; i < handles.Length; i++)
                {
                    if (inherited[i])
                    {
                        SetHandleInformation(handles[i], HandleFlagInherit, HandleFlagInherit);
                    }
                }
            }
        }
    }

    private static bool IsInheritable(nint handle)
    {
        return GetHandleInformation(handle, out var flags) && (flags & HandleFlagInherit) != 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetHandleInformation(nint handle, out uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
}

/// <summary>
/// Drains a launched game's redirected standard output and error, keeping the last lines for error messages. The game
/// must not block on a full pipe, and nothing it prints belongs in the caller's output.
/// </summary>
internal sealed class ConsoleTail
{
    private const int MaxLines = 40;

    private readonly Queue<string> lines = new();

    public ConsoleTail(Process process)
    {
        process.OutputDataReceived += (_, e) => Add(e.Data);
        process.ErrorDataReceived += (_, e) => Add(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // A game that reads its input would otherwise wait on a pipe nobody writes.
        process.StandardInput.Close();
    }

    /// <summary>The kept lines as a suffix for an error message, or an empty string when the game printed nothing.</summary>
    public string Describe()
    {
        lock (lines)
        {
            return lines.Count == 0
                ? ""
                : $"; its last console output:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
        }
    }

    private void Add(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (lines)
        {
            if (lines.Count == MaxLines)
            {
                lines.Dequeue();
            }

            lines.Enqueue(line);
        }
    }
}

/// <summary>
/// Reads a booting game's player log for signs that the driver mod will never listen, so a launch fails with the
/// cause instead of waiting out its boot timeout. Both games print the mod DLLs they found before they instantiate
/// addons, and the mod logs a line when it refuses to start.
/// </summary>
internal sealed class ModLoadCheck
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly string? path;
    private readonly string portArgument;
    private readonly StringBuilder partial = new();
    private readonly Stopwatch sinceRead = new();
    private long position;
    private bool ours;
    private bool inDllList;
    private bool modListed;
    private bool listChecked;
    private string? failure;

    public ModLoadCheck(string? playerLog, int port)
    {
        path = playerLog;
        portArgument = "-ksp-harness-port " + port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Why the mod will not listen, once the log shows it; null while it still may.</summary>
    public string? Failure()
    {
        if (failure is not null || path is null || (sinceRead.IsRunning && sinceRead.Elapsed < Interval))
        {
            return failure;
        }

        sinceRead.Restart();
        try
        {
            ReadNewText();
        }
        catch (IOException)
        {
            // The game may hold the file in a way that refuses this read now; the next check tries again.
        }

        return failure;
    }

    /// <summary>Feeds lines as they would come from the log; for tests.</summary>
    internal string? Feed(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            OnLine(line);
        }

        return failure;
    }

    private void ReadNewText()
    {
        if (!File.Exists(path))
        {
            return;
        }

        using var stream = new FileStream(
            path!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );
        if (stream.Length < position)
        {
            // Unity truncates the log when it starts, so an older game's log may have been read first.
            position = 0;
            partial.Clear();
            ours = false;
            inDllList = false;
            modListed = false;
            listChecked = false;
        }

        stream.Position = position;
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true
        );
        var text = reader.ReadToEnd();
        position = stream.Position;

        partial.Append(text);
        var all = partial.ToString();
        var end = all.LastIndexOf('\n');
        if (end < 0)
        {
            return;
        }

        partial.Clear().Append(all, end + 1, all.Length - end - 1);
        foreach (var line in all[..end].Split('\n'))
        {
            OnLine(line.TrimEnd('\r'));
            if (failure is not null)
            {
                return;
            }
        }
    }

    private void OnLine(string line)
    {
        // Only this game's log counts: a log file reused from an earlier game has another port on its Args line.
        if (line.Contains("Args:", StringComparison.Ordinal))
        {
            ours = line.Contains(portArgument, StringComparison.Ordinal);
            return;
        }

        if (!ours)
        {
            return;
        }

        if (
            line.StartsWith("[KspHarness] Not started", StringComparison.Ordinal)
            || line.StartsWith("[KspHarness] Cannot listen", StringComparison.Ordinal)
        )
        {
            failure = $"the driver mod did not start: {line.Trim()}";
        }
        else if (line.Contains("AssemblyLoader: Exception loading 'KspHarness.Mod'", StringComparison.Ordinal))
        {
            failure = $"the game could not load the driver mod: {line.Trim()}";
        }
        else if (!listChecked && line.StartsWith("Mod DLLs found:", StringComparison.Ordinal))
        {
            inDllList = true;
        }
        else if (inDllList && line.StartsWith("KspHarness.Mod ", StringComparison.Ordinal))
        {
            modListed = true;
        }
        else if (inDllList && line.StartsWith("Folders and files in GameData:", StringComparison.Ordinal))
        {
            inDllList = false;
            listChecked = true;
            if (!modListed)
            {
                failure =
                    "the game loaded without the driver mod: KSP's list of mod DLLs has no KspHarness.Mod, so "
                    + "GameData/KspHarness/Plugins/KspHarness.Mod.dll is missing or was not loaded";
            }
        }
    }
}
