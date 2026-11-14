using System;
using System.Threading.Tasks;

namespace KspHarness.Script;

/// <summary>
/// Turns whatever a typed lambda returns into the <c>Task&lt;object&gt;</c> a snippet's entry point returns; the
/// overloads let C#'s natural lambda type pick the right one.
/// </summary>
public static class Results
{
    public static async Task<object> Box<T>(Task<T> task) => await task;

    public static async Task<object> Box(Task task)
    {
        await task;
        return null;
    }

    public static Task<object> Box<T>(T value) => Task.FromResult<object>(value);

    public static Task<object> Box(Action action)
    {
        action();
        return Task.FromResult<object>(null);
    }
}
