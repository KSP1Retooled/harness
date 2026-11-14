using System;
using KspHarness.Mod;

namespace KspHarness.Script;

/// <summary>Resolves handles the client holds; generated code declares captured handles with it.</summary>
public static class Handles
{
    /// <summary>
    /// The object behind handle <paramref name="id"/>. Throws <see cref="ObjectDisposedException"/> if it was a Unity
    /// object that has been destroyed, and <see cref="InvalidCastException"/> if it is not a <typeparamref name="T"/>.
    /// </summary>
    public static T Get<T>(long id)
    {
        var handles = RunContext.Current?.Handles ?? HarnessHost.Instance?.ActiveHandles;
        if (handles == null)
            throw new InvalidOperationException("No client is connected, so there are no handles");

        object value = handles.Resolve(id);
        if (value is T typed)
            return typed;

        throw new InvalidCastException($"Handle {id} is a {value.GetType()}, not a {typeof(T)}");
    }
}
