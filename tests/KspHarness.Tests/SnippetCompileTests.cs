using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using KspHarness.Script;
using KspHarness.Tests.Infrastructure;

namespace KspHarness.Tests;

/// <summary>
/// Every snippet form compiled against real installs through <see cref="GameSession"/>, with a fake game that
/// receives the assembly.
/// </summary>
public sealed class SnippetCompileTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public async Task Source_with_return_reaches_private_members(string install)
    {
        await using var fake = await FakeSession.StartAsync(install, game => game.OnRun = Returns(7));

        var result = await fake.Session.Run<int>(
            """
            var menu = Object.FindObjectOfType<MainMenu>();
            MainMenu.pName = "harness";
            menu.ConfirmNewGame();
            return (int)MainMenu.newGameMode;
            """,
            cancellationToken: Cancel);

        Assert.Equal(7, result);
        Assert.Single(fake.Game.Loaded);
        AssertDefinesSnippet(fake.Game.Loaded.Values.Single());
    }

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public async Task Source_without_return_awaits_ticks(string install)
    {
        await using var fake = await FakeSession.StartAsync(install);

        await fake.Session.Run(
            """
            await Tick.FixedUpdate;
            await Wait.Frames(2);
            FlightInputHandler.state.mainThrottle = 1f;
            """,
            cancellationToken: Cancel);

        Assert.Single(fake.Game.Loaded);
    }

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public async Task Typed_lambdas_of_every_shape_compile(string install)
    {
        await using var fake = await FakeSession.StartAsync(install, game => game.OnRun = Returns(3));
        var session = fake.Session;

        Assert.Equal(3, await session.Run(() => 1 + 2, cancellationToken: Cancel));
        Assert.Equal(3, await session.Run(async () =>
        {
            await Tick.FixedUpdate;
            return 3;
        }, cancellationToken: Cancel));
        await session.Run(async () =>
        {
            await Tick.Update;
        }, cancellationToken: Cancel);
        await session.Run(() =>
        {
            var unused = Math.Max(1, 2);
        }, cancellationToken: Cancel);

        // A Unity call that returns nothing still works as an expression lambda.
        await session.Run(() => Console.WriteLine("hello"), cancellationToken: Cancel);

        Assert.Equal(5, fake.Game.Loaded.Count);
    }

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public async Task Dynamic_lambdas_of_every_shape_compile(string install)
    {
        await using var fake = await FakeSession.StartAsync(install, game => game.OnRun = Returns(1.5));
        var session = fake.Session;

        double altitude = await session.Run(ksp => ksp.FlightGlobals.ActiveVessel.altitude, cancellationToken: Cancel);
        Assert.Equal(1.5, altitude);

        var speed = await session.Run(async ksp =>
        {
            ksp.FlightInputHandler.state.mainThrottle = 1f;
            ksp.KSP.UI.Screens.StageManager.ActivateNextStage();
            var vessel = ksp.FlightGlobals.ActiveVessel;
            await Tick.FixedUpdate;
            return vessel.srfSpeed;
        }, cancellationToken: Cancel);
        Assert.Equal(1.5, (double)speed);

        await session.Run(async ksp =>
        {
            await Wait.Scene(ksp.GameScenes.FLIGHT);
        }, cancellationToken: Cancel);

        await session.Run(ksp =>
        {
            ksp.Debug.Log("statement lambda");
        }, cancellationToken: Cancel);

        // Void in the game, dynamic in the test.
        await session.Run(ksp => ksp.Debug.Log("expression lambda"), cancellationToken: Cancel);

        Assert.Equal(5, fake.Game.Loaded.Count);
    }

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public async Task Captured_locals_are_sent_as_arguments(string install)
    {
        JsonArray? args = null;
        await using var fake = await FakeSession.StartAsync(install, game => game.OnRun = (run, _) =>
        {
            args = run["args"]!.AsArray();
            return Task.FromResult<JsonNode?>(2.0);
        });

        double target = 70000;
        var name = "Kerbin";
        var notUsed = 5;
        _ = notUsed;
        var timeout = GameTime.Seconds(30);
        {
            var factor = 2;
            await fake.Session.Run(async ksp =>
            {
                var body = ksp.FlightGlobals.GetBodyByName(name);
                while (ksp.FlightGlobals.ActiveVessel.altitude < target * factor)
                {
                    await Tick.FixedUpdate;
                }

                await Wait.Until(() => body != null, timeout);
                return body.Radius;
            }, cancellationToken: Cancel);
        }

        Assert.NotNull(args);
        var values = args!.Select(a => a!.ToJsonString()).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["\"Kerbin\"", "2", "70000", """{"$":"gametime","seconds":30}"""], values);
    }

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public async Task Captured_handle_is_declared_with_its_game_type(string install)
    {
        JsonNode? sent = null;
        await using var fake = await FakeSession.StartAsync(install, game => game.OnRun = (run, _) =>
        {
            sent = run["args"]![0]?.DeepClone();
            return Task.FromResult<JsonNode?>(null);
        });

        dynamic vessel = new Handle(9, "Vessel", "Assembly-CSharp", "Kerbal X", fake.Session);

        // Compiles only if the local is a Vessel in the game, since object has no 'altitude'.
        await fake.Session.Run(ksp => vessel.altitude, cancellationToken: Cancel);

        Assert.Equal(9, sent!["id"]!.GetValue<long>());
        Assert.Equal("handle", sent["$"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public async Task Index_from_end_compiles_against_the_games_base_library(string install)
    {
        await using var fake = await FakeSession.StartAsync(install, game => game.OnRun = Returns(3));

        var last = await fake.Session.Run(() => new[] { 1, 2, 3 }[^1], cancellationToken: Cancel);

        Assert.Equal(3, last);
    }

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public async Task Compile_error_in_a_lambda_points_at_the_test_line(string install)
    {
        await using var fake = await FakeSession.StartAsync(install);

        var line = Line() + 1;
        var error = await Assert.ThrowsAsync<SnippetCompileException>(() => fake.Session.Run(ksp => ksp.FlightGlobals.ActiveVessel.altitide, cancellationToken: Cancel));

        Assert.Equal(ThisFile(), error.File);
        Assert.Equal(line, error.Line);
        Assert.Contains("altitide", error.Message);
        Assert.Empty(fake.Game.Loaded);
    }

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public async Task Compile_error_deep_in_a_multiline_lambda_points_at_its_line(string install)
    {
        await using var fake = await FakeSession.StartAsync(install);

        var start = Line();
        var error = await Assert.ThrowsAsync<SnippetCompileException>(() => fake.Session.Run(
            async ksp =>
            {
                var vessel = ksp.FlightGlobals.ActiveVessel;
                await Tick.FixedUpdate;
                return ksp.FlightGlobals.NoSuchMember;
            },
            cancellationToken: Cancel));

        Assert.Equal(ThisFile(), error.File);
        Assert.Equal(start + 6, error.Line);
    }

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public async Task Compile_error_in_a_source_string_points_at_its_line(string install)
    {
        await using var fake = await FakeSession.StartAsync(install);

        var start = Line();
        var error = await Assert.ThrowsAsync<SnippetCompileException>(() => fake.Session.Run<double>(
            """
            var vessel = FlightGlobals.ActiveVessel;
            return vessel.altitide;
            """,
            cancellationToken: Cancel));

        Assert.Equal(ThisFile(), error.File);
        Assert.Equal(start + 4, error.Line);
    }

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public async Task Other_uses_of_the_dynamic_parameter_are_rejected(string install)
    {
        await using var fake = await FakeSession.StartAsync(install);

        var line = Line() + 1;
        var error = await Assert.ThrowsAsync<SnippetCompileException>(() => fake.Session.Run(ksp => Describe(ksp), cancellationToken: Cancel));

        Assert.Equal(line, error.Line);
        Assert.Contains("'ksp'", error.Message);
    }

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public async Task Capturing_this_is_rejected(string install)
    {
        await using var fake = await FakeSession.StartAsync(install);

        var error = await Assert.ThrowsAsync<SnippetCompileException>(() => fake.Session.Run(() => instanceValue + 1, cancellationToken: Cancel));

        Assert.Contains("instanceValue", error.Message);
        Assert.Contains("local", error.Message);
    }

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public async Task Capturing_an_object_the_game_cannot_get_is_rejected(string install)
    {
        await using var fake = await FakeSession.StartAsync(install);
        var builder = new System.Text.StringBuilder();

        var error = await Assert.ThrowsAsync<SnippetCompileException>(() => fake.Session.Run(() => builder.Length, cancellationToken: Cancel));

        Assert.Contains("'builder'", error.Message);
    }

    [Theory]
    [MemberData(nameof(TestInstalls.All), MemberType = typeof(TestInstalls))]
    public async Task Same_snippet_compiles_once_and_loads_once(string install)
    {
        await using var fake = await FakeSession.StartAsync(install, game => game.OnRun = Returns(1));

        for (var i = 0; i < 3; i++)
        {
            await fake.Session.Run(() => 1, cancellationToken: Cancel);
        }

        Assert.Single(fake.Game.Loaded);
        Assert.Equal(1, fake.Game.Methods.Count(m => m == "load"));
        Assert.Equal(3, fake.Game.Methods.Count(m => m == "run"));
    }

    private readonly int instanceValue = 4;

    private static string Describe(object value) => value.ToString()!;

    private static Func<JsonObject, CancellationToken, Task<JsonNode?>> Returns(JsonNode? value)
    {
        return (_, _) => Task.FromResult(value?.DeepClone());
    }

    private static void AssertDefinesSnippet(byte[] assembly)
    {
        using var pe = new PEReader(new MemoryStream(assembly));
        var reader = pe.GetMetadataReader();
        var names = reader.TypeDefinitions
            .Select(h => reader.GetTypeDefinition(h))
            .Select(t => reader.GetString(t.Namespace) + "." + reader.GetString(t.Name))
            .ToArray();

        Assert.Contains(names, n => n.StartsWith("KspHarness.Snippets.S_", StringComparison.Ordinal));
    }

    private static int Line([CallerLineNumber] int line = 0) => line;

    private static string ThisFile([CallerFilePath] string file = "") => file;
}
