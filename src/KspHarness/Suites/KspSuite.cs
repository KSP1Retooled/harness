using KspHarness.Installs;

namespace KspHarness.Suites;

/// <summary>
/// A group of tests that share an install. The runner creates one object per test, so fields do not carry over from
/// one test to the next; the game does.
/// </summary>
public abstract class KspSuite
{
    /// <summary>The running game, shared by every test of the install definition.</summary>
    public GameSession Game { get; internal set; } = null!;

    /// <summary>The built instance the game runs in.</summary>
    public Instance Instance { get; internal set; } = null!;

    /// <summary>The starting install the instance was built from.</summary>
    public StartingInstall Start { get; internal set; } = null!;

    /// <summary>The game log's cursor when the test started, after the reset.</summary>
    public long TestStart { get; internal set; }

    /// <summary>The log cursor when the current game connected, so a test can look at the whole boot.</summary>
    public long GameStart { get; internal set; }

    /// <summary>Notes and files kept with the test's result.</summary>
    public TestEvidence Evidence { get; internal set; } = null!;

    /// <summary>Cancelled when the test's time is up; pass it to every call that takes one.</summary>
    public CancellationToken Cancel { get; internal set; }

    internal abstract Type InstallType { get; }

    /// <summary>
    /// Brings the game to a known state before each test. The default goes back to the main menu without saving and
    /// closes the popups there by <see cref="GameSession.Popups"/> (the install definition's choices on top of
    /// <see cref="PopupPolicy.Default"/>).
    /// </summary>
    public virtual async Task ResetAsync()
    {
        await Game.ReturnToMainMenuAsync(cancellationToken: Cancel);
        await Game.DismissDialogsAsync(cancellationToken: Cancel);
    }

    /// <summary>
    /// Errors and exceptions logged since <paramref name="since"/> whose stack runs through a mod's code (any plugin
    /// outside <c>GameData/Squad*</c> other than the driver mod). Opt in by calling it from a test.
    /// </summary>
    public IReadOnlyList<LogEvent> ModErrors(long since)
    {
        return Game.Log.ErrorsSince(since)
            .Where(e => e.Mods.Any(mod => !mod.StartsWith("KspHarness.Mod@", StringComparison.Ordinal)))
            .ToList();
    }
}

/// <summary>A suite whose tests run in an instance built from <typeparamref name="TInstall"/>.</summary>
public abstract class KspSuite<TInstall> : KspSuite
    where TInstall : InstallDefinition, new()
{
    internal sealed override Type InstallType => typeof(TInstall);
}
