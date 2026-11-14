using System;
using System.Globalization;
using KspHarness.Mod.Events;
using KspHarness.Mod.Runtime;
using UnityEngine;

namespace KspHarness.Script;

/// <summary>Awaitables that resume, in Update, once something has happened.</summary>
public static class Wait
{
    /// <summary>The frames <see cref="Scene"/> waits after a scene is ready, for its UI and late starters.</summary>
    public const int SceneSettleFrames = 10;

    /// <summary>
    /// Resumes in the first Update where <paramref name="condition"/> is true, including the current one; throws
    /// <see cref="TimeoutException"/> once <paramref name="timeout"/> of game time has passed.
    /// </summary>
    public static ScriptAwaitable Until(Func<bool> condition, GameTime? timeout = null)
    {
        if (condition == null)
            throw new ArgumentNullException(nameof(condition));

        return new UntilAwaitable(condition, Deadline(timeout), "the condition");
    }

    public static ScriptAwaitable GameSeconds(double seconds) =>
        new ClockAwaitable(GameClock.GameSeconds + seconds, game: true);

    public static ScriptAwaitable RealSeconds(double seconds) =>
        new ClockAwaitable(GameClock.RealSeconds + seconds, game: false);

    /// <summary>Resumes in the Update <paramref name="frames"/> frames from now.</summary>
    public static ScriptAwaitable Frames(int frames) => new FramesAwaitable(Time.frameCount + frames);

    /// <summary>
    /// Resumes once <paramref name="scene"/> is loaded and its load has finished (for FLIGHT, once
    /// <c>FlightGlobals.ready</c> with an active vessel), and then <see cref="SceneSettleFrames"/> more frames.
    /// </summary>
    public static ScriptAwaitable Scene(GameScenes scene, GameTime? timeout = null) =>
        new SceneAwaitable(scene, Deadline(timeout));

    public static ScriptAwaitable Event(EventVoid gameEvent) => new VoidEventAwaitable(gameEvent);

    public static ScriptAwaitable<T> Event<T>(EventData<T> gameEvent) => new EventAwaitable<T>(gameEvent);

    public static ScriptAwaitable<(T1, T2)> Event<T1, T2>(EventData<T1, T2> gameEvent) =>
        new EventAwaitable<T1, T2>(gameEvent);

    private static double? Deadline(GameTime? timeout) =>
        timeout.HasValue ? GameClock.GameSeconds + timeout.Value.TotalSeconds : (double?)null;

    private static TimeoutException TimedOut(string what, double deadline) =>
        new TimeoutException(
            string.Format(
                CultureInfo.InvariantCulture,
                "Timed out waiting for {0} (game-time deadline {1:0.###} s)",
                what,
                deadline
            )
        );

    private sealed class UntilAwaitable : ScriptAwaitable
    {
        private readonly Func<bool> condition;
        private readonly double? deadline;
        private readonly string what;

        public UntilAwaitable(Func<bool> condition, double? deadline, string what)
            : base(TickPoint.Update)
        {
            this.condition = condition;
            this.deadline = deadline;
            this.what = what;
        }

        internal override bool IsReadyNow() => condition();

        internal override bool Poll()
        {
            if (condition())
                return true;

            if (deadline.HasValue && GameClock.GameSeconds >= deadline.Value)
                throw TimedOut(what, deadline.Value);

            return false;
        }
    }

    private sealed class ClockAwaitable : ScriptAwaitable
    {
        private readonly double until;
        private readonly bool game;

        public ClockAwaitable(double until, bool game)
            : base(TickPoint.Update)
        {
            this.until = until;
            this.game = game;
        }

        internal override bool IsReadyNow() => Now >= until;

        internal override bool Poll() => Now >= until;

        private double Now => game ? GameClock.GameSeconds : GameClock.RealSeconds;
    }

    private sealed class FramesAwaitable : ScriptAwaitable
    {
        private readonly int frame;

        public FramesAwaitable(int frame)
            : base(TickPoint.Update)
        {
            this.frame = frame;
        }

        internal override bool IsReadyNow() => Time.frameCount >= frame;

        internal override bool Poll() => Time.frameCount >= frame;
    }

    private sealed class SceneAwaitable : ScriptAwaitable
    {
        private readonly GameScenes scene;
        private readonly double? deadline;
        private int readyFrames;

        public SceneAwaitable(GameScenes scene, double? deadline)
            : base(TickPoint.Update)
        {
            this.scene = scene;
            this.deadline = deadline;
        }

        internal override bool Poll()
        {
            // A scene that stops being ready (another load started) has to settle again from the start
            readyFrames = SceneTracker.IsReady(scene) ? readyFrames + 1 : 0;
            if (readyFrames > SceneSettleFrames)
                return true;

            if (deadline.HasValue && GameClock.GameSeconds >= deadline.Value)
                throw TimedOut("scene " + scene, deadline.Value);

            return false;
        }
    }

    // Events resume at the next Update rather than inside Fire: KSP's events share one clone list per event, so
    // code that fired the same event from inside a handler would corrupt the delivery in progress.
    private sealed class VoidEventAwaitable : ScriptAwaitable
    {
        private readonly EventVoid gameEvent;
        private volatile bool fired;

        public VoidEventAwaitable(EventVoid gameEvent)
            : base(TickPoint.Update)
        {
            this.gameEvent = gameEvent ?? throw new ArgumentNullException(nameof(gameEvent));
            gameEvent.Add(OnFired);
        }

        internal override bool IsReadyNow() => fired;

        internal override bool Poll() => fired;

        internal override void OnFinished() => gameEvent.Remove(OnFired);

        private void OnFired() => fired = true;
    }

    private sealed class EventAwaitable<T> : ScriptAwaitable<T>
    {
        private readonly EventData<T> gameEvent;
        private volatile bool fired;

        public EventAwaitable(EventData<T> gameEvent)
            : base(TickPoint.Update)
        {
            this.gameEvent = gameEvent ?? throw new ArgumentNullException(nameof(gameEvent));
            gameEvent.Add(OnFired);
        }

        internal override bool IsReadyNow() => fired;

        internal override bool Poll() => fired;

        internal override void OnFinished() => gameEvent.Remove(OnFired);

        private void OnFired(T data)
        {
            if (fired)
                return;

            Result = data;
            fired = true;
        }
    }

    private sealed class EventAwaitable<T1, T2> : ScriptAwaitable<(T1, T2)>
    {
        private readonly EventData<T1, T2> gameEvent;
        private volatile bool fired;

        public EventAwaitable(EventData<T1, T2> gameEvent)
            : base(TickPoint.Update)
        {
            this.gameEvent = gameEvent ?? throw new ArgumentNullException(nameof(gameEvent));
            gameEvent.Add(OnFired);
        }

        internal override bool IsReadyNow() => fired;

        internal override bool Poll() => fired;

        internal override void OnFinished() => gameEvent.Remove(OnFired);

        private void OnFired(T1 first, T2 second)
        {
            if (fired)
                return;

            Result = (first, second);
            fired = true;
        }
    }
}
