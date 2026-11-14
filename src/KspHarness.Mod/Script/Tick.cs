using KspHarness.Mod.Runtime;

namespace KspHarness.Script;

/// <summary>Awaitables that resume at a point of the next frame or physics step, on the main thread.</summary>
public static class Tick
{
    public static ScriptAwaitable Update => new ScriptAwaitable(TickPoint.Update);

    /// <summary>Resumes in the next FixedUpdate, where <c>Time.inFixedTimeStep</c> is true.</summary>
    public static ScriptAwaitable FixedUpdate => new ScriptAwaitable(TickPoint.FixedUpdate);

    public static ScriptAwaitable LateUpdate => new ScriptAwaitable(TickPoint.LateUpdate);

    /// <summary>Resumes after rendering, where the frame can be read back.</summary>
    public static ScriptAwaitable EndOfFrame => new ScriptAwaitable(TickPoint.EndOfFrame);
}
