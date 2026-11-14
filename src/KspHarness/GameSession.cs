using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using KspHarness.Installs;
using KspHarness.Rpc;
using KspHarness.Snippets;

namespace KspHarness;

/// <summary>
/// A connection to a running game with the driver mod: runs snippets, keeps the game's log and events, and (when it
/// launched the game) owns the process.
/// </summary>
public sealed partial class GameSession : IAsyncDisposable
{
    public const int ProtocolVersion = 1;

    private static readonly ConcurrentDictionary<(string References, string? Cache), SnippetCompiler> Compilers = new();

    private readonly RpcConnection connection;
    private readonly SessionOptions options;
    private readonly TimeSpan quitTimeout;
    private readonly Lazy<SnippetCompiler> compiler;
    private readonly ConcurrentDictionary<string, Task> loaded = new();
    private int disposed;

    private GameSession(
        RpcConnection connection,
        string instanceDir,
        SessionOptions options,
        Process? process,
        int port,
        string token,
        TimeSpan quitTimeout)
    {
        this.connection = connection;
        this.options = options;
        this.quitTimeout = quitTimeout;
        InstanceDir = instanceDir;
        Process = process;
        Port = port;
        Token = token;
        compiler = new Lazy<SnippetCompiler>(CreateCompiler);

        if (GameLayout.FindByManagedDir(instanceDir) is { } layout)
        {
            GameAssemblies.AddDirectory(layout.ManagedDir);
        }

        connection.Notification += OnNotification;
        connection.Closed += reason => ConnectionClosed?.Invoke(reason);
    }

    /// <summary>The instance folder the game runs in, as a full path.</summary>
    public string InstanceDir { get; }

    /// <summary>The game process when this session launched it.</summary>
    public Process? Process { get; }

    /// <summary>The loopback port the driver mod listens on.</summary>
    public int Port { get; }

    /// <summary>The token the mod expects in <c>hello</c>; with <see cref="Port"/>, enough to attach again.</summary>
    public string Token { get; }

    /// <summary>The game's reply to <c>hello</c>.</summary>
    public GameInfo Info { get; private set; } = null!;

    /// <summary>Every event the game sent on this connection, the log replayed after <c>hello</c> included.</summary>
    public GameLog Log { get; } = new();

    /// <summary>False once the connection has closed; a closed session cannot reconnect.</summary>
    public bool IsConnected => connection.IsOpen;

    /// <summary>Raised from the connection's reader thread when a scene starts or finishes loading.</summary>
    public event Action<SceneEvent>? SceneChanged;

    /// <summary>
    /// Raised from the connection's reader thread for each <c>dialog</c> event: a <c>PopupDialog</c> appeared, or a
    /// snippet answered one (<see cref="DialogEvent.Action"/>).
    /// </summary>
    public event Action<DialogEvent>? DialogOpened;

    /// <summary>Raised once when the connection closes, with the reason.</summary>
    public event Action<Exception>? ConnectionClosed;

    internal SnippetCompiler Compiler => compiler.Value;

    /// <summary>
    /// Starts the game (<see cref="LaunchOptions.Executable"/>, by default the instance's <c>KSP_x64.exe</c>,
    /// <c>KSP.x86_64</c> or the binary in <c>KSP.app</c>) in the instance with the harness arguments, and connects
    /// once the mod listens.
    /// </summary>
    public static async Task<GameSession> LaunchAsync(LaunchOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(options.InstanceDir))
        {
            throw new ArgumentException("LaunchOptions.InstanceDir names no instance", nameof(options));
        }

        var instanceDir = Path.GetFullPath(options.InstanceDir);
        var executable = options.Executable ?? DefaultExecutable(instanceDir);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("the game executable does not exist", executable);
        }

        if (HarnessMod.FindIn(instanceDir) is null)
        {
            throw new FileNotFoundException(
                $"the driver mod is not in {Path.Combine(instanceDir, "GameData")}, so the game would never listen; "
                + "deploy it with HarnessMod.DeployTo or launch with GameSession.LaunchAsync(Instance)",
                Path.Combine(instanceDir, "GameData", HarnessMod.FolderName, HarnessMod.AssemblyRelativePath));
        }

        var port = options.Port ?? FreePort();
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        // The game's standard streams go to pipes this session drains: inherited, they would put the player's own
        // output (Retooled's [UnityMemory] lines) into the caller's output and keep the caller's pipe open for as
        // long as the game runs.
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = instanceDir,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in LaunchArguments(options, port, token))
        {
            start.ArgumentList.Add(argument);
        }

        var process = GameProcess.Start(start);
        var consoleOutput = new ConsoleTail(process);
        RpcConnection? connection = null;

        try
        {
            connection = await ConnectWhenListeningAsync(
                process,
                port,
                options.BootTimeout,
                options.PlayerLogPath is null ? null : Path.GetFullPath(options.PlayerLogPath),
                consoleOutput,
                cancellationToken);
            var session = new GameSession(connection, instanceDir, options, process, port, token, options.QuitTimeout);
            await session.HelloAsync(cancellationToken);
            return session;
        }
        catch
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            Kill(process);
            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Deploys the driver mod that ships with this library into the instance and launches the game there.
    /// <see cref="LaunchOptions.InstanceDir"/> is taken from the instance.
    /// </summary>
    /// <exception cref="InstallException">A game already runs in the instance.</exception>
    public static Task<GameSession> LaunchAsync(
        Instance instance,
        LaunchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        instance.EnsureNoGameRunning("launch a game");
        HarnessMod.DeployTo(instance);
        options = (options ?? new LaunchOptions()) with { InstanceDir = instance.Root };
        return LaunchAsync(options, cancellationToken);
    }

    /// <summary>Connects to a game started elsewhere with <c>-ksp-harness-port</c> and <c>-ksp-harness-token</c>.</summary>
    public static async Task<GameSession> ConnectAsync(
        int port,
        string token,
        string instanceDir,
        SessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SessionOptions();
        var connection = await RpcConnection.ConnectAsync(port, cancellationToken);
        var session = new GameSession(connection, Path.GetFullPath(instanceDir), options, null, port, token, TimeSpan.Zero);
        try
        {
            await session.HelloAsync(cancellationToken);
            return session;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Connects to a game started elsewhere with <see cref="LaunchArguments"/> that this session will not own: waits
    /// for the mod to listen as <see cref="LaunchAsync(LaunchOptions, CancellationToken)"/> does, failing early with
    /// <see cref="GameBootException"/> when the player log shows it never will. Neither kills nor disposes the process.
    /// </summary>
    internal static async Task<GameSession> ConnectStartedAsync(
        Process process,
        int port,
        string token,
        string instanceDir,
        string? playerLog,
        TimeSpan bootTimeout,
        CancellationToken cancellationToken)
    {
        var connection = await ConnectWhenListeningAsync(
            process,
            port,
            bootTimeout,
            playerLog is null ? null : Path.GetFullPath(playerLog),
            consoleOutput: null,
            cancellationToken);
        var session = new GameSession(
            connection, Path.GetFullPath(instanceDir), new SessionOptions(), null, port, token, TimeSpan.Zero);
        try
        {
            await session.HelloAsync(cancellationToken);
            return session;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    // The player this machine runs, which the instance's layout names; a Windows install on Linux is not started.
    private static string DefaultExecutable(string instanceDir)
    {
        var layout = GameLayout.FindByLaunchPath(instanceDir);
        if (layout is null)
        {
            throw new FileNotFoundException(
                $"{instanceDir} has no game executable (KSP_x64.exe, KSP.x86_64 or KSP.app/Contents/MacOS/KSP)");
        }

        if (!layout.RunsOnThisMachine)
        {
            throw new PlatformNotSupportedException(
                $"{instanceDir} holds the {layout.Platform} build of the game, which this machine cannot run; build "
                + "the instance from a starting install for this operating system");
        }

        return layout.LaunchPath;
    }

    internal static IReadOnlyList<string> LaunchArguments(LaunchOptions options, int port, string token)
    {
        var arguments = new List<string>
        {
            "-ksp-harness-port",
            port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ksp-harness-token",
            token,
            "-screen-fullscreen",
            options.Windowed ? "0" : "1",
        };

        if (options.Width is { } width)
        {
            arguments.Add("-screen-width");
            arguments.Add(width.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (options.Height is { } height)
        {
            arguments.Add("-screen-height");
            arguments.Add(height.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (options.PlayerLogPath is not null)
        {
            arguments.Add("-logFile");
            arguments.Add(Path.GetFullPath(options.PlayerLogPath));
        }

        arguments.AddRange(options.ExtraArgs);
        return arguments;
    }

    /// <summary>Asks the game to quit and, for a launched game, waits for the process to exit.</summary>
    public async Task QuitAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await connection.RequestAsync("quit", [], cancellationToken).WaitAsync(options.RequestTimeout, cancellationToken);
        }
        catch (GameConnectionLostException)
        {
            // Quitting may close the connection before the reply arrives.
        }

        if (Process is not null)
        {
            await Process.WaitForExitAsync(cancellationToken);
        }
    }

    /// <summary>Lets the game drop the objects behind these handles.</summary>
    public async Task ReleaseAsync(IEnumerable<Handle> handles, CancellationToken cancellationToken = default)
    {
        var ids = new JsonArray(handles.Select(h => (JsonNode)h.Id).ToArray());
        if (ids.Count == 0 || !IsConnected)
        {
            return;
        }

        await connection.RequestAsync("release", new JsonObject { ["handles"] = ids }, cancellationToken)
            .WaitAsync(options.RequestTimeout, cancellationToken);
    }

    /// <summary>
    /// Closes the connection. A game this session launched is asked to quit, and killed if it has not exited within
    /// the quit timeout.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (Process is not null && !Process.HasExited)
        {
            try
            {
                using var quitting = new CancellationTokenSource(quitTimeout);
                await QuitAsync(quitting.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException or GameRpcException
                or GameConnectionLostException)
            {
                // The game is killed below.
            }

            Kill(Process);
        }

        await connection.DisposeAsync();
        Process?.Dispose();
    }

    private async Task HelloAsync(CancellationToken cancellationToken)
    {
        var result = await connection
            .RequestAsync("hello", new JsonObject { ["token"] = Token, ["protocol"] = ProtocolVersion }, cancellationToken)
            .WaitAsync(options.RequestTimeout, cancellationToken);

        Info = new GameInfo(
            result?["protocol"]?.GetValue<int>() ?? 0,
            result?["pid"]?.GetValue<int>() ?? 0,
            result?["ksp"]?.GetValue<string>() ?? "",
            result?["retooled"]?.GetValue<bool>() ?? false,
            result?["unity"]?.GetValue<string>() ?? "",
            result?["scene"]?.GetValue<string>() ?? "",
            result?["loaded"]?.GetValue<bool>() ?? false,
            result?["logCursor"]?.GetValue<long>() ?? 0);

        if (Info.Protocol != ProtocolVersion)
        {
            throw new InvalidOperationException(
                $"the driver mod speaks protocol {Info.Protocol}, but this library speaks {ProtocolVersion}");
        }

        Log.SetCursorFloor(Info.LogCursor);
    }

    private void OnNotification(JsonObject parameters)
    {
        // Nothing here may throw into the reader: it would close the connection and drop every later reply.
        GameEvent gameEvent;
        try
        {
            gameEvent = GameEventParser.Parse(parameters);
        }
        catch (Exception e)
        {
            Trace.TraceError($"the game sent an event that could not be read: {e}\n{parameters.ToJsonString()}");
            return;
        }

        Log.Add(gameEvent);
        try
        {
            switch (gameEvent)
            {
                case SceneEvent scene:
                    SceneChanged?.Invoke(scene);
                    break;
                case DialogEvent dialog:
                    DialogOpened?.Invoke(dialog);
                    break;
            }
        }
        catch (Exception e)
        {
            Trace.TraceError($"a GameSession event handler threw: {e}");
        }
    }

    private SnippetCompiler CreateCompiler()
    {
        var references = ReferenceSet.FromInstance(InstanceDir, options.ModAssemblyPath);
        var cache = Path.Combine(options.ResolveHome(), "snippets");
        return Compilers.GetOrAdd((references.Identity, cache), _ => new SnippetCompiler(references, cache));
    }

    private static async Task<RpcConnection> ConnectWhenListeningAsync(
        Process process,
        int port,
        TimeSpan bootTimeout,
        string? playerLog,
        ConsoleTail? consoleOutput,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        var modCheck = new ModLoadCheck(playerLog, port);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                // Waiting again lets the drained output reach its end, so the message holds the game's last words.
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                    .ContinueWith(_ => { }, TaskScheduler.Default);
                throw new GameExitedException(
                    $"the game exited with code {process.ExitCode} before the driver mod accepted a connection"
                    + consoleOutput?.Describe());
            }

            if (deadline.Elapsed > bootTimeout)
            {
                throw new TimeoutException($"the driver mod did not listen on port {port} within {bootTimeout}");
            }

            if (modCheck.Failure() is { } failure)
            {
                throw new GameBootException(failure);
            }

            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(5));
                return await RpcConnection.ConnectAsync(port, attempt.Token);
            }
            catch (Exception e) when (e is SocketException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
    }

    internal static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(30));
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill.
        }
    }
}
