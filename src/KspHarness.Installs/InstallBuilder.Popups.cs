namespace KspHarness.Installs;

/// <summary>A run-time answer to a popup dialog: the button to press, or null to leave the dialog open.</summary>
/// <param name="Dialog">The dialog's name or title, ignoring case, with <c>*</c> and <c>?</c> wildcards.</param>
/// <param name="Button">The button caption, matched the same way; null leaves the dialog for the test to see.</param>
public sealed record DialogAnswer(string Dialog, string? Button);

/// <summary>
/// Closes an IMGUI window a mod draws, which cannot be found from outside: a snippet body that clears the mod's own
/// visibility flag, run only when an assembly named <see cref="Assembly"/> is loaded.
/// </summary>
public sealed record WindowCloser(string Name, string Assembly, string Source);

/// <summary>What one definition says about popups; <see cref="InstallLayer.Popups"/> merges the include chain.</summary>
public sealed class PopupChoices
{
    internal Dictionary<string, string> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal bool? AnswerKnown { get; set; }

    internal List<DialogAnswer> Dialogs { get; } = [];

    internal List<string> CloseButtons { get; } = [];

    internal List<WindowCloser> Closers { get; } = [];
}

public sealed partial class InstallBuilder
{
    internal PopupChoices Popups { get; } = new();

    /// <summary>
    /// Answers a known first-run popup (<see cref="KnownPopups"/>) with <paramref name="answer"/> instead of its
    /// default, for this definition and those that include it.
    /// </summary>
    public InstallBuilder AnswerPopup(string popup, string answer)
    {
        var known = KnownPopups.Get(popup);
        var match = known.Answers.FirstOrDefault(a => string.Equals(a, answer, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"{known.Id} takes the answers {string.Join(", ", known.Answers)}, not '{answer}'", nameof(answer));
        Popups.Answers[known.Id] = match;
        return this;
    }

    /// <summary>
    /// False stops the builder from answering known popups with files, from this definition up (and removes the
    /// answers an included definition wrote), so that a test can see them; true turns it back on. Answers given with
    /// <see cref="AnswerPopup"/> still apply.
    /// </summary>
    public InstallBuilder AnswerKnownPopups(bool answer)
    {
        Popups.AnswerKnown = answer;
        return this;
    }

    /// <summary>
    /// Makes the run-time policy press <paramref name="button"/> in dialogs matching <paramref name="dialog"/> (name or
    /// title), ahead of the default (Cancel, Close, OK, or the only button).
    /// </summary>
    public InstallBuilder AnswerDialog(string dialog, string button)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dialog);
        ArgumentException.ThrowIfNullOrWhiteSpace(button);
        Popups.Dialogs.Add(new DialogAnswer(dialog, button));
        return this;
    }

    /// <summary>Makes the run-time policy leave dialogs matching <paramref name="dialog"/> open.</summary>
    public InstallBuilder LeaveDialog(string dialog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dialog);
        Popups.Dialogs.Add(new DialogAnswer(dialog, null));
        return this;
    }

    /// <summary>
    /// Makes the run-time policy click uGUI buttons at <paramref name="path"/> (a <c>Ui.Find</c> path, such as
    /// <c>**/MyWindow/**/ButtonClose</c>) to close a mod's window.
    /// </summary>
    public InstallBuilder CloseWindowWith(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Popups.CloseButtons.Add(path);
        return this;
    }

    /// <summary>
    /// Adds a closer for an IMGUI window: <paramref name="source"/> is a snippet body that hides it (it compiles
    /// against the game and the mod's assembly), run while <paramref name="assembly"/> is loaded.
    /// </summary>
    public InstallBuilder CloseWindowWith(string name, string assembly, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Popups.Closers.Add(new WindowCloser(name, assembly, source));
        return this;
    }
}

/// <summary>A definition's popup choices merged with those of the definitions it includes.</summary>
public sealed class PopupSettings
{
    internal PopupSettings(
        IReadOnlyDictionary<string, string?> knownAnswers,
        IReadOnlyList<DialogAnswer> dialogs,
        IReadOnlyList<string> closeButtons,
        IReadOnlyList<WindowCloser> closers)
    {
        KnownAnswers = knownAnswers;
        Dialogs = dialogs;
        CloseButtons = closeButtons;
        Closers = closers;
    }

    /// <summary>Each known popup's answer, or null where the builder leaves it unanswered.</summary>
    public IReadOnlyDictionary<string, string?> KnownAnswers { get; }

    /// <summary>Run-time dialog answers, the including definition's first, so they win.</summary>
    public IReadOnlyList<DialogAnswer> Dialogs { get; }

    public IReadOnlyList<string> CloseButtons { get; }

    public IReadOnlyList<WindowCloser> Closers { get; }

    /// <summary>Goes into the instance hash: the files the builder writes follow from it.</summary>
    internal string Fingerprint => $"popups v{KnownPopups.Version} "
        + string.Join(" ", KnownAnswers.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value ?? "-"}"));

    internal static PopupSettings Merge(PopupSettings? included, PopupChoices own)
    {
        var known = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var popup in KnownPopups.All)
        {
            string? inherited = included is null ? popup.DefaultAnswer : included.KnownAnswers[popup.Id];
            // An explicit answer always counts, even in a definition that turns the rest off.
            known[popup.Id] = own.Answers.GetValueOrDefault(popup.Id) ?? own.AnswerKnown switch
            {
                false => null,
                true => inherited ?? popup.DefaultAnswer,
                null => inherited,
            };
        }

        return new PopupSettings(
            known,
            [.. own.Dialogs, .. included?.Dialogs ?? []],
            [.. own.CloseButtons, .. included?.CloseButtons ?? []],
            [.. own.Closers, .. included?.Closers ?? []]);
    }
}
