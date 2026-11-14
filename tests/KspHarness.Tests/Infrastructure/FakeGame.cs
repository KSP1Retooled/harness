using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace KspHarness.Tests.Infrastructure;

/// <summary>An error reply the fake game sends for a request.</summary>
internal sealed class FakeError(int code, string message, JsonNode? data = null) : Exception(message)
{
    public int Code { get; } = code;

    public JsonNode? ErrorData { get; } = data;
}

/// <summary>
/// A TCP server that speaks the driver mod's protocol, so that the client can be tested without a game. Runs are
/// answered by <see cref="OnRun"/>, each on its own task so that a cancel can arrive while a run is pending.
/// </summary>
internal sealed class FakeGame : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentQueue<JsonObject> requests = new();
    private Task? serveLoop;
    private NetworkStream? stream;
    private TcpClient? client;
    private long nextSeq;

    public FakeGame()
    {
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        serveLoop = Task.Run(ServeAsync);
    }

    public int Port { get; }

    public string Token { get; } = "0123456789abcdef";

    public IReadOnlyList<JsonObject> Requests => requests.ToArray();

    public ConcurrentDictionary<string, byte[]> Loaded { get; } = new();

    /// <summary>Events sent right after hello, as the mod sends what it buffered before the client connected.</summary>
    public List<JsonObject> BufferedEvents { get; } = [];

    /// <summary>Answers a run: the returned node is the result's <c>value</c>; a <see cref="FakeError"/> is an error.</summary>
    public Func<JsonObject, CancellationToken, Task<JsonNode?>> OnRun { get; set; } =
        (_, _) => Task.FromResult<JsonNode?>(null);

    /// <summary>When true, the game forgets loaded assemblies before the next run, as after a restart.</summary>
    public bool ForgetLoadedOnce { get; set; }

    /// <summary>When true, cancel requests get no reply and stop nothing, as when the game's main thread is stuck.</summary>
    public bool IgnoreCancel { get; set; }

    public ConcurrentDictionary<long, CancellationTokenSource> PendingRuns { get; } = new();

    public IEnumerable<string> Methods => Requests.Select(r => r["method"]!.GetValue<string>());

    public async Task SendEventAsync(string kind, JsonObject fields)
    {
        var parameters = new JsonObject
        {
            ["kind"] = kind,
            ["seq"] = Interlocked.Increment(ref nextSeq) - 1,
            ["realTime"] = 1.5,
            ["frame"] = 10,
            ["ut"] = null,
        };

        foreach (var (key, value) in fields.ToList())
        {
            fields.Remove(key);
            parameters[key] = value;
        }

        await WriteAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "event",
                ["params"] = parameters,
            }
        );
    }

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        listener.Stop();
        client?.Dispose();
        if (serveLoop is not null)
        {
            try
            {
                await serveLoop;
            }
            catch
            {
                // Stopping closes the socket under the loop.
            }
        }
    }

    private async Task ServeAsync()
    {
        client = await listener.AcceptTcpClientAsync(stopping.Token);
        stream = client.GetStream();
        var header = new byte[4];
        while (!stopping.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(header, stopping.Token);
            var payload = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header)];
            await stream.ReadExactlyAsync(payload, stopping.Token);
            var request = JsonNode.Parse(payload)!.AsObject();
            requests.Enqueue(request);
            await HandleAsync(request);
        }
    }

    private async Task HandleAsync(JsonObject request)
    {
        var id = request["id"]!.GetValue<long>();
        var parameters = request["params"] as JsonObject ?? [];
        switch (request["method"]!.GetValue<string>())
        {
            case "hello":
                if (parameters["token"]?.GetValue<string>() != Token)
                {
                    await ReplyErrorAsync(id, new FakeError(-32002, "not authenticated"));
                    client!.Dispose();
                    return;
                }

                await ReplyAsync(
                    id,
                    new JsonObject
                    {
                        ["protocol"] = 1,
                        ["pid"] = 4242,
                        ["ksp"] = "1.12.5",
                        ["retooled"] = false,
                        ["unity"] = "2019.4.18f1",
                        ["scene"] = "MAINMENU",
                        ["loaded"] = true,
                        ["logCursor"] = BufferedEvents.Count,
                    }
                );

                foreach (var buffered in BufferedEvents)
                {
                    await SendEventAsync(buffered["kind"]!.GetValue<string>(), buffered.DeepClone().AsObject());
                }

                return;
            case "load":
                Loaded[parameters["hash"]!.GetValue<string>()] = Convert.FromBase64String(
                    parameters["assembly"]!.GetValue<string>()
                );
                await ReplyAsync(id, new JsonObject());
                return;
            case "run":
                _ = Task.Run(() => RunAsync(id, parameters));
                return;
            case "cancel":
                if (IgnoreCancel)
                {
                    return;
                }

                if (PendingRuns.TryGetValue(parameters["id"]!.GetValue<long>(), out var run))
                {
                    run.Cancel();
                }

                await ReplyAsync(id, new JsonObject());
                return;
            default:
                await ReplyAsync(id, new JsonObject());
                return;
        }
    }

    private async Task RunAsync(long id, JsonObject parameters)
    {
        if (ForgetLoadedOnce)
        {
            ForgetLoadedOnce = false;
            Loaded.Clear();
        }

        if (!Loaded.ContainsKey(parameters["hash"]!.GetValue<string>()))
        {
            await ReplyErrorAsync(id, new FakeError(-32001, "unknown snippet"));
            return;
        }

        using var cancellation = new CancellationTokenSource();
        PendingRuns[id] = cancellation;
        try
        {
            var value = await OnRun(parameters, cancellation.Token);
            await ReplyAsync(id, new JsonObject { ["value"] = value });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await ReplyErrorAsync(id, new FakeError(-32003, "cancelled"));
        }
        catch (FakeError error)
        {
            await ReplyErrorAsync(id, error);
        }
        finally
        {
            PendingRuns.TryRemove(id, out _);
        }
    }

    private Task ReplyAsync(long id, JsonNode? result)
    {
        return WriteAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result,
            }
        );
    }

    private Task ReplyErrorAsync(long id, FakeError error)
    {
        return WriteAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JsonObject
                {
                    ["code"] = error.Code,
                    ["message"] = error.Message,
                    ["data"] = error.ErrorData,
                },
            }
        );
    }

    private async Task WriteAsync(JsonObject message)
    {
        var payload = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await writeLock.WaitAsync();
        try
        {
            await stream!.WriteAsync(header);
            await stream.WriteAsync(payload);
        }
        finally
        {
            writeLock.Release();
        }
    }
}
