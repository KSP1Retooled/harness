namespace KspHarness.Tests.Infrastructure;

/// <summary>A <see cref="GameSession"/> connected to a <see cref="FakeGame"/>, compiling against a real install.</summary>
internal sealed class FakeSession : IAsyncDisposable
{
    private FakeSession(FakeGame game, GameSession session)
    {
        Game = game;
        Session = session;
    }

    public FakeGame Game { get; }

    public GameSession Session { get; }

    /// <summary>Connects to a new fake game; compiling snippets needs <paramref name="install"/>.</summary>
    public static async Task<FakeSession> StartAsync(string? install = null, Action<FakeGame>? configure = null)
    {
        var game = new FakeGame();
        configure?.Invoke(game);

        var instanceDir = install is null ? TestInstalls.TempDir("no-install") : TestInstalls.Dir(install);
        var options = new SessionOptions
        {
            HarnessHome = TestInstalls.TempDir("home"),
            ModAssemblyPath = install is null ? null : TestInstalls.ModStandIn(install),
        };

        var session = await GameSession.ConnectAsync(
            game.Port,
            game.Token,
            instanceDir,
            options,
            TestContext.Current.CancellationToken
        );
        return new FakeSession(game, session);
    }

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        await Game.DisposeAsync();
    }
}
