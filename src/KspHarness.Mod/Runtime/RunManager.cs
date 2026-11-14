using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using KspHarness.Mod.Protocol;
using KspHarness.Mod.Values;
using KspHarness.Script;

namespace KspHarness.Mod.Runtime;

/// <summary>Handles <c>load</c>, <c>run</c>, <c>cancel</c> and <c>release</c>, and replies when runs finish.</summary>
internal sealed class RunManager
{
    private readonly Dictionary<RunKey, Run> runs = new Dictionary<RunKey, Run>();
    private readonly object gate = new object();

    /// <summary>Loads on the reader thread, so a <c>run</c> that follows its <c>load</c> finds the assembly.</summary>
    public void Load(Request request)
    {
        string hash = Required<string>(request.Params, "hash");
        if (SnippetLoader.Find(hash) == null)
        {
            byte[] assembly = Convert.FromBase64String(Required<string>(request.Params, "assembly"));
            request.Params.TryGetValue("symbols", out object symbolsValue);
            byte[] symbols = symbolsValue is string text ? Convert.FromBase64String(text) : null;
            SnippetLoader.Load(hash, assembly, symbols);
        }

        request.Connection.SendResult(request.Id, new JsonObject());
    }

    /// <summary>Starts a run at its tick point; call on the main thread in Update.</summary>
    public void Start(Request request)
    {
        Dictionary<string, object> p = request.Params;
        string hash = Required<string>(p, "hash");
        Assembly assembly = SnippetLoader.Find(hash);
        if (assembly == null)
            throw new RpcException(ErrorCodes.UnknownSnippet, $"Snippet {hash} is not loaded");

        string typeName = Required<string>(p, "type");
        string methodName = Required<string>(p, "method");
        TickPoint? point = ParsePoint(Optional<string>(p, "at") ?? "Now");
        if (point == null)
            throw new RpcException(
                ErrorCodes.InvalidParams,
                "at must be Update, FixedUpdate, LateUpdate, EndOfFrame or Now"
            );

        Type type = assembly.GetType(typeName);
        MethodInfo method = type?.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            null,
            new[] { typeof(object[]) },
            null
        );
        if (method == null)
            throw new RpcException(
                ErrorCodes.InvalidParams,
                $"{typeName}.{methodName}(object[]) is not in snippet {hash}"
            );

        object[] arguments;
        try
        {
            p.TryGetValue("args", out object argsJson);
            arguments = ValueDecoder.DecodeArguments(argsJson, request.Connection.Handles);
        }
        catch (FormatException e)
        {
            throw new RpcException(ErrorCodes.InvalidParams, "Bad args: " + e.Message);
        }

        double? gameTimeout = OptionalNumber(p, "timeoutGameSeconds");
        double? realTimeout = OptionalNumber(p, "timeoutRealSeconds");
        var run = new Run(request, method, arguments, Optional<string>(p, "result") == "handle");
        run.Context = new RunContext(
            GameClock.GameSeconds + gameTimeout,
            GameClock.RealSeconds + realTimeout,
            request.Connection.Handles,
            context => OnAborted(run)
        );

        lock (gate)
            runs[run.Key] = run;

        if (point == TickPoint.Update)
            Begin(run);
        else
            Scheduler.Schedule(point.Value, () => Begin(run));
    }

    public void Cancel(Request request)
    {
        long id = Required<long>(request.Params, "id");
        Run run;
        lock (gate)
            runs.TryGetValue(new RunKey(request.Connection.Id, id), out run);

        run?.Context.Abort(AbortReason.Cancelled);
        request.Connection.SendResult(request.Id, new JsonObject());
    }

    public void Release(Request request)
    {
        if (request.Params.TryGetValue("handles", out object value) && value is List<object> ids)
        {
            foreach (object id in ids)
            {
                if (id is long handle)
                    request.Connection.Handles.Release(handle);
            }
        }

        request.Connection.SendResult(request.Id, new JsonObject());
    }

    /// <summary>Times out runs whose deadline passed; call once a frame.</summary>
    public void CheckDeadlines()
    {
        foreach (Run run in Snapshot(null))
            run.Context.CheckAborted();
    }

    /// <summary>Stops the runs of a client that went away; nothing is sent to it.</summary>
    public void Abandon(Connection connection)
    {
        foreach (Run run in Snapshot(connection))
            run.Context.Abort(AbortReason.Disconnected);
    }

    private void Begin(Run run)
    {
        if (run.Context.IsAborted)
            return;

        object returned;
        RunContext previous = RunContext.Current;
        RunContext.SetCurrent(run.Context);
        try
        {
            returned = run.Method.Invoke(null, new object[] { run.Arguments });
        }
        catch (TargetInvocationException e)
        {
            Fail(run, e.InnerException ?? e);
            return;
        }
        finally
        {
            RunContext.SetCurrent(previous);
        }

        if (returned is Task task)
        {
            task.ContinueWith(
                _ => OnTaskDone(run, task),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }
        else
        {
            Succeed(run, returned);
        }
    }

    private void OnTaskDone(Run run, Task task)
    {
        // Results are encoded on the main thread, where Unity objects can be inspected
        if (!Scheduler.IsMainThread)
        {
            Scheduler.Post(() => OnTaskDone(run, task));
            return;
        }

        if (task.IsFaulted)
            Fail(run, Unwrap(task.Exception));
        else if (task.IsCanceled)
            Fail(run, new TaskCanceledException(task));
        else
            Succeed(run, task is Task<object> withValue ? withValue.Result : null);
    }

    private void Succeed(Run run, object value)
    {
        if (!Finish(run))
            return;

        object encoded;
        try
        {
            HandleRegistry handles = run.Request.Connection.Handles;
            encoded = run.AsHandle ? ValueEncoder.EncodeHandle(value, handles) : ValueEncoder.Encode(value, handles);
        }
        catch (Exception e)
        {
            Reply(run, ErrorCodes.SnippetThrew, "Encoding the result failed: " + e.Message, ExceptionData(e));
            return;
        }

        run.Request.Connection.SendResult(run.Request.Id, new JsonObject { { "value", encoded } });
    }

    private void Fail(Run run, Exception error)
    {
        if (!Finish(run))
            return;

        Reply(run, ErrorCodes.SnippetThrew, $"{error.GetType().Name}: {error.Message}", ExceptionData(error));
    }

    private void OnAborted(Run run)
    {
        if (!Finish(run))
            return;

        switch (run.Context.Reason)
        {
            case AbortReason.Cancelled:
                Reply(run, ErrorCodes.Cancelled, "The run was cancelled", null);
                break;
            case AbortReason.TimedOutGame:
                Reply(run, ErrorCodes.TimedOut, "The run timed out (game time)", new JsonObject { { "kind", "game" } });
                break;
            case AbortReason.TimedOutReal:
                Reply(run, ErrorCodes.TimedOut, "The run timed out (real time)", new JsonObject { { "kind", "real" } });
                break;
        }
    }

    // Exactly one reply per run, whichever of completion and abort gets here first
    private bool Finish(Run run)
    {
        lock (gate)
        {
            if (run.Finished)
                return false;

            run.Finished = true;
            runs.Remove(run.Key);
            return true;
        }
    }

    private static void Reply(Run run, int code, string message, object data)
    {
        run.Request.Connection.SendError(run.Request.Id, code, message, data);
    }

    private List<Run> Snapshot(Connection connection)
    {
        lock (gate)
        {
            var list = new List<Run>(runs.Count);
            foreach (Run run in runs.Values)
            {
                if (connection == null || run.Request.Connection == connection)
                    list.Add(run);
            }

            return list;
        }
    }

    public static JsonObject ExceptionData(Exception error) =>
        new JsonObject
        {
            { "type", error.GetType().FullName },
            { "message", error.Message },
            { "stack", error.StackTrace ?? "" },
            { "inner", error.InnerException != null ? ExceptionData(error.InnerException) : null },
        };

    private static Exception Unwrap(Exception error)
    {
        while (true)
        {
            if (error is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
                error = aggregate.InnerExceptions[0];
            else if (error is TargetInvocationException invocation && invocation.InnerException != null)
                error = invocation.InnerException;
            else
                return error;
        }
    }

    private static TickPoint? ParsePoint(string at) =>
        at switch
        {
            "Now" or "Update" => TickPoint.Update,
            "FixedUpdate" => TickPoint.FixedUpdate,
            "LateUpdate" => TickPoint.LateUpdate,
            "EndOfFrame" => TickPoint.EndOfFrame,
            _ => null,
        };

    private static T Required<T>(Dictionary<string, object> parameters, string name)
    {
        if (parameters.TryGetValue(name, out object value) && value is T typed)
            return typed;

        throw new RpcException(ErrorCodes.InvalidParams, $"\"{name}\" is required and must be a {typeof(T).Name}");
    }

    private static T Optional<T>(Dictionary<string, object> parameters, string name)
        where T : class
    {
        parameters.TryGetValue(name, out object value);
        return value as T;
    }

    private static double? OptionalNumber(Dictionary<string, object> parameters, string name)
    {
        parameters.TryGetValue(name, out object value);
        return value switch
        {
            null => null,
            long whole => whole,
            double real => real,
            _ => throw new RpcException(ErrorCodes.InvalidParams, $"\"{name}\" must be a number or null"),
        };
    }

    private sealed class Run
    {
        public Run(Request request, MethodInfo method, object[] arguments, bool asHandle)
        {
            Request = request;
            Method = method;
            Arguments = arguments;
            AsHandle = asHandle;
            Key = new RunKey(request.Connection.Id, request.Id);
        }

        public Request Request { get; }

        public MethodInfo Method { get; }

        public object[] Arguments { get; }

        public bool AsHandle { get; }

        public RunKey Key { get; }

        public RunContext Context { get; set; }

        public bool Finished { get; set; }
    }

    private readonly struct RunKey : IEquatable<RunKey>
    {
        private readonly int connection;
        private readonly long id;

        public RunKey(int connection, long id)
        {
            this.connection = connection;
            this.id = id;
        }

        public bool Equals(RunKey other) => connection == other.connection && id == other.id;

        public override bool Equals(object obj) => obj is RunKey other && Equals(other);

        public override int GetHashCode() => (connection * 397) ^ id.GetHashCode();
    }
}
