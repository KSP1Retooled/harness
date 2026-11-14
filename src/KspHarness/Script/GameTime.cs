#nullable disable

using System;
using System.Globalization;

namespace KspHarness.Script;

/// <summary>
/// A span or point of game (universal) time in seconds. The same struct exists in the mod, so a value built in a
/// test means the same thing in the game.
/// </summary>
public readonly struct GameTime : IEquatable<GameTime>, IComparable<GameTime>
{
    private GameTime(double totalSeconds)
    {
        TotalSeconds = totalSeconds;
    }

    public double TotalSeconds { get; }

    /// <summary>The game's current universal time; only the game knows it.</summary>
    public static GameTime Now => throw OutsideGame.Error();

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

    public override string ToString() => TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s";
}
