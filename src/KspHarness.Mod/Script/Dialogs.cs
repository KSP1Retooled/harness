using System;
using System.Collections.Generic;
using System.Linq;
using KspHarness.ConfigNodes;
using KspHarness.Mod.Events;
using KspHarness.Mod.Protocol;
using KspHarness.Mod.Runtime;

namespace KspHarness.Script;

/// <summary>An open <c>PopupDialog</c>, as <see cref="Dialogs.Open"/> lists it. Travels to the driver by value.</summary>
public readonly struct DialogInfo
{
    public DialogInfo(string name, string title, string message, string[] buttons)
        : this(0, name, title, message, buttons) { }

    public DialogInfo(int id, string name, string title, string message, string[] buttons)
    {
        Id = id;
        Name = name;
        Title = title;
        Message = message;
        Buttons = buttons;
    }

    /// <summary>
    /// The dialog's instance id, for <see cref="Dialogs.Click(int, string)"/> and <see cref="Dialogs.Dismiss(int)"/>
    /// when several open dialogs share a name; <c>dialog</c> events carry the same id.
    /// </summary>
    public int Id { get; }

    /// <summary>The dialog's <c>MultiOptionDialog.name</c>, which code uses to find it.</summary>
    public string Name { get; }

    /// <summary>The title without rich-text tags.</summary>
    public string Title { get; }

    /// <summary>The message without rich-text tags.</summary>
    public string Message { get; }

    /// <summary>The button captions, in layout order.</summary>
    public string[] Buttons { get; }

    public override string ToString() =>
        Name + " \"" + Title + "\" [" + string.Join(", ", Buttons ?? new string[0]) + "]";
}

/// <summary>
/// The game's popup dialogs (<c>PopupDialog</c>). <c>dialog</c> parameters match a dialog's name or title, ignoring
/// case and rich-text tags, with <c>*</c> and <c>?</c> wildcards; <c>button</c> parameters match captions the same way.
/// Every click and dismissal made here is reported to the driver as a <c>dialog</c> event naming the button.
/// </summary>
public static class Dialogs
{
    /// <summary>The dialogs open now.</summary>
    public static List<DialogInfo> Open => OpenPopups().Select(Describe).ToList();

    /// <summary>
    /// Clicks the button captioned <paramref name="button"/> in the one open dialog that has it, and resumes at
    /// the next Update. Throws when no dialog or more than one has such a button.
    /// </summary>
    public static ScriptAwaitable Click(string button) => Click(null, button);

    /// <summary>Clicks <paramref name="button"/> in the dialog matching <paramref name="dialog"/>.</summary>
    public static ScriptAwaitable Click(string dialog, string button) => Click(Matching(dialog), dialog, button);

    /// <summary>Clicks <paramref name="button"/> in the open dialog whose <see cref="DialogInfo.Id"/> is <paramref name="dialog"/>.</summary>
    public static ScriptAwaitable Click(int dialog, string button) => Click(WithId(dialog), "#" + dialog, button);

    private static ScriptAwaitable Click(IEnumerable<PopupDialog> popups, string dialog, string button)
    {
        if (string.IsNullOrEmpty(button))
            throw new ArgumentException("A button caption is required", nameof(button));

        var candidates = new List<(PopupDialog Popup, DialogGUIButton Button)>();
        foreach (PopupDialog popup in popups)
        {
            DialogGUIButton match = Buttons(popup.dialogToDisplay.Options)
                .FirstOrDefault(b => Names(Caption(b), button));
            if (match != null)
                candidates.Add((popup, match));
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "No open dialog"
                    + (dialog == null ? "" : " matching '" + dialog + "'")
                    + " has a button '"
                    + button
                    + "'. Open: "
                    + Summary()
            );
        }

        if (candidates.Count > 1)
        {
            throw new InvalidOperationException(
                "More than one open dialog has a button '" + button + "'; name the dialog. Open: " + Summary()
            );
        }

        DialogGUIButton target = candidates[0].Button;
        if (target.OptionInteractableCondition != null && !target.OptionInteractableCondition())
            throw new InvalidOperationException("The button '" + button + "' is disabled");

        PopupDialog answered = candidates[0].Popup;
        int id = answered.GetInstanceID();
        JsonObject answer = DialogWatcher.Answer(answered, DialogWatcher.Clicked, Ui.Normalize(Caption(target)));

        // Through the real button when it is on screen, so the dialog's uGUI handlers run as for a mouse click
        ScriptAwaitable resume;
        if (target.uiItem != null && target.uiItem.activeInHierarchy && Ui.ClickTarget(target.uiItem) != null)
        {
            resume = Ui.Click(target.uiItem);
        }
        else
        {
            target.OptionSelected();
            resume = Tick.Update;
        }

        DialogWatcher.Current?.Answered(id, answer);
        return resume;
    }

    /// <summary>
    /// Closes the dialogs matching <paramref name="dialog"/> the way Escape does (its dismiss callback, then
    /// <c>Dismiss</c>), and returns how many it closed.
    /// </summary>
    public static int Dismiss(string dialog) => Dismiss(Matching(dialog));

    /// <summary>Closes the open dialog whose <see cref="DialogInfo.Id"/> is <paramref name="dialog"/> as Escape does.</summary>
    public static int Dismiss(int dialog) => Dismiss(WithId(dialog));

    private static int Dismiss(IEnumerable<PopupDialog> popups)
    {
        int count = 0;
        foreach (PopupDialog popup in popups.ToList())
        {
            int id = popup.GetInstanceID();
            JsonObject answer = DialogWatcher.Answer(popup, DialogWatcher.Dismissed, null);
            popup.OnDismiss?.Invoke();
            popup.Dismiss();
            DialogWatcher.Current?.Answered(id, answer);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Resumes in the first Update where a dialog matching <paramref name="dialog"/> (any dialog when null) is
    /// open, with that dialog; throws <see cref="TimeoutException"/> once <paramref name="timeout"/> of game time
    /// has passed.
    /// </summary>
    public static ScriptAwaitable<DialogInfo> WaitFor(string dialog = null, GameTime? timeout = null) =>
        new DialogAwaitable(
            dialog,
            timeout.HasValue ? GameClock.GameSeconds + timeout.Value.TotalSeconds : (double?)null
        );

    private static IEnumerable<PopupDialog> OpenPopups() => DialogWatcher.OpenPopups();

    private static IEnumerable<PopupDialog> Matching(string dialog) =>
        OpenPopups()
            .Where(popup =>
                dialog == null
                || Names(popup.dialogToDisplay.name, dialog)
                || Names(popup.dialogToDisplay.title, dialog)
            );

    private static IEnumerable<PopupDialog> WithId(int id) => OpenPopups().Where(popup => popup.GetInstanceID() == id);

    private static DialogInfo Describe(PopupDialog popup)
    {
        MultiOptionDialog dialog = popup.dialogToDisplay;
        return new DialogInfo(
            popup.GetInstanceID(),
            dialog.name,
            Ui.Normalize(dialog.title),
            Ui.Normalize(dialog.message),
            Buttons(dialog.Options).Select(b => Ui.Normalize(Caption(b))).ToArray()
        );
    }

    private static string Summary()
    {
        List<DialogInfo> open = Open;
        return open.Count == 0 ? "none" : string.Join("; ", open.Select(d => d.ToString()));
    }

    private static IEnumerable<DialogGUIButton> Buttons(IEnumerable<DialogGUIBase> options)
    {
        if (options == null)
            yield break;

        foreach (DialogGUIBase option in options)
        {
            if (option is DialogGUIButton button)
                yield return button;

            foreach (DialogGUIButton child in Buttons(option.children))
                yield return child;
        }
    }

    private static string Caption(DialogGUIButton button) =>
        button.GetString != null ? button.GetString() : button.OptionText;

    private static bool Names(string text, string pattern) =>
        text != null
        && Wildcard.IsMatch(Ui.Normalize(pattern).ToLowerInvariant(), Ui.Normalize(text).ToLowerInvariant());

    private sealed class DialogAwaitable : ScriptAwaitable<DialogInfo>
    {
        private readonly string dialog;
        private readonly double? deadline;

        public DialogAwaitable(string dialog, double? deadline)
            : base(TickPoint.Update)
        {
            this.dialog = dialog;
            this.deadline = deadline;
        }

        internal override bool IsReadyNow() => TryFind();

        internal override bool Poll()
        {
            if (TryFind())
                return true;

            if (deadline.HasValue && GameClock.GameSeconds >= deadline.Value)
            {
                throw new TimeoutException(
                    "Timed out waiting for " + (dialog == null ? "a dialog" : "the dialog '" + dialog + "'")
                );
            }

            return false;
        }

        private bool TryFind()
        {
            PopupDialog popup = Matching(dialog).FirstOrDefault();
            if (popup == null)
                return false;

            Result = Describe(popup);
            return true;
        }
    }
}
