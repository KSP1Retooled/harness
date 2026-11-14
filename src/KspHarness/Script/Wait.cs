#nullable disable

using System;

namespace KspHarness.Script;

/// <summary>
/// Waits inside the game. The mod's parameters typed with game types (<c>GameScenes</c>, <c>EventVoid</c>,
/// <c>EventData&lt;T&gt;</c>) are <see cref="object"/> here, because the driver does not reference the game; the
/// snippet compiler binds the real overloads, where an event's arguments have their real types.
/// </summary>
public static class Wait
{
    /// <summary>Frames <see cref="Scene"/> waits after the scene is ready, so that late Start methods have run.</summary>
    public const int SceneSettleFrames = 10;

    public static ScriptAwaitable Until(Func<bool> condition, GameTime? timeout = null) => throw OutsideGame.Error();

    public static ScriptAwaitable GameSeconds(double seconds) => throw OutsideGame.Error();

    public static ScriptAwaitable RealSeconds(double seconds) => throw OutsideGame.Error();

    public static ScriptAwaitable Frames(int frames) => throw OutsideGame.Error();

    public static ScriptAwaitable Scene(object scene, GameTime? timeout = null) => throw OutsideGame.Error();

    public static ScriptAwaitable<dynamic> Event(object gameEvent) => throw OutsideGame.Error();
}
