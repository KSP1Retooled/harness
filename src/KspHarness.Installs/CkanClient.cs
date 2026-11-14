using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace KspHarness.Installs;

/// <summary>
/// Runs the CKAN command line against one game folder. Each call starts <c>ckan</c> with <c>--headless</c> and
/// <c>--gamedir</c>, which leaves CKAN's list of known instances alone. CKAN keeps its downloads and repository data
/// in its own per-user cache, shared by every instance.
/// </summary>
public sealed class CkanClient
{
    public const string EnvironmentVariable = "CKAN_EXE";

    private readonly string executable;
    private readonly string gameDir;
    private readonly string logPath;
    private readonly TimeSpan timeout;

    public CkanClient(string executable, string gameDir, string logPath, TimeSpan timeout)
    {
        this.executable = executable;
        this.gameDir = gameDir;
        this.logPath = logPath;
        this.timeout = timeout;
    }

    /// <summary>
    /// The option, else <c>CKAN_EXE</c>, else <c>ckan.exe</c> on the PATH on Windows, or <c>ckan</c> (a wrapper script
    /// or package) then <c>ckan.exe</c> on the PATH elsewhere. Outside Windows a <c>.exe</c> runs under Mono, see
    /// <see cref="CommandFor"/>.
    /// </summary>
    public static string FindExecutable(string? configured)
    {
        string? path = configured ?? Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
            {
                throw new InstallException($"CKAN executable not found: {path}");
            }

            return Path.GetFullPath(path);
        }

        string[] names = OperatingSystem.IsWindows() ? ["ckan.exe"] : ["ckan", "ckan.exe"];
        string[] searchPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        return names
                .SelectMany(name => searchPath.Where(dir => dir.Length > 0).Select(dir => Path.Combine(dir, name)))
                .FirstOrDefault(File.Exists)
            ?? throw new InstallException(
                $"CKAN not found: set {EnvironmentVariable} or put {string.Join(" or ", names)} on the PATH "
                    + "(CKAN 1.36 or later)"
            );
    }

    /// <summary>
    /// The program to start for a CKAN executable and the arguments that go before CKAN's own. CKAN ships one .NET
    /// Framework <c>ckan.exe</c> for every platform, which Linux and macOS run with <c>mono</c> from the PATH.
    /// </summary>
    internal static (string FileName, IReadOnlyList<string> Prefix) CommandFor(string executable, bool windows)
    {
        bool framework = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        return framework && !windows ? ("mono", [executable]) : (executable, []);
    }

    /// <summary>Refreshes the repository metadata CKAN resolves modules from.</summary>
    public Task UpdateAsync(CancellationToken cancel) => RunAsync(["update"], cancel);

    public Task AddCompatibleVersionsAsync(IEnumerable<string> versions, CancellationToken cancel)
    {
        return RunAsync(["compat", "add", .. versions], cancel);
    }

    /// <summary>Installs all the modules in one CKAN transaction. Recommended modules are left out so that only
    /// what the definition names and what it depends on gets installed.</summary>
    public Task InstallAsync(IEnumerable<string> specs, CancellationToken cancel)
    {
        return RunAsync(["install", "--no-recommends", .. specs], cancel);
    }

    /// <summary>Moves installed modules to the given versions (<c>identifier=version</c>).</summary>
    public Task UpgradeAsync(IEnumerable<string> specs, CancellationToken cancel)
    {
        return RunAsync(["upgrade", "--no-recommends", .. specs], cancel);
    }

    /// <summary>
    /// The modules CKAN installed, from <c>CKAN/registry.json</c>. Modules CKAN only detected (loose DLLs, DLC) are
    /// not in this list. The registry is read directly because <c>ckan list --porcelain</c> mixes in those detected
    /// modules and marks rows only by a status character.
    /// </summary>
    public static IReadOnlyList<LockedModule> ReadInstalled(string gameDir)
    {
        string registry = Path.Combine(gameDir, "CKAN", "registry.json");
        if (!File.Exists(registry))
        {
            return [];
        }

        using var stream = File.OpenRead(registry);
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("installed_modules", out var installed))
        {
            return [];
        }

        var modules = new List<LockedModule>();
        foreach (var entry in installed.EnumerateObject())
        {
            var source = entry.Value.GetProperty("source_module");

            // CKAN registers detected DLC as installed modules; they cannot be installed, so they are not locked.
            if (source.TryGetProperty("kind", out var kind) && kind.GetString() == "dlc")
            {
                continue;
            }

            string identifier = source.GetProperty("identifier").GetString() ?? entry.Name;
            string version =
                source.GetProperty("version").GetString()
                ?? throw new InstallException($"{registry}: {identifier} has no version");
            modules.Add(new LockedModule(identifier, version));
        }

        return modules.OrderBy(module => module.Identifier, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task RunAsync(IReadOnlyList<string> command, CancellationToken cancel)
    {
        var (fileName, prefix) = CommandFor(executable, OperatingSystem.IsWindows());
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        string[] arguments = [.. command, "--headless", "--gamedir", gameDir];
        foreach (string argument in prefix.Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await using var log = new StreamWriter(logPath, append: true, Encoding.UTF8);
        string commandLine = string.Join(' ', arguments.Select(Quote));
        await log.WriteLineAsync($"=== {DateTime.UtcNow:O} ckan {commandLine}");

        using var userLock = await AcquireUserLockAsync(log, cancel);

        using var process = new Process { StartInfo = start };
        var gate = new object();
        void Write(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (gate)
            {
                log.WriteLine(line);
            }
        }

        process.OutputDataReceived += (_, e) => Write(e.Data);
        process.ErrorDataReceived += (_, e) => Write(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception e)
        {
            string runner = prefix.Count > 0 ? $" with {fileName} (Mono, which runs ckan.exe outside Windows)" : "";
            throw new InstallException($"cannot start CKAN at {executable}{runner}: {e.Message}", e);
        }

        // Headless CKAN should never prompt; a closed input makes one fail instead of hang.
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            cancel.ThrowIfCancellationRequested();
            lock (gate)
            {
                log.WriteLine($"=== timed out after {timeout}");
            }

            await log.DisposeAsync();
            throw Failure(command, -1, $"timed out after {timeout}");
        }

        // WaitForExit without a timeout drains the asynchronous output readers.
        process.WaitForExit();
        lock (gate)
        {
            log.WriteLine($"=== exit {process.ExitCode}");
        }

        // Closed before Failure reads the tail back.
        await log.DisposeAsync();
        if (process.ExitCode != 0)
        {
            throw Failure(command, process.ExitCode, $"exited with {process.ExitCode}");
        }
    }

    /// <summary>
    /// The lock that keeps CKAN runs of this user apart. CKAN locks only the registry of the game folder it is given;
    /// its repository data and download cache are per user, and two runs at once (two builds of different instances)
    /// break each other's update or download.
    /// </summary>
    internal static string UserLockPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KspHarness",
            "ckan.lock"
        );

    private async Task<FileStream> AcquireUserLockAsync(StreamWriter log, CancellationToken cancel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserLockPath)!);
        var waited = Stopwatch.StartNew();
        bool logged = false;

        while (true)
        {
            try
            {
                return new FileStream(UserLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (waited.Elapsed < timeout)
            {
                if (!logged)
                {
                    await log.WriteLineAsync($"=== waiting for another CKAN run ({UserLockPath})");
                    await log.FlushAsync(cancel);
                    logged = true;
                }
            }
            catch (IOException e)
            {
                throw new InstallException($"another CKAN run held {UserLockPath} for {timeout}", e);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancel);
        }
    }

    private CkanException Failure(IReadOnlyList<string> command, int exitCode, string what)
    {
        const int tailLines = 30;
        var lines = File.ReadAllLines(logPath);
        int lastHeader = Array.FindLastIndex(
            lines,
            line =>
                line.StartsWith("=== ", StringComparison.Ordinal) && line.Contains(" ckan ", StringComparison.Ordinal)
        );
        var tail = lines.Skip(Math.Max(Math.Max(lastHeader, 0), lines.Length - tailLines));
        string message = $"ckan {command[0]} {what} for {gameDir}. Log: {logPath}\n" + string.Join('\n', tail);
        return new CkanException(message, logPath, exitCode);
    }

    private static string Quote(string argument)
    {
        return argument.Contains(' ') ? $"\"{argument}\"" : argument;
    }
}
