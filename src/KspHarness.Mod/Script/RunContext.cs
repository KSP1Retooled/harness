using System;
using System.Globalization;
using System.Threading;
using KspHarness.Mod.Runtime;
using KspHarness.Mod.Values;

namespace KspHarness.Script;

/// <summary>The running snippet's cancellation and deadlines; flows with the snippet across awaits.</summary>
public sealed class RunContext
{
    private static readonly AsyncLocal<RunContext> CurrentContext = new AsyncLocal<RunContext>();

    private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
    private readonly object gate = new object();
    private Action<RunContext> onAborted;
    private int reason;

    internal RunContext(double? gameDeadline, double? realDeadline, HandleRegistry handles, Action<RunContext> onAborted)
    {
        GameDeadline = gameDeadline;
        RealDeadline = realDeadline;
        Handles = handles;
        this.onAborted = onAborted;
    }

    /// <summary>The context of the snippet running on this logical call chain, or null outside a snippet.</summary>
    public static RunContext Current => CurrentContext.Value;

    /// <summary>Cancelled when the run is cancelled, times out or loses its client.</summary>
    public CancellationToken CancellationToken => cancellation.Token;

    /// <summary>The game-time deadline in <see cref="GameTime.Now"/> seconds, or null.</summary>
    public double? GameDeadline { get; }

    /// <summary>The real-time deadline in seconds since the mod started, or null.</summary>
    public double? RealDeadline { get; }

    public bool IsAborted => Reason != AbortReason.None;

    internal AbortReason Reason => (AbortReason)Volatile.Read(ref reason);

    internal HandleRegistry Handles { get; }

    /// <summary>Throws if the run was cancelled or passed a deadline.</summary>
    public void ThrowIfAborted()
    {
        if (!CheckAborted())
            return;

        switch (Reason)
        {
            case AbortReason.TimedOutGame:
                throw new TimeoutException(string.Format(
                    CultureInfo.InvariantCulture, "The run passed its game-time deadline ({0:0.###} s)", GameDeadline));
            case AbortReason.TimedOutReal:
                throw new TimeoutException(string.Format(
                    CultureInfo.InvariantCulture, "The run passed its real-time deadline ({0:0.###} s)", RealDeadline));
            default:
                throw new OperationCanceledException("The run was cancelled", cancellation.Token);
        }
    }

    internal static void SetCurrent(RunContext context) => CurrentContext.Value = context;

    /// <summary>Checks the deadlines, aborting the run if one has passed; returns whether the run is aborted.</summary>
    internal bool CheckAborted()
    {
        if (IsAborted)
            return true;

        if (GameDeadline.HasValue && GameClock.GameSeconds >= GameDeadline.Value)
            Abort(AbortReason.TimedOutGame);
        else if (RealDeadline.HasValue && GameClock.RealSeconds >= RealDeadline.Value)
            Abort(AbortReason.TimedOutReal);

        return IsAborted;
    }

    internal void Abort(AbortReason why)
    {
        Action<RunContext> callback;
        lock (gate)
        {
            if (IsAborted)
                return;

            Volatile.Write(ref reason, (int)why);
            callback = onAborted;
            onAborted = null;
        }

        callback?.Invoke(this);

        try
        {
            cancellation.Cancel();
        }
        catch (AggregateException e)
        {
            UnityEngine.Debug.LogException(e);
        }
    }
}

internal enum AbortReason
{
    None,
    Cancelled,
    Disconnected,
    TimedOutGame,
    TimedOutReal,
}
