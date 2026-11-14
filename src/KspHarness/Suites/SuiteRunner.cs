using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using KspHarness.Approvals;
using KspHarness.Installs;

namespace KspHarness.Suites;

/// <summary>
/// Runs the suites in a test assembly: one instance and one game per install definition, tests in source order, a
/// line per test on the console and a JSON summary under <c>&lt;home&gt;/runs/&lt;run id&gt;/</c>.
/// </summary>
public static class SuiteRunner
{
    /// <summary>
    /// The <c>AssemblyMetadata</c> key a suite assembly uses to name the folder with its lock files, normally the
    /// folder that holds its install definitions.
    /// </summary>
    public const string LockDirectoryKey = "KspHarness.LockDirectory";

    private static readonly JsonSerializerOptions SummaryJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> RunAsync(string[] args, Assembly assembly)
    {
        var output = Console.Out;
        SuiteRunnerOptions options;
        IReadOnlyList<SuiteGroup> groups;
        try
        {
            options = SuiteRunnerOptions.Parse(args);
            groups = SuiteDiscovery.Find(assembly);
        }
        catch (ArgumentException e)
        {
            await Console.Error.WriteLineAsync(e.Message + Environment.NewLine + SuiteRunnerOptions.Usage);
            return 2;
        }
        catch (InvalidOperationException e)
        {
            await Console.Error.WriteLineAsync($"the suites are not valid: {e.Message}");
            return 2;
        }
        catch (ReflectionTypeLoadException e)
        {
            var reasons = e.LoaderExceptions.OfType<Exception>().Select(loader => loader.Message).Distinct();
            await Console.Error.WriteLineAsync(
                $"the suite assembly's types did not load: {string.Join(Environment.NewLine, reasons)}");
            return 2;
        }

        if (options.Help)
        {
            await output.WriteLineAsync(SuiteRunnerOptions.Usage);
            return 0;
        }

        groups = Filter(groups, options);
        if (options.List)
        {
            foreach (var group in groups)
            {
                await output.WriteLineAsync(group.Name);
                foreach (var test in group.Tests)
                {
                    var fresh = test.Attribute.FreshGame ? "  (fresh game)" : "";
                    await output.WriteLineAsync($"  {test.FullName}{fresh}");
                }
            }

            return 0;
        }

        if (groups.Count == 0)
        {
            await Console.Error.WriteLineAsync("no tests to run" + (options.Filter is null ? "" : " match the filter"));
            return 2;
        }

        if (string.IsNullOrEmpty(options.Start))
        {
            await Console.Error.WriteLineAsync(
                $"name a starting install with --start or {SuiteRunnerOptions.StartVariable}"
                + Environment.NewLine + SuiteRunnerOptions.Usage);
            return 2;
        }

        StartingInstall start;
        try
        {
            start = StartingInstall.Open(options.Start);
        }
        catch (Exception e) when (e is InstallException or IOException)
        {
            await Console.Error.WriteLineAsync($"not a usable starting install: {e.Message}");
            return 2;
        }

        if (options.Approved is not null)
        {
            ApprovalContext.DirectoryOverride = Path.GetFullPath(options.Approved);
        }

        var home = options.Home is null ? HarnessHome.FromEnvironment() : new HarnessHome(options.Home);
        var locks = options.Locks ?? LockDirectory(assembly) ?? Path.Combine(Environment.CurrentDirectory, "locks");
        var startedUtc = DateTime.UtcNow;
        var runsRoot = Path.Combine(home.Root, "runs");
        var runId = CreateRunDirectory(runsRoot, $"{startedUtc.ToLocalTime():yyyyMMdd-HHmmss}-{start.Kind}");
        var runDir = Path.Combine(runsRoot, runId);

        using var interrupt = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            interrupt.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        var clock = Stopwatch.StartNew();
        var testCount = groups.Sum(group => group.Tests.Count);
        await output.WriteLineAsync(
            $"run {runId}: {testCount} tests in {groups.Count} installs on {start.Kind} {start.KspVersion}"
            + (start.RetooledVersion is null ? "" : $" (Retooled {start.RetooledVersion})") + $" from {start.Root}");

        var results = new List<TestResult>();
        var summaries = new List<GroupSummary>();
        try
        {
            for (int i = 0; i < groups.Count; i++)
            {
                if (interrupt.IsCancellationRequested)
                {
                    results.AddRange(await GroupRun.NotRunAsync(groups[i], output));
                    summaries.Add(new GroupSummary { Install = groups[i].Name });
                    continue;
                }

                var keepGame = options.KeepGame && i == groups.Count - 1;
                await using var run = new GroupRun(groups[i], start, home, runDir, locks, output, keepGame);
                results.AddRange(await run.RunAsync(interrupt.Token));
                summaries.Add(run.Summary);
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        var passed = results.Count(result => result.Outcome == TestOutcome.Passed);
        var summary = new RunSummary
        {
            RunId = runId,
            StartRoot = start.Root,
            StartKind = start.Kind,
            KspVersion = start.KspVersion,
            RetooledVersion = start.RetooledVersion,
            StartedUtc = startedUtc,
            Seconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
            Passed = passed,
            Failed = results.Count - passed,
            Groups = summaries,
            Tests = results,
        };

        var summaryPath = Path.Combine(runDir, "summary.json");
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, SummaryJson));
        await output.WriteLineAsync(
            $"{passed} passed, {results.Count - passed} did not pass, in {FormatDuration(clock.Elapsed)}; {summaryPath}");
        return passed == results.Count ? 0 : 1;
    }

    internal static string FormatDuration(TimeSpan time)
    {
        return time.TotalMinutes >= 1
            ? $"{(int)time.TotalMinutes}m{time.Seconds:00}s"
            : time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
    }

    /// <summary>
    /// Creates the run's folder and returns its id: <paramref name="baseId"/>, or with "-2", "-3" when another run took
    /// that one. A run claims its folder by creating <c>.run</c> in it, so runs started in the same second by separate
    /// processes do not share one.
    /// </summary>
    internal static string CreateRunDirectory(string runsRoot, string baseId)
    {
        for (int number = 1; ; number++)
        {
            var id = number == 1 ? baseId : $"{baseId}-{number}";
            var dir = Path.Combine(runsRoot, id);
            if (Directory.Exists(dir))
            {
                continue;
            }

            Directory.CreateDirectory(dir);
            try
            {
                using var claim = new StreamWriter(new FileStream(Path.Combine(dir, ".run"), FileMode.CreateNew));
                claim.Write(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                return id;
            }
            catch (IOException) when (File.Exists(Path.Combine(dir, ".run")))
            {
                // Another process created the folder between the check and the claim.
            }
        }
    }

    private static IReadOnlyList<SuiteGroup> Filter(IReadOnlyList<SuiteGroup> groups, SuiteRunnerOptions options)
    {
        if (options.Filter is null)
        {
            return groups;
        }

        return groups
            .Select(group => group with { Tests = group.Tests.Where(test => options.Filter.IsMatch(test.FullName)).ToList() })
            .Where(group => group.Tests.Count > 0)
            .ToList();
    }

    private static string? LockDirectory(Assembly assembly)
    {
        return assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == LockDirectoryKey)?.Value;
    }
}
