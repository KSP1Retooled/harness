using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KSP.Localization;
using KspHarness.ConfigNodes;
using KspHarness.Mod;
using KspHarness.Mod.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace KspHarness.Script;

/// <summary>
/// Finds and clicks the game's UI: uGUI canvases and the 3D text buttons of the main menu. Only active objects are
/// found.
/// </summary>
/// <remarks>
/// Paths are object names from a scene root separated by <c>/</c>. A segment matches one name with <c>*</c> and
/// <c>?</c> wildcards (case-sensitive), and <c>**</c> matches any number of levels: <c>**/ButtonStart</c> finds
/// that object anywhere. Text patterns match the visible text of <see cref="Text"/> and <see cref="TMP_Text"/>
/// components as shown (localization tags resolved, rich-text tags removed), ignoring case and surrounding space,
/// with the same wildcards.
/// </remarks>
public static class Ui
{
    private const int DescribeTextLength = 60;

    private static readonly Regex RichTextTag = new Regex("<[^>]*>");
    private static readonly Regex Whitespace = new Regex(@"\s+");

    /// <summary>The first active object whose path matches <paramref name="path"/>, or null.</summary>
    public static GameObject Find(string path) => FindAll(path).FirstOrDefault();

    /// <summary>Every active object whose path matches <paramref name="path"/>, in hierarchy order.</summary>
    public static List<GameObject> FindAll(string path)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("A path is required", nameof(path));

        string[] pattern = path.Split('/');
        var matches = new List<GameObject>();
        foreach (GameObject root in ActiveRoots())
            Match(root.transform, pattern, 0, matches);

        return matches.Distinct().ToList();
    }

    /// <summary>The first active object showing text that matches <paramref name="text"/>, or null.</summary>
    public static GameObject FindByText(string text) => FindAllByText(text).FirstOrDefault();

    /// <summary>Every active object showing text that matches <paramref name="text"/>, in hierarchy order.</summary>
    public static List<GameObject> FindAllByText(string text)
    {
        if (text == null)
            throw new ArgumentNullException(nameof(text));

        string pattern = Normalize(text).ToLowerInvariant();
        return Walk()
            .Where(go => VisibleTexts(go).Any(shown => Wildcard.IsMatch(pattern, shown.ToLowerInvariant())))
            .ToList();
    }

    /// <summary>Every active and enabled <typeparamref name="T"/> in the loaded scenes, in hierarchy order.</summary>
    public static List<T> FindAll<T>()
        where T : Component
    {
        return Walk().SelectMany(go => go.GetComponents<T>()).Where(IsEnabled).ToList();
    }

    /// <summary>
    /// Every active and enabled component whose type, or a base type, has the name or full name
    /// <paramref name="typeName"/>; <c>"Selectable"</c> finds buttons, toggles and sliders.
    /// </summary>
    public static List<Component> FindByType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            throw new ArgumentException("A type name is required", nameof(typeName));

        return Walk()
            .SelectMany(go => go.GetComponents<Component>())
            .Where(component => component != null && IsEnabled(component) && HasTypeName(component.GetType(), typeName))
            .ToList();
    }

    /// <summary>The visible text on <paramref name="target"/> and its active children, one line per component.</summary>
    public static string TextOf(GameObject target)
    {
        if (target == null)
            throw new ArgumentNullException(nameof(target));

        var lines = new List<string>();
        foreach (Transform child in target.GetComponentsInChildren<Transform>(false))
            lines.AddRange(VisibleTexts(child.gameObject));

        return string.Join("\n", lines);
    }

    /// <summary>The path of <paramref name="target"/> from its scene root, as <see cref="Find"/> takes it.</summary>
    public static string PathOf(GameObject target)
    {
        if (target == null)
            throw new ArgumentNullException(nameof(target));

        var names = new List<string>();
        for (Transform t = target.transform; t != null; t = t.parent)
            names.Add(t.name);

        names.Reverse();
        return string.Join("/", names);
    }

    /// <summary>
    /// Clicks <paramref name="target"/> as the mouse would, and resumes at the next Update. A uGUI target gets
    /// pointer enter, down, up, click and exit through <see cref="ExecuteEvents"/>, so a <see cref="Button"/> runs
    /// its <c>onClick</c> and KSP's own handlers see a real click; a 3D object with a collider (the main menu's
    /// buttons) gets Unity's <c>OnMouseEnter</c>, <c>OnMouseDown</c>, <c>OnMouseUp</c>, <c>OnMouseUpAsButton</c> and
    /// <c>OnMouseExit</c> messages. Throws when nothing there takes clicks or the control is not interactable.
    /// </summary>
    public static ScriptAwaitable Click(GameObject target)
    {
        if (target == null)
            throw new ArgumentNullException(nameof(target), "Nothing to click: the target is null or was destroyed");

        if (!target.activeInHierarchy)
            throw new InvalidOperationException("Cannot click " + PathOf(target) + ": it is not active");

        if (
            ExecuteEvents.GetEventHandler<IPointerClickHandler>(target) != null
            || ExecuteEvents.GetEventHandler<IPointerDownHandler>(target) != null
        )
        {
            ClickUi(target);
        }
        else
        {
            ClickCollider(target);
        }

        return Tick.Update;
    }

    public static ScriptAwaitable Click(Component target)
    {
        if (target == null)
            throw new ArgumentNullException(nameof(target), "Nothing to click: the target is null or was destroyed");

        return Click(target.gameObject);
    }

    /// <summary>
    /// Clicks the only clickable object showing text that matches <paramref name="text"/>; throws when there is none
    /// or more than one.
    /// </summary>
    public static ScriptAwaitable ClickText(string text)
    {
        List<GameObject> clickable = FindAllByText(text).Where(IsClickable).ToList();
        if (clickable.Count == 0)
            throw new InvalidOperationException("No clickable object shows the text '" + text + "'");

        // A label inside a button and the button's own text component can both match; they click the same thing
        List<GameObject> targets = clickable.Select(ClickTarget).Distinct().ToList();
        if (targets.Count > 1)
        {
            throw new InvalidOperationException(
                "More than one clickable object shows the text '"
                    + text
                    + "': "
                    + string.Join(", ", targets.Select(PathOf))
            );
        }

        return Click(clickable[0]);
    }

    /// <summary>
    /// A compact tree of the active root canvases (only the objects that show text or take clicks, with their
    /// ancestors) and the 3D buttons with colliders, for finding paths and texts to use.
    /// </summary>
    public static string Describe()
    {
        var text = new StringBuilder();
        IEnumerable<Canvas> canvases = Object
            .FindObjectsOfType<Canvas>()
            .Where(canvas => canvas.isRootCanvas && canvas.isActiveAndEnabled)
            .OrderByDescending(canvas => canvas.sortingOrder)
            .ThenBy(canvas => PathOf(canvas.gameObject), StringComparer.Ordinal);

        foreach (Canvas canvas in canvases)
        {
            text.Append("Canvas ")
                .Append(PathOf(canvas.gameObject))
                .Append(" (")
                .Append(canvas.renderMode)
                .Append(", order ")
                .Append(canvas.sortingOrder)
                .Append(")\n");
            DescribeChildren(canvas.transform, 1, text);
        }

        List<GameObject> buttons = Walk()
            .Where(go => IsMouseButton(go) && go.GetComponentInParent<Canvas>() == null)
            .ToList();
        if (buttons.Count > 0)
        {
            text.Append("3D buttons\n");
            foreach (GameObject button in buttons)
            {
                text.Append("  ").Append(PathOf(button));
                AppendNotes(button, text);
                text.Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>Like <see cref="Describe()"/>, for the subtrees of the objects matching <paramref name="path"/>.</summary>
    public static string Describe(string path)
    {
        var text = new StringBuilder();
        foreach (GameObject match in FindAll(path))
        {
            text.Append(PathOf(match));
            AppendNotes(match, text);
            text.Append('\n');
            DescribeChildren(match.transform, 1, text);
        }

        return text.ToString();
    }

    internal static string Normalize(string text) =>
        text == null ? string.Empty : Whitespace.Replace(RichTextTag.Replace(text, string.Empty), " ").Trim();

    /// <summary>The object a click on <paramref name="target"/> reaches first, or null if none takes clicks.</summary>
    internal static GameObject ClickTarget(GameObject target) =>
        ExecuteEvents.GetEventHandler<IPointerClickHandler>(target)
        ?? ExecuteEvents.GetEventHandler<IPointerDownHandler>(target)
        ?? MouseTarget(target);

    private static bool IsClickable(GameObject target) => ClickTarget(target) != null;

    private static void ClickUi(GameObject target)
    {
        GameObject handler = ClickTarget(target);
        Selectable selectable = handler.GetComponent<Selectable>();
        if (selectable != null && !selectable.IsInteractable())
            throw new InvalidOperationException("Cannot click " + PathOf(handler) + ": it is not interactable");

        Vector2 position = ScreenPosition(target);
        var data = new PointerEventData(EventSystem.current)
        {
            button = PointerEventData.InputButton.Left,
            position = position,
            pressPosition = position,
            clickCount = 1,
            clickTime = Time.unscaledTime,
            eligibleForClick = true,
            pointerEnter = target,
            pointerCurrentRaycast = new RaycastResult { gameObject = target, screenPosition = position },
            pointerPressRaycast = new RaycastResult { gameObject = target, screenPosition = position },
        };

        // The order and targets follow StandaloneInputModule: enter goes to the target and every ancestor, down
        // bubbles to the first handler, and up and click go to the object that took the press
        for (Transform t = target.transform; t != null; t = t.parent)
        {
            ExecuteEvents.Execute(t.gameObject, data, ExecuteEvents.pointerEnterHandler);
            data.hovered.Add(t.gameObject);
        }

        GameObject pressed =
            ExecuteEvents.ExecuteHierarchy(target, data, ExecuteEvents.pointerDownHandler)
            ?? ExecuteEvents.GetEventHandler<IPointerClickHandler>(target);
        data.pointerPress = pressed;
        data.rawPointerPress = target;

        if (pressed != null)
        {
            ExecuteEvents.Execute(pressed, data, ExecuteEvents.pointerUpHandler);
            if (pressed == ExecuteEvents.GetEventHandler<IPointerClickHandler>(target))
                ExecuteEvents.Execute(pressed, data, ExecuteEvents.pointerClickHandler);
        }

        data.eligibleForClick = false;
        data.pointerPress = null;
        data.rawPointerPress = null;
        foreach (GameObject hovered in data.hovered)
            ExecuteEvents.Execute(hovered, data, ExecuteEvents.pointerExitHandler);

        data.hovered.Clear();
        data.pointerEnter = null;
    }

    private static void ClickCollider(GameObject target)
    {
        GameObject receiver = MouseTarget(target);
        if (receiver == null)
        {
            throw new InvalidOperationException(
                "Cannot click " + PathOf(target) + ": neither it nor a parent takes uGUI clicks or has a collider"
            );
        }

        // A disabled collider is how KSP turns a 3D button off, as while the main menu shows a dialog
        if (!receiver.GetComponent<Collider>().enabled)
            throw new InvalidOperationException("Cannot click " + PathOf(receiver) + ": its collider is disabled");

        // Unity sends these to the collider's object in this order for a press and release over it
        foreach (
            string message in new[] { "OnMouseEnter", "OnMouseDown", "OnMouseUp", "OnMouseUpAsButton", "OnMouseExit" }
        )
            receiver.SendMessage(message, SendMessageOptions.DontRequireReceiver);
    }

    private static GameObject MouseTarget(GameObject target)
    {
        Collider collider = target.GetComponentInParent<Collider>();
        return collider != null ? collider.gameObject : null;
    }

    private static bool IsMouseButton(GameObject go) =>
        go.GetComponent<Collider>() != null && go.GetComponents<MonoBehaviour>().Any(b => b is IMouseEvents);

    private static Vector2 ScreenPosition(GameObject target)
    {
        if (!(target.transform is RectTransform rect))
            return new Vector2(Screen.width / 2f, Screen.height / 2f);

        Canvas canvas = target.GetComponentInParent<Canvas>();
        Camera camera =
            canvas == null || canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : canvas.rootCanvas.worldCamera;
        return RectTransformUtility.WorldToScreenPoint(camera, rect.TransformPoint(rect.rect.center));
    }

    private static void Match(Transform node, string[] pattern, int index, List<GameObject> matches)
    {
        if (!node.gameObject.activeInHierarchy)
            return;

        string segment = pattern[index];
        if (segment == "**")
        {
            // Zero levels: the rest of the pattern applies to this node; one or more: it applies further down
            if (index == pattern.Length - 1)
            {
                matches.Add(node.gameObject);
            }
            else
            {
                Match(node, pattern, index + 1, matches);
            }

            foreach (Transform child in node)
                Match(child, pattern, index, matches);

            return;
        }

        if (!Wildcard.IsMatch(segment, node.name))
            return;

        if (index == pattern.Length - 1)
        {
            matches.Add(node.gameObject);
            return;
        }

        foreach (Transform child in node)
            Match(child, pattern, index + 1, matches);
    }

    private static void DescribeChildren(Transform parent, int depth, StringBuilder text)
    {
        foreach (Transform child in parent)
        {
            if (!child.gameObject.activeInHierarchy || !ShowsSomething(child))
                continue;

            text.Append(' ', depth * 2).Append(child.name);
            AppendNotes(child.gameObject, text);
            text.Append('\n');
            DescribeChildren(child, depth + 1, text);
        }
    }

    private static bool ShowsSomething(Transform node)
    {
        foreach (Transform t in node.GetComponentsInChildren<Transform>(false))
        {
            if (Notable(t.gameObject))
                return true;
        }

        return false;
    }

    private static bool Notable(GameObject go) =>
        VisibleTexts(go).Any() || go.GetComponent<Selectable>() != null || IsMouseButton(go);

    private static void AppendNotes(GameObject go, StringBuilder text)
    {
        foreach (Selectable selectable in go.GetComponents<Selectable>())
        {
            text.Append(" [").Append(selectable.GetType().Name);
            if (!selectable.IsInteractable())
                text.Append(", not interactable");

            text.Append(']');
        }

        if (IsMouseButton(go))
            text.Append(go.GetComponent<Collider>().enabled ? " [3D button]" : " [3D button, disabled]");

        foreach (string shown in VisibleTexts(go))
        {
            string clipped = shown.Length > DescribeTextLength ? shown.Substring(0, DescribeTextLength) + "..." : shown;
            text.Append(" \"").Append(clipped).Append('"');
        }
    }

    private static IEnumerable<string> VisibleTexts(GameObject go)
    {
        foreach (Text label in go.GetComponents<Text>())
        {
            string shown = label.isActiveAndEnabled ? Normalize(Localized(label.text)) : string.Empty;
            if (shown.Length > 0)
                yield return shown;
        }

        foreach (TMP_Text label in go.GetComponents<TMP_Text>())
        {
            string shown = label.isActiveAndEnabled ? Normalize(Localized(label.text)) : string.Empty;
            if (shown.Length > 0)
                yield return shown;
        }
    }

    // KSP's TMP_Text holds localization tags such as #autoLOC_190905 and resolves them only when it draws
    private static string Localized(string text) =>
        text != null && text.IndexOf('#') >= 0 && Localizer.Instance != null ? Localizer.Format(text) : text;

    private static bool IsEnabled(Component component) =>
        !(component is Behaviour behaviour) || behaviour.isActiveAndEnabled;

    private static bool HasTypeName(Type type, string name)
    {
        for (; type != null; type = type.BaseType)
        {
            if (type.Name == name || type.FullName == name)
                return true;
        }

        return false;
    }

    /// <summary>Active objects in hierarchy order: every loaded scene, then the objects kept across scenes.</summary>
    private static IEnumerable<GameObject> Walk()
    {
        var stack = new Stack<Transform>();
        foreach (GameObject root in ActiveRoots())
        {
            stack.Push(root.transform);
            while (stack.Count > 0)
            {
                Transform node = stack.Pop();
                yield return node.gameObject;
                for (int i = node.childCount - 1; i >= 0; i--)
                {
                    Transform child = node.GetChild(i);
                    if (child.gameObject.activeSelf)
                        stack.Push(child);
                }
            }
        }
    }

    private static IEnumerable<GameObject> ActiveRoots()
    {
        var scenes = new List<Scene>();
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (scene.isLoaded)
                scenes.Add(scene);
        }

        // DontDestroyOnLoad objects live in a scene SceneManager does not list; the mod's own host is one of them
        if (HarnessHost.Instance != null)
            scenes.Add(HarnessHost.Instance.gameObject.scene);

        foreach (Scene scene in scenes)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.activeSelf)
                    yield return root;
            }
        }
    }
}
