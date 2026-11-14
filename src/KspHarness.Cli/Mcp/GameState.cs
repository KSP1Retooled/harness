namespace KspHarness.Cli.Mcp;

/// <summary>A tool call that cannot proceed for a reason the agent can fix; its message goes back as the tool's error.</summary>
internal sealed class ToolException(string message) : Exception(message);

/// <summary>The game the MCP server is attached to, shared by all tool calls.</summary>
internal sealed class GameState : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private GameSession? game;

    public string? InstanceDir { get; private set; }

    public GameSession? Current => game;

    public GameSession Require()
    {
        var current = game;
        if (current is null)
        {
            throw new ToolException("no game is attached: call game_launch with an instance folder, or game_attach");
        }

        if (!current.IsConnected)
        {
            throw new ToolException(
                "the connection to the game closed (it quit or crashed, or another client attached): "
                    + "call game_status, then game_attach or game_launch"
            );
        }

        return current;
    }

    /// <summary>Replaces the attached game, closing the old connection (the old game keeps running).</summary>
    public async Task SetAsync(string instanceDir, GameSession session)
    {
        await gate.WaitAsync();
        try
        {
            var previous = game;
            game = session;
            InstanceDir = instanceDir;
            if (previous is not null && previous != session)
            {
                await previous.DisposeAsync();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ClearAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (game is not null)
            {
                await game.DisposeAsync();
            }

            game = null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ClearAsync();
    }
}
