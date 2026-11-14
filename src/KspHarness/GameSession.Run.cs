using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using KspHarness.Snippets;
using KspHarness.Values;

namespace KspHarness;

/// <remarks>
/// The overloads mirror <c>Task.Run</c> so that C# picks one from the lambda's shape: <c>async () =&gt; { return x; }</c>,
/// <c>() =&gt; expr</c>, <c>async () =&gt; { }</c> and <c>() =&gt; { }</c>, and the same with one dynamic parameter
/// (<c>ksp =&gt; ksp.FlightGlobals.ActiveVessel.altitude</c>) for code that names game types the test does not reference.
/// Captured variables are sent by value; assigning to them in the lambda does not change them in the test.
/// </remarks>
public sealed partial class GameSession
{
    // The mod enforces the real-time limit; the driver waits this much longer before it gives up on the reply.
    private static readonly TimeSpan ReplyGrace = TimeSpan.FromSeconds(30);

    // The mod handles cancel on the main thread, so a stuck frame never confirms it. After the caller cancels, the
    // driver waits this long for the run's own reply and then gives up on it.
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(5);

    /// <summary>Runs a method body that returns a value.</summary>
    public async Task<T> Run<T>(
        string source,
        RunOptions? options = null,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        options ??= RunOptions.Default;
        var snippet = SnippetBuilder.FromString(source, returnsValue: true, options.Usings, file, line);
        return Decode<T>(await RunAsync(snippet, options, keep: false, cancellationToken));
    }

    /// <summary>
    /// Runs a method body that returns a value and reads <paramref name="arguments"/> from <c>__args</c>, so that one
    /// compiled assembly serves every argument value (the library's reusable suites).
    /// </summary>
    internal async Task<T> RunWithArguments<T>(
        string source,
        IReadOnlyList<object?> arguments,
        RunOptions? options,
        CancellationToken cancellationToken,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        options ??= RunOptions.Default;
        var snippet = SnippetBuilder.FromString(source, returnsValue: true, options.Usings, file, line)
            with { Arguments = arguments };
        return Decode<T>(await RunAsync(snippet, options, keep: false, cancellationToken));
    }

    /// <summary>Runs a method body with no result.</summary>
    public async Task Run(
        string source,
        RunOptions? options = null,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        options ??= RunOptions.Default;
        var snippet = SnippetBuilder.FromString(source, returnsValue: false, options.Usings, file, line);
        await RunAsync(snippet, options, keep: false, cancellationToken);
    }

    public async Task<T> Run<T>(
        Func<T> lambda,
        RunOptions? options = null,
        CancellationToken cancellationToken = default,
        [CallerArgumentExpression(nameof(lambda))] string? text = null,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        var node = await RunLambdaAsync(lambda, text, dynamicParameter: false, options, keep: false, file, line, cancellationToken);
        return Decode<T>(node);
    }

    public async Task<T> Run<T>(
        Func<Task<T>> lambda,
        RunOptions? options = null,
        CancellationToken cancellationToken = default,
        [CallerArgumentExpression(nameof(lambda))] string? text = null,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        var node = await RunLambdaAsync(lambda, text, dynamicParameter: false, options, keep: false, file, line, cancellationToken);
        return Decode<T>(node);
    }

    public Task Run(
        Func<Task> lambda,
        RunOptions? options = null,
        CancellationToken cancellationToken = default,
        [CallerArgumentExpression(nameof(lambda))] string? text = null,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        return RunLambdaAsync(lambda, text, dynamicParameter: false, options, keep: false, file, line, cancellationToken);
    }

    public Task Run(
        Action lambda,
        RunOptions? options = null,
        CancellationToken cancellationToken = default,
        [CallerArgumentExpression(nameof(lambda))] string? text = null,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        return RunLambdaAsync(lambda, text, dynamicParameter: false, options, keep: false, file, line, cancellationToken);
    }

    /// <summary>
    /// Runs <c>ksp =&gt; expr</c>, where <c>ksp.X</c> stands for the game's <c>X</c>. The result comes back as
    /// primitives, arrays, an ExpandoObject or a <see cref="Handle"/>.
    /// </summary>
    public async Task<dynamic?> Run(
        Func<dynamic, dynamic> lambda,
        RunOptions? options = null,
        CancellationToken cancellationToken = default,
        [CallerArgumentExpression(nameof(lambda))] string? text = null,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        var node = await RunLambdaAsync(lambda, text, dynamicParameter: true, options, keep: false, file, line, cancellationToken);
        return GameValue.ToDynamic(node, this);
    }

    public async Task<dynamic?> Run(
        Func<dynamic, Task<dynamic>> lambda,
        RunOptions? options = null,
        CancellationToken cancellationToken = default,
        [CallerArgumentExpression(nameof(lambda))] string? text = null,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        var node = await RunLambdaAsync(lambda, text, dynamicParameter: true, options, keep: false, file, line, cancellationToken);
        return GameValue.ToDynamic(node, this);
    }

    public Task Run(
        Func<dynamic, Task> lambda,
        RunOptions? options = null,
        CancellationToken cancellationToken = default,
        [CallerArgumentExpression(nameof(lambda))] string? text = null,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        return RunLambdaAsync(lambda, text, dynamicParameter: true, options, keep: false, file, line, cancellationToken);
    }

    public Task Run(
        Action<dynamic> lambda,
        RunOptions? options = null,
        CancellationToken cancellationToken = default,
        [CallerArgumentExpression(nameof(lambda))] string? text = null,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        return RunLambdaAsync(lambda, text, dynamicParameter: true, options, keep: false, file, line, cancellationToken);
    }

    /// <summary>Runs a method body and keeps its result in the game, returning a handle to it.</summary>
    public async Task<Handle?> RunHandle(
        string source,
        RunOptions? options = null,
        CancellationToken cancellationToken = default,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        options ??= RunOptions.Default;
        var snippet = SnippetBuilder.FromString(source, returnsValue: true, options.Usings, file, line);
        return Decode<Handle?>(await RunAsync(snippet, options, keep: true, cancellationToken));
    }

    public async Task<Handle?> RunHandle<T>(
        Func<T> lambda,
        RunOptions? options = null,
        CancellationToken cancellationToken = default,
        [CallerArgumentExpression(nameof(lambda))] string? text = null,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        var node = await RunLambdaAsync(lambda, text, dynamicParameter: false, options, keep: true, file, line, cancellationToken);
        return Decode<Handle?>(node);
    }

    public async Task<Handle?> RunHandle<T>(
        Func<Task<T>> lambda,
        RunOptions? options = null,
        CancellationToken cancellationToken = default,
        [CallerArgumentExpression(nameof(lambda))] string? text = null,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        var node = await RunLambdaAsync(lambda, text, dynamicParameter: false, options, keep: true, file, line, cancellationToken);
        return Decode<Handle?>(node);
    }

    public async Task<Handle?> RunHandle(
        Func<dynamic, dynamic> lambda,
        RunOptions? options = null,
        CancellationToken cancellationToken = default,
        [CallerArgumentExpression(nameof(lambda))] string? text = null,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        var node = await RunLambdaAsync(lambda, text, dynamicParameter: true, options, keep: true, file, line, cancellationToken);
        return Decode<Handle?>(node);
    }

    public async Task<Handle?> RunHandle(
        Func<dynamic, Task<dynamic>> lambda,
        RunOptions? options = null,
        CancellationToken cancellationToken = default,
        [CallerArgumentExpression(nameof(lambda))] string? text = null,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        var node = await RunLambdaAsync(lambda, text, dynamicParameter: true, options, keep: true, file, line, cancellationToken);
        return Decode<Handle?>(node);
    }

    private T Decode<T>(JsonNode? node) => (T)GameValue.Convert(node, typeof(T), this)!;

    private Task<JsonNode?> RunLambdaAsync(
        Delegate lambda,
        string? text,
        bool dynamicParameter,
        RunOptions? options,
        bool keep,
        string file,
        int line,
        CancellationToken cancellationToken)
    {
        options ??= RunOptions.Default;
        var snippet = SnippetBuilder.FromLambda(
            lambda,
            text,
            dynamicParameter,
            options.Usings,
            file,
            line,
            Compiler.References.BaseCompilation);

        return RunAsync(snippet, options, keep, cancellationToken);
    }

    private async Task<JsonNode?> RunAsync(
        PreparedSnippet snippet,
        RunOptions options,
        bool keep,
        CancellationToken cancellationToken)
    {
        // Roslyn work is CPU-bound and slow the first time; keep it off the caller's context.
        var compiled = await Task.Run(() => Compiler.Compile(snippet.Source), cancellationToken);
        await EnsureLoadedAsync(compiled, cancellationToken);
        try
        {
            return await RunCompiledAsync(compiled, snippet.Arguments, options, keep, cancellationToken);
        }
        catch (GameRpcException e) when (e.Code == RpcErrorCodes.UnknownHash)
        {
            // The game forgot the assembly (a new session in the same process, say): send it again once.
            loaded.TryRemove(compiled.Hash, out _);
            await EnsureLoadedAsync(compiled, cancellationToken);
            return await RunCompiledAsync(compiled, snippet.Arguments, options, keep, cancellationToken);
        }
    }

    private Task EnsureLoadedAsync(CompiledSnippet compiled, CancellationToken cancellationToken)
    {
        var load = loaded.GetOrAdd(compiled.Hash, _ => LoadAsync(compiled));
        if (load.IsFaulted || load.IsCanceled)
        {
            loaded.TryRemove(new KeyValuePair<string, Task>(compiled.Hash, load));
        }

        return load.WaitAsync(cancellationToken);
    }

    private async Task LoadAsync(CompiledSnippet compiled)
    {
        var parameters = new JsonObject
        {
            ["hash"] = compiled.Hash,
            ["assembly"] = Convert.ToBase64String(compiled.Assembly),
            ["symbols"] = compiled.Symbols is null ? null : Convert.ToBase64String(compiled.Symbols),
        };

        try
        {
            await connection.RequestAsync("load", parameters).WaitAsync(options.RequestTimeout);
        }
        catch
        {
            loaded.TryRemove(compiled.Hash, out _);
            throw;
        }
    }

    private async Task<JsonNode?> RunCompiledAsync(
        CompiledSnippet compiled,
        IReadOnlyList<object?> arguments,
        RunOptions options,
        bool keep,
        CancellationToken cancellationToken)
    {
        var parameters = new JsonObject
        {
            ["hash"] = compiled.Hash,
            ["type"] = compiled.TypeName,
            ["method"] = compiled.MethodName,
            ["args"] = new JsonArray(arguments.Select(ValueEncoder.Encode).ToArray()),
            ["at"] = options.At.ToString(),
            ["timeoutGameSeconds"] = options.Timeout?.TotalSeconds,
            ["timeoutRealSeconds"] = options.RealTimeout?.TotalSeconds,
        };

        if (keep)
        {
            parameters["result"] = "handle";
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (id, reply) = await connection.SendAsync("run", parameters, cancellationToken);
        var limit = options.RealTimeout is { } realTimeout ? realTimeout + ReplyGrace : Timeout.InfiniteTimeSpan;
        try
        {
            try
            {
                return (await reply.WaitAsync(limit, cancellationToken))?["value"];
            }
            catch (TimeoutException) when (!reply.IsCompleted)
            {
                connection.Abandon(id);
                _ = CancelAsync(id);
                throw new TimeoutException($"the game did not answer within {limit}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !reply.IsCompleted)
            {
                _ = CancelAsync(id);
                try
                {
                    // A run that answers in time, cancelled or not, decides the outcome.
                    return (await reply.WaitAsync(CancelGrace))?["value"];
                }
                catch (TimeoutException) when (!reply.IsCompleted)
                {
                    connection.Abandon(id);
                    throw new OperationCanceledException(
                        $"cancelled; the game did not confirm the cancel within {CancelGrace}",
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException e) when (cancellationToken.IsCancellationRequested
            && e.CancellationToken != cancellationToken)
        {
            throw new OperationCanceledException(e.Message, e, cancellationToken);
        }
    }

    private async Task CancelAsync(long id)
    {
        try
        {
            await connection.RequestAsync("cancel", new JsonObject { ["id"] = id }).WaitAsync(options.RequestTimeout);
        }
        catch (Exception e) when (e is GameRpcException or GameConnectionLostException or TimeoutException)
        {
            // The run may have finished first, or the game is gone; either way the run's own reply decides.
        }
    }
}
