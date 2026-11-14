using System.Runtime.CompilerServices;
using KSP.UI.Screens;
using KspHarness.Script;
using UnityEngine;

namespace KspHarness.GameTests;

/// <summary>
/// Phase 1 checks against a real game: snippets of each kind run in the game and resume on the main thread after
/// awaits. Each test sets up the scene it needs, so they pass in any order.
/// </summary>
[Trait("Category", "Game")]
public abstract class GameTests<TFixture>(TFixture fixture)
    where TFixture : GameFixture
{
    private const double TargetAltitude = 1000;

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private GameSession Game => fixture.Game;

    [Fact]
    public async Task Main_menu_is_reached()
    {
        var game = Game;

        Output(
            $"{game.Info.Ksp} retooled={game.Info.Retooled} unity={game.Info.Unity}: instance built in "
                + $"{fixture.BuildTime.TotalSeconds:0.0} s, mod listening after {fixture.ConnectTime.TotalSeconds:0.0} s, "
                + $"main menu after {fixture.BootTime.TotalSeconds:0.0} s"
        );
        Assert.Equal(1, game.Info.Protocol);
        Assert.Equal(game.Process!.Id, game.Info.Pid);
        Assert.Equal(TestedRetooled, game.Info.Retooled);

        await game.ReturnToMainMenuAsync(cancellationToken: Cancel);
        Assert.Equal(GameScenes.MAINMENU, await game.Run(() => HighLogic.LoadedScene, cancellationToken: Cancel));
    }

    [Fact]
    public async Task Sandbox_has_no_intro_tutorial()
    {
        await Game.ReturnToMainMenuAsync(cancellationToken: Cancel);
        await Game.StartSandboxAsync("harness-game-tests", cancellationToken: Cancel);

        // The editor's greeting comes with an input lock, so a scene change after the start is the real check.
        var found = await Game.Run<string[]>(
            """
            var found = new List<string>();
            found.AddRange(Dialogs.Open.Select(d => "space center: " + d.Name));
            EditorDriver.StartEditor(EditorFacility.VAB);
            await Wait.Scene(GameScenes.EDITOR);
            await Wait.RealSeconds(3);
            found.AddRange(Dialogs.Open.Select(d => "VAB: " + d.Name));
            found.AddRange(Object.FindObjectsOfType<ScenarioNewGameIntro>().Select(i => "instance in " + HighLogic.LoadedScene));
            if (InputLockManager.GetControlLock("intro_Editor") != ControlTypes.None)
            {
                found.Add("intro_Editor lock");
            }

            found.AddRange(HighLogic.CurrentGame.scenarios
                .Where(s => s.moduleName == "ScenarioNewGameIntro")
                .Select(s => "proto module"));
            return found.ToArray();
            """,
            new RunOptions { RealTimeout = TimeSpan.FromMinutes(2) },
            Cancel
        );

        Assert.Empty(found);
    }

    [Fact]
    public async Task Source_string_runs()
    {
        var unity = await Game.Run<string>("return Application.unityVersion;", cancellationToken: Cancel);
        var answer = await Game.Run<int>(
            """
            int frame = Time.frameCount;
            await Tick.Update;
            return Time.frameCount - frame + 41;
            """,
            cancellationToken: Cancel
        );

        Assert.Equal(Game.Info.Unity, unity);
        Assert.Equal(42, answer);
    }

    [Fact]
    public async Task Typed_lambda_runs()
    {
        var frame = await Game.Run(() => Time.frameCount, cancellationToken: Cancel);
        var whole = await Game.Run(() => Math.Floor(Time.realtimeSinceStartup) + 1.0, cancellationToken: Cancel);
        var later = await Game.Run(
            async () =>
            {
                int start = Time.frameCount;
                await Tick.Update;
                await Tick.Update;
                return Time.frameCount - start;
            },
            cancellationToken: Cancel
        );

        Assert.True(frame > 0);
        Assert.True(whole >= 1.0);
        Assert.Equal(2, later);
    }

    [Fact]
    public async Task Dynamic_lambda_runs()
    {
        var scene = await Game.Run(ksp => ksp.HighLogic.LoadedScene.ToString(), cancellationToken: Cancel);
        var fixedStep = await Game.Run(
            async ksp =>
            {
                await Tick.FixedUpdate;
                return ksp.Time.inFixedTimeStep;
            },
            cancellationToken: Cancel
        );
        var whole = await Game.Run(ksp => 3.0, cancellationToken: Cancel);

        Assert.IsType<string>(scene);
        Assert.True((bool)fixedStep);

        // A whole double must come back as a double, not as the integer 3.
        Assert.IsType<double>(whole);
    }

    [Fact]
    public async Task Private_members_are_in_reach()
    {
        await Game.ReturnToMainMenuAsync(cancellationToken: Cancel);

        var name = await Game.Run<string>("return MainMenu.pName;", cancellationToken: Cancel);
        var invalidNames = await Game.Run<int>(
            "return Object.FindObjectOfType<MainMenu>().invalidNames.Count;",
            cancellationToken: Cancel
        );
        var mode = await Game.Run(ksp => ksp.MainMenu.newGameMode.ToString(), cancellationToken: Cancel);

        Assert.False(string.IsNullOrEmpty(name));
        Assert.True(invalidNames > 0);
        Assert.False(string.IsNullOrEmpty((string?)mode));
        Output($"MainMenu.pName = {name}, invalidNames.Count = {invalidNames}, newGameMode = {mode}");
    }

    [Fact]
    public async Task Flight_from_a_source_string()
    {
        await PrepareFlightAsync();

        var flight = await Game.Run<FlightResult>(
            """
            var vessel = FlightGlobals.ActiveVessel;
            int thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            FlightInputHandler.state.mainThrottle = 1f;
            StageManager.ActivateNextStage();

            int steps = 0, offThread = 0, outsideFixedStep = 0, sameFixedTime = 0;
            double lastFixedTime = Time.fixedTime, start = Time.fixedTime;
            while (vessel.altitude < 1000 && Time.fixedTime - start < 120)
            {
                await Tick.FixedUpdate;
                steps++;
                if (System.Threading.Thread.CurrentThread.ManagedThreadId != thread) offThread++;
                if (!Time.inFixedTimeStep) outsideFixedStep++;
                if (Time.fixedTime == lastFixedTime) sameFixedTime++;
                lastFixedTime = Time.fixedTime;
            }

            return new { vessel.srfSpeed, vessel.altitude, thread, steps, offThread, outsideFixedStep, sameFixedTime };
            """,
            FlightOptions,
            Cancel
        );

        CheckFlight(flight);
    }

    [Fact]
    public async Task Flight_from_a_typed_lambda()
    {
        await PrepareFlightAsync();

        double target = TargetAltitude;
        var flight = await Game.Run(
            async () =>
            {
                var vessel = FlightGlobals.ActiveVessel;
                int thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                vessel.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
                FlightInputHandler.state.mainThrottle = 1f;
                StageManager.ActivateNextStage();

                int steps = 0,
                    offThread = 0,
                    outsideFixedStep = 0,
                    sameFixedTime = 0;
                double lastFixedTime = Time.fixedTime,
                    start = Time.fixedTime;
                while (vessel.altitude < target && Time.fixedTime - start < 120)
                {
                    await Tick.FixedUpdate;
                    steps++;
                    if (System.Threading.Thread.CurrentThread.ManagedThreadId != thread)
                        offThread++;
                    if (!Time.inFixedTimeStep)
                        outsideFixedStep++;
                    if (Time.fixedTime == lastFixedTime)
                        sameFixedTime++;
                    lastFixedTime = Time.fixedTime;
                }

                return (vessel.srfSpeed, vessel.altitude, thread, steps, offThread, outsideFixedStep, sameFixedTime);
            },
            FlightOptions,
            Cancel
        );

        CheckFlight(
            new FlightResult(
                flight.srfSpeed,
                flight.altitude,
                flight.thread,
                flight.steps,
                flight.offThread,
                flight.outsideFixedStep,
                flight.sameFixedTime
            )
        );
    }

    [Fact]
    public async Task Flight_from_a_dynamic_lambda()
    {
        await PrepareFlightAsync();

        double target = TargetAltitude;
        var flight = await Game.Run(
            async ksp =>
            {
                var vessel = ksp.FlightGlobals.ActiveVessel;
                int thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                vessel.ActionGroups.SetGroup(ksp.KSPActionGroup.SAS, true);
                ksp.FlightInputHandler.state.mainThrottle = 1f;
                ksp.StageManager.ActivateNextStage();

                int steps = 0,
                    offThread = 0,
                    outsideFixedStep = 0,
                    sameFixedTime = 0;
                double lastFixedTime = ksp.Time.fixedTime,
                    start = ksp.Time.fixedTime;
                while (vessel.altitude < target && ksp.Time.fixedTime - start < 120)
                {
                    await Tick.FixedUpdate;
                    steps++;
                    if (System.Threading.Thread.CurrentThread.ManagedThreadId != thread)
                        offThread++;
                    if (!ksp.Time.inFixedTimeStep)
                        outsideFixedStep++;
                    if (ksp.Time.fixedTime == lastFixedTime)
                        sameFixedTime++;
                    lastFixedTime = ksp.Time.fixedTime;
                }

                return new
                {
                    vessel.srfSpeed,
                    vessel.altitude,
                    thread,
                    steps,
                    offThread,
                    outsideFixedStep,
                    sameFixedTime,
                };
            },
            FlightOptions,
            Cancel
        );

        Assert.NotNull(flight);
        CheckFlight(
            new FlightResult(
                (double)flight!.srfSpeed,
                (double)flight.altitude,
                (int)flight.thread,
                (int)flight.steps,
                (int)flight.offThread,
                (int)flight.outsideFixedStep,
                (int)flight.sameFixedTime
            )
        );
    }

    [Fact]
    public async Task Game_exception_becomes_a_snippet_exception()
    {
        var error = await Assert.ThrowsAsync<SnippetException>(() =>
            Game.Run(
                """
                await Tick.Update;
                throw new InvalidOperationException("harness test failure");
                """,
                cancellationToken: Cancel
            )
        );

        Assert.Equal("System.InvalidOperationException", error.GameType);
        Assert.Equal("harness test failure", error.GameMessage);
        Assert.False(string.IsNullOrWhiteSpace(error.GameStackTrace));
        Output("in-game stack:\n" + error.GameStackTrace);
    }

    [Fact]
    public async Task Compile_error_points_at_the_test_line()
    {
        int stringLine = Line() + 2;
        var fromString = await Assert.ThrowsAsync<SnippetCompileException>(() =>
            Game.Run<int>("return notDeclaredAnywhere;", cancellationToken: Cancel)
        );

        int lambdaLine = Line() + 1;
        var fromLambda = await Assert.ThrowsAsync<SnippetCompileException>(() =>
            Game.Run(ksp => ksp.NoSuchGameType.Value, cancellationToken: Cancel)
        );

        Assert.Equal(stringLine, fromString.Line);
        Assert.Equal(ThisFile(), fromString.File);
        Assert.Equal(lambdaLine, fromLambda.Line);
        Assert.Equal(ThisFile(), fromLambda.File);
    }

    [Fact]
    public async Task Handle_round_trip()
    {
        dynamic? logic = await Game.RunHandle(() => HighLogic.fetch, cancellationToken: Cancel);
        Assert.NotNull(logic);
        var handle = (Handle)logic!;

        var name = await Game.Run(ksp => logic!.gameObject.name, cancellationToken: Cancel);
        var same = await Game.Run(ksp => ReferenceEquals(logic, ksp.HighLogic.fetch), cancellationToken: Cancel);
        await handle.ReleaseAsync(Cancel);

        Assert.Equal("HighLogic", handle.Type);
        Assert.False(string.IsNullOrEmpty((string?)name));
        Assert.True((bool)same);
    }

    [Fact]
    public async Task Real_time_timeout_stops_the_snippet()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            Game.Run("await Wait.RealSeconds(60);", new RunOptions { RealTimeout = TimeSpan.FromSeconds(2) }, Cancel)
        );
        var elapsed = clock.Elapsed;

        Assert.InRange(elapsed.TotalSeconds, 1.5, 20);
        Assert.Equal(1, await Game.Run(() => 1, cancellationToken: Cancel));
    }

    [Fact]
    public async Task Log_events_arrive_and_errors_are_found()
    {
        var cursor = Game.Log.Cursor;
        var marker = Guid.NewGuid().ToString("N");

        await Game.Run(
            () =>
            {
                Debug.Log("harness info " + marker);
                Debug.LogError("harness error " + marker);
            },
            cancellationToken: Cancel
        );
        var error = await Game.Log.WaitForMessageAsync(
            "harness error " + marker,
            TimeSpan.FromSeconds(10),
            cursor,
            Cancel
        );

        Assert.Equal(LogLevel.Error, error.Level);
        Assert.Contains(
            Game.Log.LogsSince(cursor),
            e => e.Message == "harness info " + marker && e.Level == LogLevel.Log
        );
        var errors = Game.Log.ErrorsSince(cursor);
        Assert.Contains(errors, e => e.Message == "harness error " + marker);
        Assert.DoesNotContain(errors, e => e.Message.Contains("harness info", StringComparison.Ordinal));
    }

    /// <summary>Whether the fixture's starting install is Retooled.</summary>
    protected abstract bool TestedRetooled { get; }

    private static RunOptions FlightOptions => new() { RealTimeout = TimeSpan.FromMinutes(3) };

    // Every flight starts from a new sandbox: a relaunch keeps the previous flight's rocket in the save, and it falls
    // back onto the pad.
    private async Task PrepareFlightAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Game.ReturnToMainMenuAsync(cancellationToken: Cancel);
        await Game.StartSandboxAsync("harness-game-tests", cancellationToken: Cancel);
        Output($"sandbox started after {clock.Elapsed.TotalSeconds:0.0} s");

        var cursor = Game.Log.Cursor;
        await Game.LaunchCraftAsync(cancellationToken: Cancel);
        Output($"Kerbal X on the pad after {clock.Elapsed.TotalSeconds:0.0} s");
        Assert.Contains(Game.Log.Since(cursor), e => e is SceneEvent { To: "FLIGHT", Phase: "loaded" });
    }

    private static void CheckFlight(FlightResult flight)
    {
        Output(flight.ToString());
        Assert.True(flight.Altitude >= TargetAltitude, $"altitude {flight.Altitude}");
        Assert.True(flight.SrfSpeed > 50, $"surface speed {flight.SrfSpeed}");
        Assert.True(flight.Steps > 10, $"steps {flight.Steps}");
        Assert.Equal(0, flight.OffThread);
        Assert.Equal(0, flight.OutsideFixedStep);
        Assert.Equal(0, flight.SameFixedTime);
    }

    private static void Output(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);

    private static int Line([CallerLineNumber] int line = 0) => line;

    private static string ThisFile([CallerFilePath] string file = "") => file;
}

/// <summary>What a flight snippet reports: where it got and how its awaits resumed.</summary>
public sealed record FlightResult(
    double SrfSpeed,
    double Altitude,
    int Thread,
    int Steps,
    int OffThread,
    int OutsideFixedStep,
    int SameFixedTime
);

// One collection, so that the two games never run at once.
[Collection("Game")]
public sealed class StockGameTests(StockFixture fixture) : GameTests<StockFixture>(fixture), IClassFixture<StockFixture>
{
    protected override bool TestedRetooled => false;
}

[Collection("Game")]
public sealed class RetooledGameTests(RetooledFixture fixture)
    : GameTests<RetooledFixture>(fixture),
        IClassFixture<RetooledFixture>
{
    protected override bool TestedRetooled => true;
}
