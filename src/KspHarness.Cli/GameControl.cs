using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using KspHarness.Installs;

namespace KspHarness.Cli;

public sealed record LaunchRequest
{
    public required string InstanceDir { get; init; }

    public bool Windowed { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>Wait for the settled main menu before returning, not only for the mod's connection.</summary>
    public bool WaitForMenu { get; init; }

    public TimeSpan BootTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Starts games that outlive the command that started them, and attaches to them through their session file. A
/// <see cref="GameSession"/> made here never owns the process, so disposing it only closes the connection.
/// </summary>
public static class GameControl
{
    public static readonly TimeSpan QuitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Deploys the driver mod, starts the game detached, writes the session file and connects. The caller owns the
    /// returned connection, not the game.
    /// </summary>
    public static async Task<(SessionFile Session, GameSession Game)> LaunchAsync(
        LaunchRequest request,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var instance = Instance.Open(request.InstanceDir);
        var existing = SessionFile.Read(instance.Root, log);
        if (existing is not null && existing.IsAlive())
        {
            throw new InvalidOperationException(
                $"a game is already running in {instance.Root} (pid {existing.Pid}); attach to it or quit it first");
        }

        if (HarnessMod.DeployTo(instance))
        {
            log?.Invoke($"deployed the driver mod into {instance.GameDataDir}");
        }

        var port = GameSession.FreePort();
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var playerLog = instance.Path(Path.Combine(Instance.HarnessFolder, "player.log"));
        var startedUtc = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();
        using var process = Start(instance, Arguments(request, port, token, playerLog));

        var session = new SessionFile
        {
            Pid = process.Id,
            Port = port,
            Token = token,
            Instance = instance.Root,
            StartedUtc = startedUtc,
            PlayerLog = playerLog,
        };
        session.Write();
        log?.Invoke($"started pid {process.Id}, waiting for the driver mod on port {port}");

        GameSession game;
        try
        {
            game = await GameSession.ConnectStartedAsync(
                process, port, token, instance.Root, playerLog, request.BootTimeout, cancellationToken);
        }
        catch
        {
            // A game that never became drivable is of no use to attach or quit, so it does not outlive the launch.
            GameSession.Kill(process);
            SessionFile.Delete(instance.Root);
            throw;
        }

        if (!request.WaitForMenu)
        {
            return (session, game);
        }

        log?.Invoke("connected, waiting for the main menu");
        try
        {
            await game.WaitForMainMenuAsync(Remaining(request.BootTimeout, clock.Elapsed), cancellationToken);
            return (session, game);
        }
        catch (Exception e)
        {
            await game.DisposeAsync();
            if (process.HasExited)
            {
                SessionFile.Delete(instance.Root);
                throw;
            }

            // A drivable game stuck before the menu (an unknown popup, a slow load) is worth looking at, and a
            // cancelled wait only means the caller stopped waiting, so the game stays for attach and quit.
            throw new GameNotAtMenuException(
                session,
                $"the game (pid {process.Id}) is running and connected but did not reach the main menu "
                + $"({SnippetRunner.Describe(e)}); it keeps running: attach to it to look, or quit it",
                e);
        }
    }

    /// <summary>What is left of a deadline shared by the connection and the menu wait, never less than a second.</summary>
    internal static TimeSpan Remaining(TimeSpan limit, TimeSpan elapsed)
    {
        var remaining = limit - elapsed;
        return remaining < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining;
    }

    /// <summary>Connects to the game the instance's session file names.</summary>
    public static async Task<GameSession> AttachAsync(string instanceDir, CancellationToken cancellationToken = default)
    {
        var session = RunningSession(instanceDir);
        try
        {
            return await GameSession.ConnectAsync(session.Port, session.Token, session.Instance, cancellationToken: cancellationToken);
        }
        catch (SocketException e)
        {
            throw new InvalidOperationException(
                $"the game (pid {session.Pid}) is running but not listening on port {session.Port} yet: {e.Message}", e);
        }
    }

    /// <summary>
    /// Waits until the log the mod buffered before this connection (sent right after <c>hello</c>) has arrived. The mod sends it before any later reply, so one round trip is enough.
    /// </summary>
    public static Task CatchUpLogAsync(GameSession game, CancellationToken cancellationToken = default)
    {
        return game.Run<int>("return 0;", cancellationToken: cancellationToken);
    }

    /// <summary>The session file of a game that is still running; throws when there is none.</summary>
    public static SessionFile RunningSession(string instanceDir)
    {
        var session = SessionFile.Read(instanceDir)
            ?? throw new InvalidOperationException(
                $"no game was launched in {Path.GetFullPath(instanceDir)}: run `ksph launch --instance <dir>` first");
        if (!session.IsAlive())
        {
            throw new InvalidOperationException(
                $"the game launched in {session.Instance} (pid {session.Pid}) is no longer running");
        }

        return session;
    }

    /// <summary>
    /// Asks the instance's game to quit and waits for it to exit; kills it after <see cref="QuitTimeout"/> or when it
    /// does not answer. Returns false when no game was running.
    /// </summary>
    public static async Task<bool> QuitAsync(string instanceDir, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var session = SessionFile.Read(instanceDir, log);
        if (session is null || !session.IsAlive())
        {
            SessionFile.Delete(instanceDir);
            return false;
        }

        using var process = Process.GetProcessById(session.Pid);
        try
        {
            await using var game = await GameSession.ConnectAsync(
                session.Port, session.Token, session.Instance, cancellationToken: cancellationToken);
            await game.QuitAsync(cancellationToken);
        }
        catch (Exception e) when (e is SocketException or GameRpcException or GameConnectionLostException or TimeoutException)
        {
            log?.Invoke($"the game did not take the quit request ({e.Message}); killing it");
            GameSession.Kill(process);
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(QuitTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            log?.Invoke($"the game did not exit within {QuitTimeout.TotalSeconds:0} s; killing it");
            GameSession.Kill(process);
        }

        SessionFile.Delete(instanceDir);
        return true;
    }

    internal static IReadOnlyList<string> Arguments(LaunchRequest request, int port, string token, string? playerLog)
    {
        var options = new LaunchOptions
        {
            InstanceDir = request.InstanceDir,
            Windowed = request.Windowed,
            Width = request.Width,
            Height = request.Height,
            PlayerLogPath = playerLog,
        };

        return GameSession.LaunchArguments(options, port, token);
    }

    private static Process Start(Instance instance, IReadOnlyList<string> arguments)
    {
        var layout = instance.Layout;
        if (!layout.RunsOnThisMachine)
        {
            throw new PlatformNotSupportedException(
                $"{instance.Root} holds the {layout.Platform} build of the game, which this machine cannot run; build "
                + "the instance from a starting install for this operating system");
        }

        var start = StartInfo(layout.LaunchPath, instance.Root, arguments, OperatingSystem.IsWindows());
        return Process.Start(start) ?? throw new InvalidOperationException($"could not start {layout.LaunchPath}");
    }

    /// <summary>
    /// How a detached game starts. It must not hold this process's standard streams: a game holding the console's or
    /// an MCP client's pipes would keep them open after ksph exits. On Windows shell execution keeps the game from
    /// inheriting them. Elsewhere a child inherits the three standard descriptors whatever .NET does with the rest, so
    /// <c>sh</c> points them at <c>/dev/null</c> and then <c>exec</c>s the game, which keeps the process id.
    /// </summary>
    internal static ProcessStartInfo StartInfo(
        string executable, string workingDirectory, IReadOnlyList<string> arguments, bool windows)
    {
        var start = new ProcessStartInfo(windows ? executable : "/bin/sh")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = windows,
        };

        if (!windows)
        {
            // The script's own arguments are $0 and $@, so no game argument is ever parsed by the shell.
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("exec \"$0\" \"$@\" </dev/null >/dev/null 2>&1");
            start.ArgumentList.Add(executable);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

}

/// <summary>
/// A launch whose game connected but did not reach the main menu in time, or whose caller stopped waiting. The game
/// keeps running and its session file stays, so it can be attached to or quit.
/// </summary>
public sealed class GameNotAtMenuException(SessionFile session, string message, Exception inner)
    : Exception(message, inner)
{
    public SessionFile Session { get; } = session;
}
