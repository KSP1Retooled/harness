using System;
using System.Diagnostics;
using System.Threading;
using KspHarness.Mod.Events;
using UnityEngine;

namespace KspHarness.Mod.Runtime;

/// <summary>
/// The clocks that timeouts and waits use, readable from any thread. Game time follows the universal time while a
/// game with a planetarium is running (so it advances with time warp and stops while paused or in the editors) and
/// Unity's scaled time elsewhere, such as the main menu. It never goes backwards, and loading a save does not move it
/// by the gap between the two games' universal times.
/// </summary>
internal static class GameClock
{
    private static readonly Stopwatch Real = Stopwatch.StartNew();
    private static double gameSeconds;
    private static double lastUniversalTime;
    private static bool hadUniversalTime;
    private static Game lastGame;
    private static bool wasLoading;
    // Boxed so that other threads never see a torn Nullable<double>
    private static object universalTime;
    private static int frame;

    /// <summary>Seconds of game time since the mod started.</summary>
    public static double GameSeconds => Volatile.Read(ref gameSeconds);

    /// <summary>Seconds of real time since the mod started.</summary>
    public static double RealSeconds => Real.Elapsed.TotalSeconds;

    /// <summary>Universal time at the start of this frame, or null outside a game.</summary>
    public static double? UniversalTime => universalTime as double?;

    /// <summary><see cref="Time.frameCount"/> at the start of the latest Update, for other threads.</summary>
    public static int Frame => Volatile.Read(ref frame);

    /// <summary>Moves the clocks to this frame; call first thing in Update.</summary>
    public static void Advance()
    {
        Volatile.Write(ref frame, Time.frameCount);

        double? now = ReadUniversalTime();
        universalTime = now.HasValue ? (object)now.Value : null;

        // A loaded save sets its universal time while its scene starts, some frames after it became the current game,
        // so a change of game and every frame of a scene load are breaks in the universal time rather than warp
        Game game = HighLogic.CurrentGame;
        bool loading = SceneTracker.Instance != null && SceneTracker.Instance.Loading;
        bool continuous = now.HasValue && hadUniversalTime && game == lastGame && !loading && !wasLoading;

        double delta;
        if (continuous)
            delta = Math.Max(0, now.Value - lastUniversalTime);
        else
            delta = Time.deltaTime;

        hadUniversalTime = now.HasValue;
        lastUniversalTime = now ?? 0;
        lastGame = game;
        wasLoading = loading;
        Volatile.Write(ref gameSeconds, gameSeconds + delta);
    }

    private static double? ReadUniversalTime()
    {
        if (HighLogic.CurrentGame == null || Planetarium.fetch == null)
            return null;

        return Planetarium.GetUniversalTime();
    }
}
