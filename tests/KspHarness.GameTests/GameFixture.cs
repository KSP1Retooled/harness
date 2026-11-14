using System.Diagnostics;
using KspHarness.Installs;

namespace KspHarness.GameTests;

/// <summary>The instance the game tests run in: the starting install with only the window settings changed.</summary>
public sealed class GameTestsInstall : InstallDefinition
{
    public override void Configure(InstallBuilder install)
    {
        install
            .Setting("FULLSCREEN", "False")
            .Setting("SCREEN_RESOLUTION_WIDTH", "1280")
            .Setting("SCREEN_RESOLUTION_HEIGHT", "720");
    }
}

/// <summary>
/// One game per starting install, shared by a test class: builds the instance, deploys the mod, boots to the main
/// menu, and quits at the end. Without the starting install's environment variable the tests skip.
/// </summary>
public abstract class GameFixture : IAsyncLifetime
{
    private GameSession? game;
    private Exception? failure;

    protected abstract string StartVariable { get; }

    public Instance? Instance { get; private set; }

    public TimeSpan BuildTime { get; private set; }

    /// <summary>From starting the process to the driver mod's <c>hello</c> reply.</summary>
    public TimeSpan ConnectTime { get; private set; }

    /// <summary>From starting the process to the settled main menu.</summary>
    public TimeSpan BootTime { get; private set; }

    /// <summary>The running game; skips the test when the starting install is not configured.</summary>
    public GameSession Game
    {
        get
        {
            Assert.SkipWhen(
                game is null && failure is null,
                $"set {StartVariable} to a starting install to run the game tests"
            );
            if (failure is not null)
            {
                throw new InvalidOperationException("the game did not start", failure);
            }

            Assert.True(game!.IsConnected, "the game connection closed in an earlier test");
            return game!;
        }
    }

    public async ValueTask InitializeAsync()
    {
        var startRoot = Environment.GetEnvironmentVariable(StartVariable);
        if (string.IsNullOrEmpty(startRoot))
        {
            return;
        }

        try
        {
            var clock = Stopwatch.StartNew();
            var options = new InstanceBuildOptions
            {
                Log = message => TestContext.Current.SendDiagnosticMessage(message),
            };
            Instance = await InstanceBuilder.BuildAsync(
                new GameTestsInstall(),
                StartingInstall.Open(startRoot),
                HarnessHome.FromEnvironment(),
                lockFile: null,
                options
            );
            BuildTime = clock.Elapsed;

            clock.Restart();
            game = await GameSession.LaunchAsync(
                Instance,
                new LaunchOptions { PlayerLogPath = Instance.Path(Path.Combine(Instance.HarnessFolder, "player.log")) }
            );
            ConnectTime = clock.Elapsed;

            await game.WaitForMainMenuAsync();
            BootTime = clock.Elapsed;
        }
        catch (Exception e)
        {
            failure = e;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (game is not null)
        {
            await game.DisposeAsync();
        }
    }
}

public sealed class StockFixture : GameFixture
{
    protected override string StartVariable => "KSPHARNESS_TEST_STOCK";
}

public sealed class RetooledFixture : GameFixture
{
    protected override string StartVariable => "KSPHARNESS_TEST_RETOOLED";
}
