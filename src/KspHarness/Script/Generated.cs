#nullable disable

using System;
using System.Threading;
using System.Threading.Tasks;

namespace KspHarness.Script;

/// <summary>Decodes snippet arguments in the game. Used by generated code.</summary>
public static class Args
{
    public static T Get<T>(object[] args, int index) => throw OutsideGame.Error();
}

/// <summary>Boxes snippet results in the game.</summary>
public static class Results
{
    public static Task<object> Box<T>(Task<T> task) => throw OutsideGame.Error();

    public static Task<object> Box(Task task) => throw OutsideGame.Error();

    public static Task<object> Box<T>(T value) => throw OutsideGame.Error();

    public static Task<object> Box(Action action) => throw OutsideGame.Error();
}

/// <summary>Resolves handles to the objects the game keeps for them.</summary>
public static class Handles
{
    public static T Get<T>(long id) => throw OutsideGame.Error();
}

/// <summary>The running snippet's cancellation and deadlines.</summary>
public sealed class RunContext
{
    private RunContext() { }

    public static RunContext Current => throw OutsideGame.Error();

    public CancellationToken CancellationToken => throw OutsideGame.Error();

    public double? GameDeadline => throw OutsideGame.Error();

    public double? RealDeadline => throw OutsideGame.Error();

    public bool IsAborted => throw OutsideGame.Error();

    public void ThrowIfAborted() => throw OutsideGame.Error();
}
