using System.Text.Json.Nodes;
using KspHarness.Script;
using KspHarness.Tests.Infrastructure;

namespace KspHarness.Tests;

/// <summary>The client side of the protocol, against a fake game.</summary>
public sealed class GameSessionTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hello_with_a_wrong_token_fails()
    {
        await using var game = new FakeGame();

        var error = await Assert.ThrowsAsync<GameRpcException>(
            () => GameSession.ConnectAsync(game.Port, "wrong", TestInstalls.TempDir("x"), cancellationToken: Cancel));

        Assert.Equal(-32002, error.Code);
    }

    [Fact]
    public async Task Hello_reports_the_game_and_the_buffered_log_follows()
    {
        await using var fake = await FakeSession.StartAsync(configure: game =>
        {
            game.BufferedEvents.Add(new JsonObject
            {
                ["kind"] = "log",
                ["level"] = "Exception",
                ["message"] = "NullReferenceException",
                ["stack"] = "at Foo.Bar ()",
                ["mods"] = new JsonArray("Foo@1.2.3"),
            });
            game.BufferedEvents.Add(new JsonObject { ["kind"] = "log", ["level"] = "Log", ["message"] = "hello" });
        });

        var session = fake.Session;
        Assert.Equal("MAINMENU", session.Info.Scene);
        Assert.Equal(4242, session.Info.Pid);
        Assert.Equal(2, session.Log.Cursor);

        await session.Log.WaitForMessageAsync("^hello$", TimeSpan.FromSeconds(10), since: 0, Cancel);
        var errors = session.Log.ErrorsSince(0);
        var error = Assert.Single(errors);
        Assert.Equal(LogLevel.Exception, error.Level);
        Assert.Equal(["Foo@1.2.3"], error.Mods);
        Assert.Empty(session.Log.ErrorsSince(session.Log.Cursor));
    }

    [Fact]
    public async Task Events_raise_session_events_in_order()
    {
        await using var fake = await FakeSession.StartAsync();
        var scenes = new List<SceneEvent>();
        var dialogs = new TaskCompletionSource<DialogEvent>();
        fake.Session.SceneChanged += scenes.Add;
        fake.Session.DialogOpened += d => dialogs.TrySetResult(d);

        var cursor = fake.Session.Log.Cursor;
        await fake.Game.SendEventAsync("scene", new JsonObject { ["from"] = "MAINMENU", ["to"] = "SPACECENTER", ["phase"] = "requested" });
        await fake.Game.SendEventAsync("log", new JsonObject { ["level"] = "Error", ["message"] = "boom", ["mods"] = new JsonArray() });
        await fake.Game.SendEventAsync("dialog", new JsonObject
        {
            ["name"] = "warning",
            ["title"] = "Warning",
            ["message"] = "Are you sure?",
            ["buttons"] = new JsonArray("Yes", "No"),
        });

        var dialog = await dialogs.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancel);
        Assert.Equal(["Yes", "No"], dialog.Buttons);
        Assert.Equal("SPACECENTER", Assert.Single(scenes).To);
        Assert.Equal(["scene", "log", "dialog"], fake.Session.Log.Since(cursor).Select(e => e.Kind));
        Assert.Equal("boom", Assert.Single(fake.Session.Log.ErrorsSince(cursor)).Message);
    }

    [Fact]
    public async Task Run_sends_its_options()
    {
        JsonObject? request = null;
        await using var fake = await FakeSession.StartAsync(TestInstalls.Stock, game => game.OnRun = (run, _) =>
        {
            request = run;
            return Task.FromResult<JsonNode?>(null);
        });

        var options = new RunOptions { At = TickPoint.FixedUpdate, Timeout = GameTime.Seconds(60), RealTimeout = TimeSpan.FromSeconds(90) };
        await fake.Session.Run<object>("return null;", options, Cancel);

        Assert.Equal("FixedUpdate", request!["at"]!.GetValue<string>());
        Assert.Equal(60, request["timeoutGameSeconds"]!.GetValue<double>());
        Assert.Equal(90, request["timeoutRealSeconds"]!.GetValue<double>());
        Assert.Equal("Run", request["method"]!.GetValue<string>());
        Assert.StartsWith("KspHarness.Snippets.S_", request["type"]!.GetValue<string>());
        Assert.Null(request["result"]);
    }

    [Fact]
    public async Task Forgotten_assembly_is_loaded_again()
    {
        await using var fake = await FakeSession.StartAsync(TestInstalls.Stock, game => game.OnRun = (_, _) => Task.FromResult<JsonNode?>(5));
        Assert.Equal(5, await fake.Session.Run<int>("return 5;", cancellationToken: Cancel));

        fake.Game.ForgetLoadedOnce = true;
        Assert.Equal(5, await fake.Session.Run<int>("return 5;", cancellationToken: Cancel));

        Assert.Equal(["hello", "load", "run", "run", "load", "run"], fake.Game.Methods);
    }

    [Fact]
    public async Task Snippet_exception_carries_the_game_exception()
    {
        await using var fake = await FakeSession.StartAsync(TestInstalls.Stock, game => game.OnRun = (_, _) =>
            throw new FakeError(-32000, "snippet threw", new JsonObject
            {
                ["type"] = "System.InvalidOperationException",
                ["message"] = "outer",
                ["stack"] = "at S_x.Run () in C:/t/Test.cs:12",
                ["inner"] = new JsonObject { ["type"] = "System.NullReferenceException", ["message"] = "inner", ["stack"] = "" },
            }));

        var error = await Assert.ThrowsAsync<SnippetException>(() => fake.Session.Run<int>("return 1;", cancellationToken: Cancel));

        Assert.Equal("System.InvalidOperationException", error.GameType);
        Assert.Equal("outer", error.GameMessage);
        Assert.Contains("Test.cs:12", error.GameStackTrace);
        Assert.Equal("System.NullReferenceException", Assert.IsType<SnippetException>(error.InnerException).GameType);
    }

    [Fact]
    public async Task Game_timeout_becomes_a_timeout_exception()
    {
        await using var fake = await FakeSession.StartAsync(TestInstalls.Stock, game => game.OnRun = (_, _) =>
            throw new FakeError(-32004, "timed out", new JsonObject { ["kind"] = "game" }));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => fake.Session.Run<int>("return 1;", cancellationToken: Cancel));

        Assert.Contains("game", error.Message);
    }

    [Fact]
    public async Task Cancelling_sends_cancel_and_throws()
    {
        var started = new TaskCompletionSource();
        await using var fake = await FakeSession.StartAsync(TestInstalls.Stock, game => game.OnRun = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return null;
        });

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = fake.Session.Run(async () => await Tick.Update, cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancel);
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Contains("cancel", fake.Game.Methods);
    }

    [Fact]
    public async Task Cancelling_gives_up_when_the_game_never_answers()
    {
        var started = new TaskCompletionSource();
        var runs = 0;
        await using var fake = await FakeSession.StartAsync(TestInstalls.Stock, game =>
        {
            game.IgnoreCancel = true;
            game.OnRun = async (_, _) =>
            {
                if (Interlocked.Increment(ref runs) == 1)
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, TestContext.Current.CancellationToken);
                }

                return 7;
            };
        });

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        var run = fake.Session.Run<int>("return 7;", new RunOptions { RealTimeout = null }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancel);
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(TimeSpan.FromSeconds(30), Cancel));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Contains("cancel", fake.Game.Methods);

        Assert.True(fake.Session.IsConnected);
        Assert.Equal(7, await fake.Session.Run<int>("return 7;", cancellationToken: Cancel));
    }

    [Fact]
    public async Task Throwing_wait_predicate_fails_only_its_wait()
    {
        await using var fake = await FakeSession.StartAsync(TestInstalls.Stock, game =>
            game.OnRun = (_, _) => Task.FromResult<JsonNode?>(3));

        var wait = fake.Session.Log.WaitForAsync(e => ((SceneEvent)e).To == "FLIGHT", TimeSpan.FromSeconds(30), cancellationToken: Cancel);
        await fake.Game.SendEventAsync("log", new JsonObject { ["level"] = "Log", ["message"] = "not a scene" });

        await Assert.ThrowsAsync<InvalidCastException>(() => wait);
        Assert.True(fake.Session.IsConnected);
        Assert.Equal(3, await fake.Session.Run<int>("return 3;", cancellationToken: Cancel));
    }

    [Fact]
    public async Task Unreadable_event_is_skipped_and_the_session_lives_on()
    {
        await using var fake = await FakeSession.StartAsync();

        await fake.Game.SendEventAsync("log", new JsonObject { ["level"] = "Log", ["message"] = 12 });
        await fake.Game.SendEventAsync("log", new JsonObject { ["level"] = "Log", ["message"] = "after" });

        await fake.Session.Log.WaitForMessageAsync("^after$", TimeSpan.FromSeconds(10), since: 0, Cancel);
        Assert.True(fake.Session.IsConnected);
    }

    [Fact]
    public async Task RunHandle_keeps_the_result_and_returns_a_handle()
    {
        JsonObject? request = null;
        await using var fake = await FakeSession.StartAsync(TestInstalls.Stock, game => game.OnRun = (run, _) =>
        {
            request = run;
            return Task.FromResult<JsonNode?>(new JsonObject
            {
                ["$"] = "handle",
                ["id"] = 12,
                ["type"] = "Vessel",
                ["assembly"] = "Assembly-CSharp",
                ["text"] = "Kerbal X (Vessel)",
            });
        });

        var handle = await fake.Session.RunHandle(ksp => ksp.FlightGlobals.ActiveVessel, cancellationToken: Cancel);

        Assert.Equal("handle", request!["result"]!.GetValue<string>());
        Assert.Equal(12, handle!.Id);
        Assert.Equal("Vessel", handle.Type);

        dynamic used = handle;
        var misuse = Assert.Throws<InvalidOperationException>(() => (object)used.altitude);
        Assert.Contains("Game.Run", misuse.Message);

        await handle.ReleaseAsync(Cancel);
        Assert.Contains("release", fake.Game.Methods);
    }

    [Fact]
    public async Task Lost_connection_fails_pending_runs()
    {
        var started = new TaskCompletionSource();
        var fake = await FakeSession.StartAsync(TestInstalls.Stock, game => game.OnRun = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return null;
        });

        try
        {
            var run = fake.Session.Run<int>("return 1;", cancellationToken: Cancel);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), Cancel);
            await fake.Game.DisposeAsync();

            await Assert.ThrowsAsync<GameConnectionLostException>(() => run);
            Assert.False(fake.Session.IsConnected);
        }
        finally
        {
            await fake.Session.DisposeAsync();
        }
    }

    [Fact]
    public void Launch_arguments_carry_port_token_and_window()
    {
        var options = new LaunchOptions
        {
            InstanceDir = "instance",
            Width = 1600,
            Height = 900,
            ExtraArgs = ["-force-d3d11"],
        };

        var arguments = GameSession.LaunchArguments(options, 5123, "beef");

        Assert.Equal(
            ["-ksp-harness-port", "5123", "-ksp-harness-token", "beef", "-screen-fullscreen", "0", "-screen-width", "1600",
                "-screen-height", "900", "-force-d3d11"],
            arguments);
    }

    [Fact]
    public async Task Launch_fails_fast_when_the_game_exits()
    {
        // Any executable that exits at once stands in for a game that crashes during boot.
        var instance = TestInstalls.TempDir("exits");
        var mod = Path.Combine(instance, "GameData", HarnessMod.FolderName, HarnessMod.AssemblyRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(mod)!);
        File.WriteAllText(mod, "");
        var options = new LaunchOptions
        {
            InstanceDir = instance,
            Executable = ExitingProgram,
            BootTimeout = TimeSpan.FromSeconds(60),
        };

        var error = await Assert.ThrowsAsync<GameExitedException>(() => GameSession.LaunchAsync(options, Cancel));
        Assert.Contains("exited", error.Message);

        // The game's console output is drained, not inherited, and the end of it explains the exit (here the
        // program's complaint about the harness arguments).
        Assert.Contains("last console output", error.Message);
        Assert.Contains(OperatingSystem.IsWindows() ? "ERROR" : "option", error.Message);
    }

    [Fact]
    public async Task Launch_refuses_an_instance_without_the_driver_mod()
    {
        var instance = TestInstalls.TempDir("no-mod");
        Directory.CreateDirectory(Path.Combine(instance, "GameData", "Squad"));
        var options = new LaunchOptions
        {
            InstanceDir = instance,
            Executable = ExitingProgram,
        };

        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => GameSession.LaunchAsync(options, Cancel));
        Assert.Contains("driver mod", error.Message);
        Assert.EndsWith("KspHarness.Mod.dll", error.FileName);
    }

    [Fact]
    public async Task Launch_starts_the_player_of_the_instance_layout()
    {
        var instance = TestInstalls.TempDir("layout");
        Directory.CreateDirectory(Path.Combine(instance, "GameData", "Squad"));

        // No player at all: the message names what was looked for.
        var options = new LaunchOptions { InstanceDir = instance };
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => GameSession.LaunchAsync(options, Cancel));
        Assert.Contains("KSP.x86_64", missing.Message);

        // Another operating system's player is refused before anything starts.
        string foreign = OperatingSystem.IsWindows() ? "KSP.x86_64" : "KSP_x64.exe";
        File.WriteAllText(Path.Combine(instance, foreign), "");
        var error = await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => GameSession.LaunchAsync(options, Cancel));
        Assert.Contains("cannot run", error.Message);
    }

    // A program every machine has that exits at once with a complaint about the harness arguments.
    private static string ExitingProgram => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.SystemDirectory, "whoami.exe")
        : "/bin/ls";

    [Fact]
    public void Mod_load_check_reads_the_player_log()
    {
        string[] head = ["Win32NT 7FFFFFFFFFFFFFFF  Args: KSP_x64.exe -ksp-harness-port 5000 -ksp-harness-token ab"];
        string[] listed = ["Mod DLLs found:", "Stock assembly: Assembly-CSharp v0.0.0.0", "KspHarness.Mod v1.0.0.0 / v1.0.0", "", "Folders and files in GameData:"];
        string[] unlisted = ["Mod DLLs found:", "Stock assembly: Assembly-CSharp v0.0.0.0", "", "Folders and files in GameData:"];

        Assert.Null(new ModLoadCheck(null, 5000).Feed([.. head, .. listed]));
        Assert.Contains("without the driver mod", new ModLoadCheck(null, 5000).Feed([.. head, .. unlisted]));
        Assert.Contains("did not start", new ModLoadCheck(null, 5000).Feed(
            [.. head, "[KspHarness] Cannot listen on 127.0.0.1:5000: address in use"]));

        // Another game's log (another port) says nothing about this one.
        Assert.Null(new ModLoadCheck(null, 5001).Feed([.. head, .. unlisted]));

        // Retooled prints a second, differently formatted DLL table later; only the first one counts.
        Assert.Null(new ModLoadCheck(null, 5000).Feed([.. head, .. listed, .. unlisted]));
    }
}
