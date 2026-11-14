using System.Text.RegularExpressions;

namespace KspHarness.Suites;

/// <summary>The suite runner's command line.</summary>
public sealed record SuiteRunnerOptions
{
    public const string StartVariable = "KSPHARNESS_START";

    public const string Usage = """
        Usage: <suite exe> [options]
          --start <dir>     starting install to build instances from (default: KSPHARNESS_START)
          --home <dir>      harness home for instances, caches and runs (default: KSPHARNESS_HOME)
          --filter <regex>  run only tests whose Suite.Test name matches
          --locks <dir>     folder with the lock files (default: the one the suite assembly names)
          --approved <dir>  folder with the approved files (default: the one the suite assembly names)
          --keep-game       leave the last game running at the end
          --list            list the tests and exit
        Exit codes: 0 all passed, 1 a test did not pass, 2 bad arguments or nothing to run.
        """;

    public string? Start { get; init; }

    public string? Home { get; init; }

    public Regex? Filter { get; init; }

    public string? Locks { get; init; }

    /// <summary>The approved-files folder, which becomes <c>ApprovalContext.DirectoryOverride</c>.</summary>
    public string? Approved { get; init; }

    public bool KeepGame { get; init; }

    public bool List { get; init; }

    public bool Help { get; init; }

    /// <exception cref="ArgumentException">An option is unknown or lacks its value.</exception>
    public static SuiteRunnerOptions Parse(IReadOnlyList<string> args)
    {
        var options = new SuiteRunnerOptions { Start = Environment.GetEnvironmentVariable(StartVariable) };
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--start":
                    options = options with { Start = Value(args, ref i) };
                    break;
                case "--home":
                    options = options with { Home = Value(args, ref i) };
                    break;
                case "--filter":
                    options = options with { Filter = new Regex(Value(args, ref i), RegexOptions.IgnoreCase) };
                    break;
                case "--locks":
                    options = options with { Locks = Value(args, ref i) };
                    break;
                case "--approved":
                    options = options with { Approved = Value(args, ref i) };
                    break;
                case "--keep-game":
                    options = options with { KeepGame = true };
                    break;
                case "--list":
                    options = options with { List = true };
                    break;
                case "-h" or "--help":
                    options = options with { Help = true };
                    break;
                default:
                    throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }

        return options;
    }

    private static string Value(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{args[i]} needs a value");
        }

        i++;
        return args[i];
    }
}
