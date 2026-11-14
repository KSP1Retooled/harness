using KspHarness.Installs;

namespace KspHarness;

/// <summary>
/// How a session closes popups that nobody is there to answer: <c>PopupDialog</c>s by rule or default button, uGUI
/// windows by their close buttons, and IMGUI windows through per-mod closers. Every dialog it answers is recorded as a
/// <c>dialog</c> event naming the button (<see cref="GameLog.DialogsSince"/>), so a test can still assert that none
/// appeared.
/// </summary>
public sealed record PopupPolicy
{
    /// <summary>The library's rules and closers, answering everything else with the default buttons.</summary>
    public static PopupPolicy Default { get; } = new()
    {
        // Pressing Escape on it only brings it back, and the instance builder's file gives the same answer.
        Dialogs = [new DialogAnswer("TextureLoaderOptimizations", "Yes")],

        // Stock's first-run windows on the main menu (Unity Analytics privacy notice, What's New, the expansion
        // adverts) sit on the dialog canvas with a Close button in their footer.
        CloseButtons = ["_UIMaster/DialogCanvas/*(Clone)/Footer/ButtonClose"],
        Closers = WindowClosers.All,
    };

    /// <summary>Closes nothing, for a test about the popups themselves.</summary>
    public static PopupPolicy None { get; } = new() { AnswerOthers = false, Settle = TimeSpan.Zero };

    /// <summary>Dialog rules, the first match by name or title wins; a null button leaves the dialog open.</summary>
    public IReadOnlyList<DialogAnswer> Dialogs { get; init; } = [];

    /// <summary>Captions tried in order on a dialog no rule names, before its only button.</summary>
    public IReadOnlyList<string> DefaultButtons { get; init; } = ["Cancel", "Close", "OK"];

    /// <summary>False answers only the dialogs a rule names and leaves windows alone.</summary>
    public bool AnswerOthers { get; init; } = true;

    /// <summary><c>Ui.Find</c> paths of buttons that close uGUI windows; every active match is clicked.</summary>
    public IReadOnlyList<string> CloseButtons { get; init; } = [];

    /// <summary>Snippets that hide IMGUI windows, each run while its mod's assembly is loaded.</summary>
    public IReadOnlyList<WindowCloser> Closers { get; init; } = [];

    /// <summary>
    /// How long a sweep keeps watching once it has started (or, waiting for the main menu, once the menu has settled),
    /// for popups that open a moment after the scene.
    /// </summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>This policy with an install definition's choices in front of its own.</summary>
    public PopupPolicy With(PopupSettings settings)
    {
        return this with
        {
            Dialogs = [.. settings.Dialogs, .. Dialogs],
            CloseButtons = [.. settings.CloseButtons, .. CloseButtons],
            Closers = [.. settings.Closers, .. Closers],
        };
    }

    /// <summary>The default policy with the choices of <paramref name="definition"/> and those it includes.</summary>
    public static PopupPolicy For(InstallDefinition definition) => Default.With(InstallPlan.Create(definition).Popups);
}

/// <summary>What a sweep closed or left open, in the order it happened.</summary>
/// <param name="Kind"><c>dialog</c>, <c>window</c> (uGUI) or <c>imgui</c>.</param>
/// <param name="Name">The dialog's name, the close button's path or the closer's name.</param>
/// <param name="Detail">The title and the button pressed, or why it was left open.</param>
public sealed record ClosedPopup(string Kind, string Name, string Detail)
{
    public override string ToString() => $"{Kind} {Name}: {Detail}";
}

/// <summary>
/// Closers for the IMGUI windows of common mods. The instance builder normally answers these with files
/// (<see cref="KnownPopups"/>); the closers cover instances built without those answers.
/// </summary>
public static class WindowClosers
{
    /// <summary>Toolbar Controller's intro window (KSC); hides it for this run only, as its OK button does.</summary>
    public static WindowCloser ToolbarControllerIntro { get; } = new(
        "ToolbarController.Intro",
        "ToolbarControl",
        """
        bool shown = ToolbarControl_NS.IntroWindowClass.showHelp;
        ToolbarControl_NS.IntroWindowClass.showHelp = false;
        return shown;
        """);

    /// <summary>
    /// Click Through Blocker's focus model window (KSC, each new game); closes it as Cancel does, without choosing,
    /// so the next new game asks again.
    /// </summary>
    public static WindowCloser ClickThroughBlockerFocus { get; } = new(
        "ClickThroughBlocker.Focus",
        "ClickThroughBlocker",
        """
        var popup = ClickThroughFix.OneTimePopup.Instance;
        if (popup == null || !popup.visible)
        {
            return false;
        }

        popup.visible = false;
        Object.Destroy(popup);
        return true;
        """);

    public static IReadOnlyList<WindowCloser> All { get; } = [ToolbarControllerIntro, ClickThroughBlockerFocus];
}
