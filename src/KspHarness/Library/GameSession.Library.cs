using KspHarness.Library;
using KspHarness.Snippets;

namespace KspHarness;

/// <summary>Common steps, run as the library's own snippets.</summary>
public sealed partial class GameSession
{
    /// <summary>How long a library step that loads a scene may take, in real time, when the caller gives no limit.</summary>
    public static readonly TimeSpan DefaultSceneTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Waits until the main menu is loaded and settled, closing popups by <see cref="Popups"/> on the way (while
    /// loading too) and for <see cref="PopupPolicy.Settle"/> after; <see cref="ClosedPopups"/> lists them.
    /// </summary>
    public Task WaitForMainMenuAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return WaitForMainMenuSweepingAsync(timeout, cancellationToken);
    }

    /// <summary>
    /// Starts a new sandbox game from the main menu, replacing any save of that name, and waits for the space center.
    /// </summary>
    public Task StartSandboxAsync(string name, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return RunLibraryAsync(LibrarySnippets.StartSandbox, [name], timeout, cancellationToken);
    }

    /// <summary>
    /// Launches a craft with its default crew and waits until the flight is ready. <paramref name="craft"/> is a craft
    /// name, looked up in the save's and the install's <c>Ships</c> folders, or a <c>.craft</c> path (relative to the
    /// instance or absolute). <paramref name="site"/> is <c>LaunchPad</c>, <c>Runway</c> or another launch site name.
    /// </summary>
    public Task LaunchCraftAsync(
        string craft = "Kerbal X",
        string site = "LaunchPad",
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return RunLibraryAsync(LibrarySnippets.Launch, [craft, site], timeout, cancellationToken);
    }

    /// <summary>
    /// Loads <c>saves/&lt;folder&gt;/&lt;game&gt;.sfs</c> as the main menu's Resume Game does and waits for the scene it
    /// was saved in; a save made in flight (a quicksave) resumes the flight with its active vessel unpacked.
    /// </summary>
    public Task LoadSaveAsync(
        string folder,
        string game = "persistent",
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return RunLibraryAsync(LibrarySnippets.LoadSave, [folder, game], timeout, cancellationToken);
    }

    /// <summary>
    /// Saves the current game as <c>saves/&lt;save folder&gt;/&lt;name&gt;.sfs</c>, as the quicksave key does. In flight
    /// it throws when the game refuses to save (moving on the ground, in atmosphere and so on).
    /// </summary>
    public Task QuickSaveAsync(string name = "quicksave", TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return RunLibraryAsync(LibrarySnippets.QuickSave, [name], timeout, cancellationToken);
    }

    /// <summary>Loads the main menu without saving and waits for it.</summary>
    public Task ReturnToMainMenuAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return RunLibraryAsync(LibrarySnippets.ReturnToMainMenu, [], timeout, cancellationToken);
    }

    private Task RunLibraryAsync(
        LibrarySnippet snippet,
        object?[] arguments,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var prepared = SnippetBuilder.FromString(snippet.Source, returnsValue: false, [], snippet.File, snippet.Line)
            with { Arguments = arguments };
        var options = RunOptions.Default with { RealTimeout = timeout ?? DefaultSceneTimeout };
        return RunAsync(prepared, options, keep: false, cancellationToken);
    }
}
