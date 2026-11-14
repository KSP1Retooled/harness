namespace KspHarness.Suites;

public enum TestOutcome
{
    Passed,

    /// <summary>The test or its reset threw.</summary>
    Failed,

    /// <summary>The test ran past its timeout.</summary>
    TimedOut,

    /// <summary>The game process exited or the connection dropped during the test.</summary>
    Crashed,

    /// <summary>The test could not start: the instance did not build or the game did not boot.</summary>
    Error,

    /// <summary>The run was interrupted before the test started.</summary>
    NotRun,
}

public sealed record TestResult
{
    public required string Suite { get; init; }

    public required string Test { get; init; }

    public required string Install { get; init; }

    public required TestOutcome Outcome { get; init; }

    public double Seconds { get; init; }

    /// <summary>The exception's type and message for a test that did not pass.</summary>
    public string? Error { get; init; }

    /// <summary>The folder with the test's evidence, when it wrote any.</summary>
    public string? Evidence { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record GameBoot(double ConnectSeconds, double MainMenuSeconds, int Pid, string PlayerLog);

public sealed record GroupSummary
{
    public required string Install { get; init; }

    public string? Instance { get; init; }

    public string? LockFile { get; init; }

    public double BuildSeconds { get; init; }

    public string? BuildError { get; init; }

    /// <summary>Every game started for the group, in order; more than one means a fresh game, a crash or a timeout.</summary>
    public List<GameBoot> Boots { get; init; } = [];
}

public sealed record RunSummary
{
    public required string RunId { get; init; }

    public required string StartRoot { get; init; }

    public required string StartKind { get; init; }

    public string? KspVersion { get; init; }

    public string? RetooledVersion { get; init; }

    public DateTime StartedUtc { get; init; }

    public double Seconds { get; init; }

    public int Passed { get; init; }

    public int Failed { get; init; }

    public required IReadOnlyList<GroupSummary> Groups { get; init; }

    public required IReadOnlyList<TestResult> Tests { get; init; }
}
