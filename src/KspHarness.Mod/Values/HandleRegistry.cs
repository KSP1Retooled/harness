using System;
using System.Collections.Generic;

namespace KspHarness.Mod.Values;

/// <summary>
/// The objects a client holds handles to, kept alive until the client releases them or disconnects. Every result
/// gets a new id, so releasing one handle never invalidates another.
/// </summary>
internal sealed class HandleRegistry
{
    private readonly Dictionary<long, object> objects = new Dictionary<long, object>();
    private readonly object gate = new object();
    private long nextId = 1;

    public long Add(object value)
    {
        lock (gate)
        {
            long id = nextId++;
            objects[id] = value;
            return id;
        }
    }

    public void Release(long id)
    {
        lock (gate)
            objects.Remove(id);
    }

    public void Clear()
    {
        lock (gate)
            objects.Clear();
    }

    public int Count
    {
        get
        {
            lock (gate)
                return objects.Count;
        }
    }

    /// <summary>The live object behind <paramref name="id"/>; throws if it was released or destroyed.</summary>
    public object Resolve(long id)
    {
        object value;
        lock (gate)
        {
            if (!objects.TryGetValue(id, out value))
                throw new ArgumentException($"Handle {id} is unknown or was released");
        }

        // Unity's equality operator reports a destroyed object as null even though the reference is still set
        if (value is UnityEngine.Object unityObject && unityObject == null)
            throw new ObjectDisposedException(value.GetType().FullName, $"the object behind handle {id} was destroyed");

        return value;
    }
}

/// <summary>An argument that named a handle the mod cannot resolve; using it throws.</summary>
internal sealed class UnresolvedHandle
{
    public UnresolvedHandle(long id, Exception error)
    {
        Id = id;
        Error = error;
    }

    public long Id { get; }

    public Exception Error { get; }
}
