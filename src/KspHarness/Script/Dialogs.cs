#nullable disable

using System.Collections.Generic;

namespace KspHarness.Script;

/// <summary>An open popup dialog. The game sends it by value, so a test can read the one a run returns.</summary>
public readonly struct DialogInfo
{
    public DialogInfo(string name, string title, string message, string[] buttons)
        : this(0, name, title, message, buttons)
    {
    }

    public DialogInfo(int id, string name, string title, string message, string[] buttons)
    {
        Id = id;
        Name = name;
        Title = title;
        Message = message;
        Buttons = buttons;
    }

    /// <summary>The dialog's instance id in the game, as <c>dialog</c> events carry it.</summary>
    public int Id { get; }

    public string Name { get; }

    public string Title { get; }

    public string Message { get; }

    public string[] Buttons { get; }

    public override string ToString() => Name + " \"" + Title + "\" [" + string.Join(", ", Buttons ?? new string[0]) + "]";
}

/// <summary>The game's popup dialogs.</summary>
public static class Dialogs
{
    public static List<DialogInfo> Open => throw OutsideGame.Error();

    public static ScriptAwaitable Click(string button) => throw OutsideGame.Error();

    public static ScriptAwaitable Click(string dialog, string button) => throw OutsideGame.Error();

    public static ScriptAwaitable Click(int dialog, string button) => throw OutsideGame.Error();

    public static int Dismiss(string dialog) => throw OutsideGame.Error();

    public static int Dismiss(int dialog) => throw OutsideGame.Error();

    public static ScriptAwaitable<DialogInfo> WaitFor(string dialog = null, GameTime? timeout = null) =>
        throw OutsideGame.Error();
}
