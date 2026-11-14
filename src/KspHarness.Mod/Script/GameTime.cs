using System;
using System.Globalization;
using KspHarness.Mod.Runtime;

namespace KspHarness.Script;

/// <summary>
/// An amount of game time: universal time while a game with a planetarium is running (it advances with warp and
/// stops while paused or in the editors), Unity's scaled time elsewhere.
/// </summary>
public struct GameTime : IEquatable<GameTime>, IComparable<GameTime>
{
    private GameTime(double totalSeconds)
    {
        TotalSeconds = totalSeconds;
    }

    public double TotalSeconds { get; }

    /// <summary>Game time elapsed since the mod started.</summary>
    public static GameTime Now => new GameTime(GameClock.GameSeconds);

    public static GameTime Seconds(double seconds) => new GameTime(seconds);

    public static GameTime operator +(GameTime a, GameTime b) => new GameTime(a.TotalSeconds + b.TotalSeconds);

    public static GameTime operator -(GameTime a, GameTime b) => new GameTime(a.TotalSeconds - b.TotalSeconds);

    public static bool operator <(GameTime a, GameTime b) => a.TotalSeconds < b.TotalSeconds;

    public static bool operator >(GameTime a, GameTime b) => a.TotalSeconds > b.TotalSeconds;

    public static bool operator ==(GameTime a, GameTime b) => a.Equals(b);

    public static bool operator !=(GameTime a, GameTime b) => !a.Equals(b);

    public bool Equals(GameTime other) => TotalSeconds.Equals(other.TotalSeconds);

    public override bool Equals(object obj) => obj is GameTime other && Equals(other);

    public override int GetHashCode() => TotalSeconds.GetHashCode();

    public int CompareTo(GameTime other) => TotalSeconds.CompareTo(other.TotalSeconds);

    public override string ToString() =>
        TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s game time";
}
