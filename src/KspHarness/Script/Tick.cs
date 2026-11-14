#nullable disable

using System;
using System.Runtime.CompilerServices;

namespace KspHarness.Script;

/// <summary>Awaitables that resume at a point of the next frame or physics step.</summary>
public static class Tick
{
    public static ScriptAwaitable Update => throw OutsideGame.Error();

    public static ScriptAwaitable FixedUpdate => throw OutsideGame.Error();

    public static ScriptAwaitable LateUpdate => throw OutsideGame.Error();

    public static ScriptAwaitable EndOfFrame => throw OutsideGame.Error();
}

/// <summary>What the mod's waits return; it is its own awaiter.</summary>
public class ScriptAwaitable : ICriticalNotifyCompletion
{
    private protected ScriptAwaitable() { }

    public bool IsCompleted => throw OutsideGame.Error();

    public ScriptAwaitable GetAwaiter() => throw OutsideGame.Error();

    public void OnCompleted(Action continuation) => throw OutsideGame.Error();

    public void UnsafeOnCompleted(Action continuation) => throw OutsideGame.Error();

    public void GetResult() => throw OutsideGame.Error();
}

public class ScriptAwaitable<T> : ScriptAwaitable
{
    private protected ScriptAwaitable() { }

    public new ScriptAwaitable<T> GetAwaiter() => throw OutsideGame.Error();

    public new T GetResult() => throw OutsideGame.Error();
}

internal static class OutsideGame
{
    public static InvalidOperationException Error()
    {
        return new InvalidOperationException("only inside Game.Run: this member runs in the game, not in the test");
    }
}
