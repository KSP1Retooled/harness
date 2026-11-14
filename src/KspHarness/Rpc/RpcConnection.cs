using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace KspHarness.Rpc;

/// <summary>
/// JSON-RPC 2.0 over a TCP stream, each message framed by a 4-byte little-endian length.
/// </summary>
internal sealed class RpcConnection : IAsyncDisposable
{
    // Snippet assemblies travel base64-encoded in one message; anything near this size is a broken stream.
    private const int MaxMessageBytes = 512 * 1024 * 1024;

    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> pending = new();
    private readonly CancellationTokenSource closing = new();
    private readonly Task readLoop;
    private long nextId;
    private Exception? closedReason;

    public RpcConnection(TcpClient client)
    {
        this.client = client;
        client.NoDelay = true;
        stream = client.GetStream();
        readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Raised on the reader thread for each notification, in order.</summary>
    public event Action<JsonObject>? Notification;

    /// <summary>Raised once when the connection closes, with the reason.</summary>
    public event Action<Exception>? Closed;

    public bool IsOpen => closedReason is null;

    public static async Task<RpcConnection> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, cancellationToken);
            return new RpcConnection(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Sends a request and returns its id once sent, with the task for its result.</summary>
    public async Task<(long Id, Task<JsonNode?> Result)> SendAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken = default
    )
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;

        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        };

        try
        {
            await WriteAsync(message, cancellationToken);
        }
        catch
        {
            pending.TryRemove(id, out _);
            throw;
        }

        // The reader may have closed between registering and sending; nothing would complete the request then.
        if (closedReason is { } reason)
        {
            completion.TrySetException(Lost(reason));
        }

        return (id, completion.Task);
    }

    /// <summary>Stops waiting for a request's reply; one that arrives later is dropped.</summary>
    public void Abandon(long id)
    {
        pending.TryRemove(id, out _);
    }

    public async Task<JsonNode?> RequestAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken = default
    )
    {
        var (_, result) = await SendAsync(method, parameters, cancellationToken);
        return await result.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        closing.Cancel();
        client.Dispose();
        try
        {
            await readLoop;
        }
        catch
        {
            // The loop records its own failure; disposal only waits for it to stop.
        }

        writeLock.Dispose();
        closing.Dispose();
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        if (closedReason is { } reason)
        {
            throw Lost(reason);
        }

        var payload = Encoding.UTF8.GetBytes(message.ToJsonString());
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);

        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(frame, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            throw Lost(e);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception reason;
        try
        {
            var header = new byte[4];
            while (true)
            {
                await stream.ReadExactlyAsync(header, closing.Token);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (length > MaxMessageBytes)
                {
                    throw new InvalidDataException(
                        $"the game sent a {length}-byte message, which is not a valid frame"
                    );
                }

                var payload = new byte[length];
                await stream.ReadExactlyAsync(payload, closing.Token);
                if (JsonNode.Parse(payload) is JsonObject message)
                {
                    Dispatch(message);
                }
            }
        }
        catch (EndOfStreamException)
        {
            reason = new EndOfStreamException("the game closed the connection");
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested)
        {
            reason = new ObjectDisposedException(nameof(RpcConnection), "the driver closed the connection");
        }
        catch (Exception e)
        {
            reason = e;
        }

        closedReason = reason;
        foreach (var id in pending.Keys)
        {
            if (pending.TryRemove(id, out var completion))
            {
                completion.TrySetException(Lost(reason));
            }
        }

        Closed?.Invoke(reason);
    }

    private void Dispatch(JsonObject message)
    {
        if (message["id"] is JsonValue idValue && (message.ContainsKey("result") || message.ContainsKey("error")))
        {
            if (!pending.TryRemove(idValue.GetValue<long>(), out var completion))
            {
                return;
            }

            if (message["error"] is JsonObject error)
            {
                completion.TrySetException(RpcErrors.ToException(error));
            }
            else
            {
                completion.TrySetResult(message["result"]?.DeepClone());
            }

            return;
        }

        if (message["method"]?.GetValue<string>() == "event" && message["params"] is JsonObject parameters)
        {
            Notification?.Invoke(parameters);
        }
    }

    private static GameConnectionLostException Lost(Exception reason)
    {
        if (reason is GameConnectionLostException lost)
        {
            return lost;
        }

        return new GameConnectionLostException($"the connection to the game closed: {reason.Message}", reason);
    }
}

internal static class RpcErrors
{
    public static Exception ToException(JsonObject error)
    {
        var code = error["code"]?.GetValue<int>() ?? 0;
        var message = error["message"]?.GetValue<string>() ?? "error";
        var data = error["data"]?.DeepClone();

        return code switch
        {
            RpcErrorCodes.SnippetThrew => new SnippetException(data),
            RpcErrorCodes.Cancelled => new OperationCanceledException("the game cancelled the snippet: " + message),
            RpcErrorCodes.TimedOut => new TimeoutException(
                $"the snippet ran out of {data?["kind"]?.GetValue<string>() ?? "its"} time in the game: {message}"
            ),
            _ => new GameRpcException(code, message, data),
        };
    }
}
