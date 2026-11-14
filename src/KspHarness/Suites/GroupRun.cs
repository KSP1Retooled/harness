using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using KspHarness.Installs;

namespace KspHarness.Suites;

/// <summary>
/// The tests of one install definition: builds or reuses the instance, keeps one game running for them, and restarts
/// it when a test asks for a fresh one or leaves it dead or stuck.
/// </summary>
internal sealed class GroupRun : IAsyncDisposable
{
    private static readonly TimeSpan EvidenceTimeout = TimeSpan.FromSeconds(30);
    private const int PlayerLogTailLines = 400;

    private readonly SuiteGroup group;
    private readonly StartingInstall start;
    private readonly HarnessHome home;
    private readonly string runDir;
    private readonly string lockDir;
    private readonly TextWriter output;
    private readonly bool keepGame;

    private string? lockFile;
    private Instance? instance;
    private GameSession? game;
    private string? gamePlayerLog;
    private int testsOnGame;
    private int bootAttempts;
    private PopupPolicy popups = PopupPolicy.Default;
    private int popupsNoted;
    private Exception? bootFailure;

    // The current test's reset failure, while the body restarts the game because of it.
    private Exception? failedReset;

    public GroupRun(
        SuiteGroup group,
        StartingInstall start,
        HarnessHome home,
        string runDir,
        string lockDir,
        TextWriter output,
        bool keepGame
    )
    {
        this.group = group;
        this.start = start;
        this.home = home;
        this.runDir = runDir;
        this.lockDir = lockDir;
        this.output = output;
        this.keepGame = keepGame;
        Summary = new GroupSummary { Install = group.Name };
    }

    public GroupSummary Summary { get; private set; }

    /// <summary>The group's tests as not run, for a group that the run was interrupted before it reached.</summary>
    public static async Task<IReadOnlyList<TestResult>> NotRunAsync(SuiteGroup group, TextWriter output)
    {
        await output.WriteLineAsync($"{group.Name}: {group.Tests.Count} tests not run, the run was interrupted");
        return group
            .Tests.Select(test => new TestResult
            {
                Suite = test.Suite.Name,
                Test = test.Method.Name,
                Install = test.Install.Name,
                Outcome = TestOutcome.NotRun,
            })
            .ToList();
    }

    public async Task<IReadOnlyList<TestResult>> RunAsync(CancellationToken interrupt)
    {
        var results = new List<TestResult>();
        var definition = (InstallDefinition)Activator.CreateInstance(group.Install)!;
        popups = PopupPolicy.For(definition);
        lockFile = Path.Combine(lockDir, $"{definition.Name}.{start.Kind}.lock.json");
        Directory.CreateDirectory(lockDir);

        await output.WriteLineAsync($"{group.Name}: {group.Tests.Count} tests; building the instance");
        var clock = Stopwatch.StartNew();
        try
        {
            var options = new InstanceBuildOptions { Log = line => output.WriteLine("    " + line) };
            instance = await InstanceBuilder.BuildAsync(definition, start, home, lockFile, options, interrupt);
            Summary = Summary with
            {
                Instance = instance.Root,
                LockFile = lockFile,
                BuildSeconds = Seconds(clock.Elapsed),
            };
            await output.WriteLineAsync(
                $"  instance {instance.Name} ready after {SuiteRunner.FormatDuration(clock.Elapsed)}"
            );
        }
        catch (Exception e) when (!interrupt.IsCancellationRequested)
        {
            Summary = Summary with
            {
                LockFile = lockFile,
                BuildSeconds = Seconds(clock.Elapsed),
                BuildError = e.Message,
            };
            await output.WriteLineAsync($"  the instance did not build: {e.Message}");
            var evidence = new TestEvidence(Path.Combine(runDir, group.Name));
            evidence.Write("build-error.txt", e.ToString());
            if (e is CkanException { LogPath: var ckanLog } && File.Exists(ckanLog))
            {
                evidence.CopyFile("ckan.log", ckanLog);
            }

            foreach (var test in group.Tests)
            {
                results.Add(await ReportAsync(test, TestOutcome.Error, TimeSpan.Zero, e, evidence));
            }

            return results;
        }
        catch (Exception) when (interrupt.IsCancellationRequested)
        {
            Summary = Summary with
            {
                LockFile = lockFile,
                BuildSeconds = Seconds(clock.Elapsed),
                BuildError = "interrupted",
            };
            await output.WriteLineAsync("  interrupted while the instance was building");
            foreach (var test in group.Tests)
            {
                results.Add(await ReportAsync(test, TestOutcome.NotRun, TimeSpan.Zero, null, null));
            }

            return results;
        }

        foreach (var test in group.Tests)
        {
            if (interrupt.IsCancellationRequested)
            {
                results.Add(await ReportAsync(test, TestOutcome.NotRun, TimeSpan.Zero, null, null));
                continue;
            }

            results.Add(await RunTestAsync(test, interrupt));
        }

        return results;
    }

    public async ValueTask DisposeAsync()
    {
        if (game is not null && keepGame && IsAlive(game))
        {
            await output.WriteLineAsync(
                $"  left the game running: pid {game.Process?.Id}, port {game.Port}, token {game.Token}, "
                    + $"instance {game.InstanceDir}"
            );
            return;
        }

        await StopGameAsync();
    }

    private async Task<TestResult> RunTestAsync(SuiteTest test, CancellationToken interrupt)
    {
        var evidence = new TestEvidence(Path.Combine(runDir, test.Suite.Name, test.Method.Name));
        failedReset = null;
        if (game is not null && !IsAlive(game))
        {
            evidence.Note("the game died after the previous test finished; starting a new one");
        }

        GameSession session;
        try
        {
            session = await EnsureGameAsync(test.Attribute.FreshGame, interrupt);
        }
        catch (Exception e) when (!interrupt.IsCancellationRequested)
        {
            evidence.Write("exception.txt", e.ToString());
            CopyPlayerLogTail(evidence);
            return await ReportAsync(test, TestOutcome.Error, TimeSpan.Zero, e, evidence);
        }
        catch (OperationCanceledException)
        {
            return await ReportAsync(test, TestOutcome.NotRun, TimeSpan.Zero, null, null);
        }

        KspSuite suite;
        MethodInfo method;
        try
        {
            (suite, method) = new InstanceSuites(instance!.ManagedDir).Create(test);
        }
        catch (Exception e)
        {
            evidence.Write("exception.txt", e.ToString());
            return await ReportAsync(test, TestOutcome.Error, TimeSpan.Zero, Unwrap(e), evidence);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(interrupt);
        deadline.CancelAfter(TimeSpan.FromSeconds(test.Attribute.TimeoutSeconds));
        suite.Game = session;
        suite.Instance = instance!;
        suite.Start = start;
        suite.Evidence = evidence;
        suite.Cancel = deadline.Token;

        var firstGame = session;
        var firstCursor = session.Log.Cursor;
        var clock = Stopwatch.StartNew();
        Exception? failure = null;
        var body = RunBodyAsync(suite, method, test, deadline.Token);
        try
        {
            await body.WaitAsync(deadline.Token);
        }
        catch (Exception e)
        {
            failure = Unwrap(e);
        }

        clock.Stop();
        testsOnGame++;
        NotePopups(suite);

        // A body abandoned at its deadline may still fail later, once its game is stopped.
        _ = body.ContinueWith(task => task.Exception, TaskScheduler.Default);

        // Ctrl+C cancels the body too; what it was doing then is not a result. The game is left to DisposeAsync, which
        // keeps it for --keep-game.
        if (failure is not null && interrupt.IsCancellationRequested)
        {
            evidence.Note($"interrupted after {SuiteRunner.FormatDuration(clock.Elapsed)}");
            return await ReportAsync(test, TestOutcome.NotRun, clock.Elapsed, null, evidence);
        }

        // A game that dies once the last call of a body has returned would otherwise pass the test and be replaced by
        // the next one without a word.
        if (failure is null && (game is null || !IsAlive(game)))
        {
            failure = new InvalidOperationException("the game exited or disconnected after the test's body returned");
        }
        else if (
            failure is null
            && firstGame.Log.Since(firstCursor).OfType<CrashingEvent>().FirstOrDefault() is { } crash
        )
        {
            evidence.Note($"the game reported an unhandled exception but kept running: {FirstLine(crash.Message)}");
        }

        var outcome = TestOutcome.Passed;
        if (failure is not null)
        {
            // While the game restarts after a failed reset there is no game, and that is not a crash.
            var restarting = failedReset is not null && game is null;
            var timedOut = deadline.IsCancellationRequested;
            if (timedOut)
            {
                failure = restarting
                    ? new TimeoutException(
                        $"the test did not finish within {test.Attribute.TimeoutSeconds} s: the game was still "
                            + "restarting after the reset failed",
                        failedReset
                    )
                    : new TimeoutException(
                        $"the test did not finish within {test.Attribute.TimeoutSeconds} s",
                        failure is OperationCanceledException ? null : failure
                    );
            }

            outcome =
                timedOut && restarting ? TestOutcome.TimedOut
                : restarting ? TestOutcome.Error
                : game is null || !IsAlive(game) ? TestOutcome.Crashed
                : timedOut ? TestOutcome.TimedOut
                : TestOutcome.Failed;

            await CollectEvidenceAsync(evidence, failure, firstGame, firstCursor);
            if (outcome is TestOutcome.Crashed or TestOutcome.TimedOut)
            {
                // A dead game must be replaced; a stuck one may still run the abandoned snippet.
                await StopGameAsync();
            }
        }

        return await ReportAsync(test, outcome, clock.Elapsed, failure, evidence);
    }

    private async Task RunBodyAsync(
        KspSuite suite,
        MethodInfo method,
        SuiteTest test,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await suite.ResetAsync();
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            suite.Evidence.Note($"reset failed ({e.GetType().Name}: {FirstLine(e.Message)}); restarting the game");
            failedReset = e;
            await StopGameAsync();
            suite.Game = await EnsureGameAsync(fresh: false, cancellationToken);
            await suite.ResetAsync();
        }

        suite.TestStart = suite.Game.Log.Cursor;
        var task =
            (Task?)method.Invoke(suite, null)
            ?? throw new InvalidOperationException($"{test.FullName} returned no task");
        await task;
    }

    private async Task<GameSession> EnsureGameAsync(bool fresh, CancellationToken cancellationToken)
    {
        if (game is not null && ((fresh && testsOnGame > 0) || !IsAlive(game)))
        {
            await StopGameAsync();
        }

        if (game is not null)
        {
            return game;
        }

        if (bootFailure is not null)
        {
            throw new InvalidOperationException("the game did not boot earlier in this install's tests", bootFailure);
        }

        // One retry: a first boot can fail for reasons that do not repeat (a shader cache, a slow disk).
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await BootAsync(cancellationToken);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                await output.WriteLineAsync($"  game did not boot: {FirstLine(e.Message)}");
                if (attempt == 2)
                {
                    bootFailure = e;
                    throw;
                }
            }
        }
    }

    private async Task<GameSession> BootAsync(CancellationToken cancellationToken)
    {
        // Numbered by attempt, so a failed boot keeps its log next to the retry's.
        var number = ++bootAttempts;
        var playerLog = Path.Combine(runDir, group.Name, $"player-{number}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(playerLog)!);
        gamePlayerLog = playerLog;

        var clock = Stopwatch.StartNew();
        var session = await GameSession.LaunchAsync(
            instance!,
            new LaunchOptions { HarnessHome = home.Root, PlayerLogPath = playerLog },
            cancellationToken
        );
        var connected = clock.Elapsed;
        session.Popups = popups;

        // A mod that asks a question while loading stops the boot until it is answered; say so as it happens.
        Action<DialogEvent> onDialog = dialog => output.WriteLine($"  game {number} dialog: {dialog}");
        session.DialogOpened += onDialog;
        try
        {
            await session.WaitForMainMenuAsync(cancellationToken: cancellationToken);
        }
        catch (Exception e)
        {
            var dialogs = session.Log.DialogsSince(0).Select(dialog => dialog.ToString()).ToList();
            await session.DisposeAsync();
            if (dialogs.Count == 0)
            {
                throw;
            }

            throw new InvalidOperationException(
                $"the game did not reach the main menu; dialogs it showed while loading: {string.Join(", ", dialogs)}",
                e
            );
        }
        finally
        {
            session.DialogOpened -= onDialog;
        }

        var pid = session.Process?.Id ?? session.Info.Pid;
        Summary.Boots.Add(new GameBoot(Seconds(connected), Seconds(clock.Elapsed), pid, playerLog));
        await output.WriteLineAsync(
            $"  game {number} (pid {pid}, {(session.Info.Retooled ? "Retooled" : "stock")} {session.Info.Ksp}, "
                + $"Unity {session.Info.Unity}): connected after {SuiteRunner.FormatDuration(connected)}, "
                + $"main menu after {SuiteRunner.FormatDuration(clock.Elapsed)}"
        );

        game = session;
        testsOnGame = 0;
        popupsNoted = 0;
        return session;
    }

    // The popups the policy closed while the game booted and during the test's reset and body, as notes, so that a
    // passing test still shows what was dismissed on its way.
    private void NotePopups(KspSuite suite)
    {
        var closed = suite.Game.ClosedPopups;
        if (suite.Game != game || closed.Count <= popupsNoted)
        {
            return;
        }

        suite.Evidence.Note($"popups closed by the policy: {string.Join("; ", closed.Skip(popupsNoted))}");
        popupsNoted = closed.Count;
    }

    private async Task StopGameAsync()
    {
        var stopping = game;
        game = null;
        if (stopping is not null)
        {
            await stopping.DisposeAsync();
        }
    }

    private async Task CollectEvidenceAsync(
        TestEvidence evidence,
        Exception failure,
        GameSession firstGame,
        long firstCursor
    )
    {
        evidence.Write("exception.txt", failure.ToString());
        ApprovalEvidence.Collect(evidence, failure);

        if (game is not null && IsAlive(game))
        {
            try
            {
                using var screenshotTimeout = new CancellationTokenSource(EvidenceTimeout);
                await evidence.ScreenshotAsync(game, "screenshot.png", screenshotTimeout.Token);
            }
            catch (Exception e)
            {
                evidence.Note($"no screenshot: {e.GetType().Name}: {FirstLine(e.Message)}");
            }
        }

        evidence.Write("log.txt", FormatEvents(firstGame.Log.Since(firstCursor)));
        if (game is not null && game != firstGame)
        {
            evidence.Write("log-restarted-game.txt", FormatEvents(game.Log.All));
        }

        var crash = firstGame.Log.Since(firstCursor).OfType<CrashingEvent>().FirstOrDefault();
        if (crash is not null)
        {
            evidence.Note(
                $"the game reported an unhandled exception on a background thread: {FirstLine(crash.Message)}"
            );
        }

        if (game is null || !IsAlive(game))
        {
            CopyPlayerLogTail(evidence);
        }

        if (lockFile is not null && File.Exists(lockFile))
        {
            evidence.CopyFile(Path.GetFileName(lockFile), lockFile);
        }
    }

    private void CopyPlayerLogTail(TestEvidence evidence)
    {
        if (gamePlayerLog is null || !File.Exists(gamePlayerLog))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(
                gamePlayerLog,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            using var reader = new StreamReader(stream);
            var tail = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                if (tail.Count == PlayerLogTailLines)
                {
                    tail.Dequeue();
                }

                tail.Enqueue(line);
            }

            evidence.Write(
                "player-log-tail.txt",
                $"(last {tail.Count} lines of {gamePlayerLog})"
                    + Environment.NewLine
                    + string.Join(Environment.NewLine, tail)
            );
        }
        catch (IOException e)
        {
            evidence.Note($"could not read the player log: {e.Message}");
        }
    }

    private async Task<TestResult> ReportAsync(
        SuiteTest test,
        TestOutcome outcome,
        TimeSpan time,
        Exception? failure,
        TestEvidence? evidence
    )
    {
        var label = outcome switch
        {
            TestOutcome.Passed => "PASS ",
            TestOutcome.Failed => "FAIL ",
            TestOutcome.TimedOut => "TIME ",
            TestOutcome.Crashed => "CRASH",
            TestOutcome.Error => "ERROR",
            _ => "SKIP ",
        };

        var error = failure is null ? null : $"{failure.GetType().Name}: {failure.Message}";
        var line = new StringBuilder($"  {label} {test.FullName}  {SuiteRunner.FormatDuration(time)}");
        if (error is not null)
        {
            line.Append("  ").Append(FirstLine(error));
        }

        foreach (var note in evidence?.Notes ?? [])
        {
            line.AppendLine().Append("        ").Append(note);
        }

        var hasFiles = evidence is not null && evidence.Files.Count > 0;
        if (hasFiles)
        {
            line.AppendLine().Append("        evidence: ").Append(evidence!.Folder);
        }

        await output.WriteLineAsync(line.ToString());
        return new TestResult
        {
            Suite = test.Suite.Name,
            Test = test.Method.Name,
            Install = test.Install.Name,
            Outcome = outcome,
            Seconds = Seconds(time),
            Error = error,
            Evidence = hasFiles ? evidence!.Folder : null,
            Notes = evidence?.Notes.ToList() ?? [],
        };
    }

    private static bool IsAlive(GameSession session) => session.IsConnected && session.Process is { HasExited: false };

    private static Exception Unwrap(Exception e)
    {
        while (e is TargetInvocationException { InnerException: { } inner })
        {
            e = inner;
        }

        return e;
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end] + " ...";
    }

    private static double Seconds(TimeSpan time) => Math.Round(time.TotalSeconds, 1);

    internal static string FormatEvents(IEnumerable<GameEvent> events)
    {
        var text = new StringBuilder();
        foreach (var e in events)
        {
            text.Append(e.RealTime.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(10)).Append(' ');
            switch (e)
            {
                case LogEvent log:
                    text.Append('[').Append(log.Level).Append("] ").Append(log.Message);
                    if (log.Mods.Count > 0)
                    {
                        text.Append(" [mods: ").Append(string.Join(", ", log.Mods)).Append(']');
                    }

                    if (log.IsError && !string.IsNullOrWhiteSpace(log.Stack))
                    {
                        text.AppendLine().Append(log.Stack.TrimEnd());
                    }

                    break;
                case SceneEvent scene:
                    text.Append("[scene] ")
                        .Append(scene.From)
                        .Append(" -> ")
                        .Append(scene.To)
                        .Append(' ')
                        .Append(scene.Phase);
                    break;
                case DialogEvent dialog:
                    text.Append("[dialog] ")
                        .Append(dialog.Name)
                        .Append(": ")
                        .Append(dialog.Title)
                        .Append(" | ")
                        .Append(dialog.Message)
                        .Append(" [")
                        .Append(string.Join(", ", dialog.Buttons))
                        .Append(']');
                    break;
                case CrashingEvent crash:
                    text.Append("[crashing] ").Append(crash.Message);
                    break;
                default:
                    text.Append('[').Append(e.Kind).Append("] ").Append(e.Data.ToJsonString());
                    break;
            }

            text.AppendLine();
        }

        return text.ToString();
    }
}
