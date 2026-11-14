#nullable disable

using System.Collections.Generic;

namespace KspHarness.Script;

/// <summary>
/// Finds and clicks the game's UI. Game types are <c>dynamic</c> or <see cref="object"/> here, because the driver
/// does not reference the game: <c>Find</c> returns a <c>GameObject</c> in the game, and <c>Click</c> takes a
/// <c>GameObject</c> or a <c>Component</c>.
/// </summary>
public static class Ui
{
    public static dynamic Find(string path) => throw OutsideGame.Error();

    public static List<dynamic> FindAll(string path) => throw OutsideGame.Error();

    public static dynamic FindByText(string text) => throw OutsideGame.Error();

    public static List<dynamic> FindAllByText(string text) => throw OutsideGame.Error();

    public static List<T> FindAll<T>() => throw OutsideGame.Error();

    public static List<dynamic> FindByType(string typeName) => throw OutsideGame.Error();

    public static string TextOf(object target) => throw OutsideGame.Error();

    public static string PathOf(object target) => throw OutsideGame.Error();

    public static ScriptAwaitable Click(object target) => throw OutsideGame.Error();

    public static ScriptAwaitable ClickText(string text) => throw OutsideGame.Error();

    public static string Describe() => throw OutsideGame.Error();

    public static string Describe(string path) => throw OutsideGame.Error();
}
