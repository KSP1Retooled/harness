using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace KspHarness.Cli.Mcp;

/// <summary>Serves the harness tools over MCP on standard input and output.</summary>
internal static class McpServer
{
    private const string Instructions = """
        Drives Kerbal Space Program 1.12.5 (stock or KSP Retooled) through the KSP Harness mod.
        Typical flow: instance_build (once, from a starting install) -> game_launch (boots to the main menu, minutes)
        -> run_csharp / ui_describe / ui_click / screenshot / dialogs_list / db_query / logs -> game_quit.
        If a game was started earlier with `ksph launch`, use game_attach instead of game_launch.
        run_csharp runs a C# method body on the game's main thread; await Tick.FixedUpdate, Wait.Scene(...) and the
        Ui and Dialogs helpers work inside it, and results come back as JSON. Look before acting: ui_describe and
        screenshot show what is on screen, logs (levels "errors") shows exceptions with the mods in their stacks.
        """;

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);

        // Standard output carries the protocol, so logging goes to standard error.
        builder.Logging.AddConsole(options =>
            options.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace
        );
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);

        // Registered as an instance so that the container leaves disposing it to this method.
        await using var state = new GameState();
        builder.Services.AddSingleton(state);
        builder
            .Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "ksp-harness",
                    Version = typeof(McpServer).Assembly.GetName().Version?.ToString() ?? "0",
                };
                options.ServerInstructions = Instructions;
            })
            .WithStdioServerTransport()
            .WithTools<HarnessTools>();

        // Disposing the state closes the connection only: a game launched from here keeps running for game_attach or
        // `ksph quit`.
        using var host = builder.Build();
        await host.RunAsync(cancellationToken);
        return 0;
    }
}
