using System.Collections.Generic;
using System.Reflection;
using KspHarness.Mod.Protocol;
using UnityEngine;

namespace KspHarness.Mod.Events;

/// <summary>
/// Reports each <see cref="PopupDialog"/> the game opens as a <c>dialog</c> event, while a client listens, and each one
/// that a snippet answers through <see cref="KspHarness.Script.Dialogs"/>.
/// </summary>
internal sealed class DialogWatcher
{
    public const string Opened = "opened";
    public const string Clicked = "clicked";
    public const string Dismissed = "dismissed";

    private const float PollIntervalSeconds = 0.25f;

    // Every dialog that SpawnPopupDialog makes without persistAcrossScenes, wherever it was moved since
    private static readonly FieldInfo SpawnedField = typeof(PopupDialog).GetField(
        "instantiatedPopUps",
        BindingFlags.NonPublic | BindingFlags.Static
    );

    private readonly EventHub hub;

    // Open dialogs by instance id, and whether they have been reported
    private readonly Dictionary<int, bool> open = new Dictionary<int, bool>();
    private readonly HashSet<int> stillOpen = new HashSet<int>();
    private readonly List<int> closed = new List<int>();
    private float nextPoll;

    public DialogWatcher(EventHub hub)
    {
        this.hub = hub;
    }

    /// <summary>The running host's watcher; null when the harness did not start.</summary>
    public static DialogWatcher Current { get; set; }

    /// <summary>
    /// The open dialogs: those under the game's popup canvas, where SpawnPopupDialog puts every dialog, and those
    /// it lists as spawned. Looking only there keeps the watcher's polling off a scan of every object in the scene.
    /// </summary>
    public static List<PopupDialog> OpenPopups()
    {
        var found = new List<PopupDialog>();
        var ids = new HashSet<int>();

        PopupDialogController controller = PopupDialogController.Instance;
        if (controller != null && controller.popupDialogCanvas != null)
        {
            foreach (PopupDialog popup in controller.popupDialogCanvas.GetComponentsInChildren<PopupDialog>())
                Add(popup, found, ids);
        }

        if (SpawnedField?.GetValue(null) is List<PopupDialog> spawned)
        {
            foreach (PopupDialog popup in spawned)
                Add(popup, found, ids);
        }

        return found;
    }

    /// <summary>
    /// The <c>dialog</c> event for a snippet's answer to <paramref name="popup"/>, taken before the answer runs
    /// because answering can close or change the dialog. Publish it with <see cref="Answered"/> once the answer
    /// succeeded.
    /// </summary>
    public static JsonObject Answer(PopupDialog popup, string action, string button) => Describe(popup, action, button);

    /// <summary>
    /// Publishes an answer from <see cref="Answer"/>. The event carries the whole dialog, because a dialog answered
    /// within a poll interval of opening was never reported as opened.
    /// </summary>
    public void Answered(int id, JsonObject answer)
    {
        open[id] = true;
        if (hub.HasClient)
            hub.Publish("dialog", answer);
    }

    /// <summary>Call once a frame; dialogs already open when a client connects are reported to it.</summary>
    public void Update()
    {
        if (!hub.HasClient)
        {
            open.Clear();
            return;
        }

        if (Time.realtimeSinceStartup < nextPoll)
            return;

        nextPoll = Time.realtimeSinceStartup + PollIntervalSeconds;

        stillOpen.Clear();
        foreach (PopupDialog popup in OpenPopups())
        {
            int id = popup.GetInstanceID();
            stillOpen.Add(id);

            // Reported on the second sighting: a dialog is filled in after it spawns, and some close at once
            if (!open.TryGetValue(id, out bool reported))
            {
                open[id] = false;
            }
            else if (!reported)
            {
                open[id] = true;
                hub.Publish("dialog", Describe(popup, Opened, null));
            }
        }

        closed.Clear();
        foreach (int id in open.Keys)
        {
            if (!stillOpen.Contains(id))
                closed.Add(id);
        }

        foreach (int id in closed)
            open.Remove(id);
    }

    private static void Add(PopupDialog popup, List<PopupDialog> found, HashSet<int> ids)
    {
        if (popup == null || popup.dialogToDisplay == null || !popup.isActiveAndEnabled)
            return;

        if (ids.Add(popup.GetInstanceID()))
            found.Add(popup);
    }

    private static JsonObject Describe(PopupDialog popup, string action, string button)
    {
        MultiOptionDialog dialog = popup.dialogToDisplay;
        var buttons = new List<object>();
        CollectButtons(dialog.Options, buttons);
        return new JsonObject
        {
            { "id", popup.GetInstanceID() },
            { "name", dialog.name },
            { "title", dialog.title },
            { "message", dialog.message },
            { "buttons", buttons },
            { "action", action },
            { "button", button },
        };
    }

    private static void CollectButtons(IEnumerable<DialogGUIBase> options, List<object> captions)
    {
        if (options == null)
            return;

        foreach (DialogGUIBase option in options)
        {
            if (option is DialogGUIButton button)
                captions.Add(button.GetString != null ? button.GetString() : button.OptionText);

            CollectButtons(option.children, captions);
        }
    }
}
