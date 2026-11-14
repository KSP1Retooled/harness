using KspHarness.Script;

namespace KspHarness;

/// <summary>Where in the game's frame a snippet starts.</summary>
public enum TickPoint
{
    Update,
    FixedUpdate,
    LateUpdate,
    EndOfFrame,

    /// <summary>The next main-thread opportunity, which is the next Update.</summary>
    Now,
}

public sealed record RunOptions
{
    public static RunOptions Default { get; } = new();

    public TickPoint At { get; init; } = TickPoint.Update;

    /// <summary>Game time the snippet may take, or null for no limit.</summary>
    public GameTime? Timeout { get; init; }

    /// <summary>Real time the snippet may take.</summary>
    public TimeSpan? RealTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Extra namespaces for the snippet, as in <c>"KSP.UI.Screens.Flight"</c> or <c>"static System.Math"</c>.</summary>
    public IReadOnlyList<string> Usings { get; init; } = [];
}

/// <summary>Settings shared by launched and attached sessions.</summary>
public record SessionOptions
{
    /// <summary>
    /// The harness home for caches. Defaults to <c>KSPHARNESS_HOME</c>, else a <c>KspHarness</c> folder under the
    /// user's local application data.
    /// </summary>
    public string? HarnessHome { get; init; }

    /// <summary>
    /// The driver mod assembly snippets compile against. Defaults to the one deployed in the instance's GameData.
    /// </summary>
    public string? ModAssemblyPath { get; init; }

    /// <summary>How long <c>hello</c> and other control requests may take.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal string ResolveHome()
    {
        if (HarnessHome is not null)
        {
            return HarnessHome;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("KSPHARNESS_HOME");
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            return fromEnvironment;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KspHarness");
    }
}

public sealed record LaunchOptions : SessionOptions
{
    /// <summary>
    /// A built instance with the driver mod in its GameData. <see cref="GameSession.LaunchAsync(Installs.Instance,
    /// LaunchOptions?, CancellationToken)"/> sets it from the instance.
    /// </summary>
    public string InstanceDir { get; init; } = "";

    /// <summary>
    /// The game executable. Defaults to the instance's own: <c>KSP_x64.exe</c>, <c>KSP.x86_64</c>, or on macOS the
    /// binary inside <c>KSP.app</c> (<c>KSP.app/Contents/MacOS/KSP</c>).
    /// </summary>
    public string? Executable { get; init; }

    /// <summary>
    /// Whether the game runs in a window (<c>-screen-fullscreen 0</c>). The driver mod also writes this into the game's
    /// settings, so that settings.cfg cannot change it.
    /// </summary>
    public bool Windowed { get; init; } = true;

    /// <summary>The window or screen width (<c>-screen-width</c>); null leaves the game's setting.</summary>
    public int? Width { get; init; } = 1280;

    /// <summary>The window or screen height (<c>-screen-height</c>); null leaves the game's setting.</summary>
    public int? Height { get; init; } = 720;

    /// <summary>Unity's player log, passed as <c>-logFile</c>. Null leaves the game's default.</summary>
    public string? PlayerLogPath { get; init; }

    /// <summary>More command-line arguments for the game, after the harness's own.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    /// <summary>The port for the mod to listen on; null picks a free one.</summary>
    public int? Port { get; init; }

    /// <summary>How long the game may take to start listening.</summary>
    public TimeSpan BootTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long <see cref="GameSession.DisposeAsync"/> waits after asking the game to quit before killing it.</summary>
    public TimeSpan QuitTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>What the game said in reply to <c>hello</c>.</summary>
public sealed record GameInfo(
    int Protocol,
    int Pid,
    string Ksp,
    bool Retooled,
    string Unity,
    string Scene,
    bool Loaded,
    long LogCursor);
