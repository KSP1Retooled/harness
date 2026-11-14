using System;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using KspHarness.Mod.Runtime;

namespace KspHarness.Script;

/// <summary>
/// What <see cref="Tick"/> and <see cref="Wait"/> return. Awaiting one parks the continuation until the mod's tick
/// at the named point of the frame finds it ready, and then runs the continuation right there on the main thread.
/// </summary>
/// <remarks>
/// When it resumes it checks the running snippet's <see cref="RunContext"/>, so a cancelled or timed-out snippet
/// gets an <see cref="OperationCanceledException"/> or <see cref="TimeoutException"/> from its next await.
/// </remarks>
public class ScriptAwaitable : ICriticalNotifyCompletion
{
    private readonly TickPoint point;
    private Action continuation;
    private RunContext context;
    private ExceptionDispatchInfo failure;
    private volatile bool finished;

    internal ScriptAwaitable(TickPoint point)
    {
        this.point = point;
    }

    public ScriptAwaitable GetAwaiter() => this;

    public bool IsCompleted => finished || IsReadyNow();

    public void OnCompleted(Action continuation) => Park(continuation);

    public void UnsafeOnCompleted(Action continuation) => Park(continuation);

    public void GetResult()
    {
        // An awaitable that was ready before the await never parked, so it never ran its cleanup
        if (!finished)
        {
            finished = true;
            OnFinished();
        }

        failure?.Throw();
        (context ?? RunContext.Current)?.ThrowIfAborted();
    }

    /// <summary>Whether awaiting can skip the wait, checked before parking.</summary>
    internal virtual bool IsReadyNow() => false;

    /// <summary>Called at the awaitable's point on the main thread; true resumes the continuation.</summary>
    internal virtual bool Poll() => true;

    /// <summary>Called once, on the main thread, before the continuation resumes.</summary>
    internal virtual void OnFinished()
    {
    }

    /// <summary>Polls and resumes if ready or if the run was aborted; returns whether it resumed.</summary>
    internal bool Step()
    {
        bool ready;
        if (context != null && context.CheckAborted())
        {
            ready = true;
        }
        else
        {
            try
            {
                ready = Poll();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
                ready = true;
            }
        }

        if (!ready)
            return false;

        finished = true;
        OnFinished();
        Action resume = continuation;
        continuation = null;
        resume?.Invoke();
        return true;
    }

    private void Park(Action continuation)
    {
        context = RunContext.Current;
        this.continuation = continuation;
        Scheduler.Park(this, point);
    }
}

/// <summary>A <see cref="ScriptAwaitable"/> that produces a value, such as the arguments of a game event.</summary>
public class ScriptAwaitable<T> : ScriptAwaitable
{
    internal ScriptAwaitable(TickPoint point)
        : base(point)
    {
    }

    internal T Result { get; set; }

    public new ScriptAwaitable<T> GetAwaiter() => this;

    public new T GetResult()
    {
        base.GetResult();
        return Result;
    }
}
