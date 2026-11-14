using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using KspHarness.Script;
using UnityEngine;

namespace KspHarness.Mod.Runtime;

internal enum TickPoint
{
    Update,
    FixedUpdate,
    LateUpdate,
    EndOfFrame,
}

/// <summary>
/// Runs work on the main thread at the four points of a frame that the host component forwards. Awaitables parked
/// at a point are polled there, and the ones that are ready resume their continuation inline, so code after an
/// <c>await</c> runs at that point of the frame and not a frame later.
/// </summary>
internal static class Scheduler
{
    private const int PointCount = 4;

    private static readonly ConcurrentQueue<Action> Posted = new ConcurrentQueue<Action>();
    private static readonly object Gate = new object();
    private static readonly List<ScriptAwaitable>[] Parked = CreateLists<ScriptAwaitable>();
    private static readonly List<Action>[] Scheduled = CreateLists<Action>();

    private static int mainThreadId;

    public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == mainThreadId;

    public static void Initialize()
    {
        mainThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    /// <summary>Runs <paramref name="action"/> on the main thread at the next Update; callable from any thread.</summary>
    public static void Post(Action action) => Posted.Enqueue(action);

    /// <summary>Runs <paramref name="action"/> once, the next time the frame reaches <paramref name="point"/>.</summary>
    public static void Schedule(TickPoint point, Action action)
    {
        lock (Gate)
            Scheduled[(int)point].Add(action);
    }

    public static void Park(ScriptAwaitable awaitable, TickPoint point)
    {
        lock (Gate)
            Parked[(int)point].Add(awaitable);
    }

    /// <summary>
    /// Runs posted work, then the point's scheduled work, then polls the awaitables that were parked at the point
    /// before this tick began.
    /// </summary>
    public static void Tick(TickPoint point)
    {
        // Anything parked during this tick, by work started here or by continuations resumed here, belongs to the
        // next occurrence of the point: a snippet that starts in Update and awaits Tick.Update resumes a frame later.
        List<ScriptAwaitable> parked = Take(Parked, point);

        if (point == TickPoint.Update)
        {
            while (Posted.TryDequeue(out Action action))
                Invoke(action);
        }

        List<Action> actions = Take(Scheduled, point);
        if (actions != null)
        {
            foreach (Action action in actions)
                Invoke(action);
        }

        if (parked == null)
            return;

        foreach (ScriptAwaitable awaitable in parked)
        {
            bool resumed;
            try
            {
                resumed = awaitable.Step();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                resumed = true;
            }

            if (!resumed)
                Park(awaitable, point);
        }
    }

    private static List<T> Take<T>(List<T>[] lists, TickPoint point)
    {
        lock (Gate)
        {
            List<T> list = lists[(int)point];
            if (list.Count == 0)
                return null;

            lists[(int)point] = new List<T>();
            return list;
        }
    }

    private static void Invoke(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    private static List<T>[] CreateLists<T>()
    {
        var lists = new List<T>[PointCount];
        for (int i = 0; i < PointCount; i++)
            lists[i] = new List<T>();

        return lists;
    }
}
