using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using KspHarness.Mod.Values;

namespace KspHarness.Mod.Protocol;

internal sealed class Request
{
    public Request(Connection connection, long id, string method, Dictionary<string, object> parameters)
    {
        Connection = connection;
        Id = id;
        Method = method;
        Params = parameters;
    }

    public Connection Connection { get; }

    public long Id { get; }

    public string Method { get; }

    public Dictionary<string, object> Params { get; }
}

/// <summary>
/// One client socket: a reader thread that parses frames and a writer thread that sends them, so that neither the
/// main thread nor a logging thread ever blocks on the network.
/// </summary>
internal sealed class Connection
{
    // Assemblies with symbols are the largest messages; anything near this is a broken stream, not a snippet
    private const int MaxFrameBytes = 256 * 1024 * 1024;

    // Until hello succeeds, a connection may be anything that found the port, so it gets no large buffers and only a
    // short time to say hello
    private const int MaxHelloFrameBytes = 64 * 1024;
    private const int HelloTimeoutMilliseconds = 10_000;

    // A client that stops reading but keeps its socket open fills the TCP window; past these the connection is
    // dropped rather than queueing the game's log without end. A driver that reconnects gets the log replayed.
    private const int WriteTimeoutMilliseconds = 60_000;
    private const long MaxQueuedBytes = 256L * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private static int lastId;

    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly Action<Connection, Dictionary<string, object>> onMessage;
    private readonly Action<Connection> onClosed;
    private readonly Queue<byte[]> outgoing = new Queue<byte[]>();
    private readonly object gate = new object();
    private long queuedBytes;
    private bool closeWhenFlushed;
    private int closed;

    public Connection(
        TcpClient client,
        Action<Connection, Dictionary<string, object>> onMessage,
        Action<Connection> onClosed
    )
    {
        this.client = client;
        this.onMessage = onMessage;
        this.onClosed = onClosed;
        client.NoDelay = true;
        stream = client.GetStream();
        stream.ReadTimeout = HelloTimeoutMilliseconds;
        stream.WriteTimeout = WriteTimeoutMilliseconds;
        Id = Interlocked.Increment(ref lastId);
    }

    public int Id { get; }

    /// <summary>The handles this client holds; they go when it disconnects.</summary>
    public HandleRegistry Handles { get; } = new HandleRegistry();

    public bool IsOpen => Volatile.Read(ref closed) == 0;

    /// <summary>Set once <c>hello</c> succeeded; only then do requests other than <c>hello</c> run.</summary>
    public volatile bool Authenticated;

    public void Start()
    {
        new Thread(ReadLoop) { IsBackground = true, Name = $"KspHarness reader {Id}" }.Start();
        new Thread(WriteLoop) { IsBackground = true, Name = $"KspHarness writer {Id}" }.Start();
    }

    public void Send(object message)
    {
        if (!IsOpen)
            return;

        byte[] payload = Utf8.GetBytes(JsonWriter.Write(message));
        bool overflow;
        lock (gate)
        {
            if (closeWhenFlushed)
                return;

            overflow = queuedBytes + payload.Length > MaxQueuedBytes;
            if (!overflow)
            {
                outgoing.Enqueue(payload);
                queuedBytes += payload.Length;
                Monitor.PulseAll(gate);
            }
        }

        if (overflow)
            Close();
    }

    public void SendResult(long id, object result)
    {
        Send(
            new JsonObject
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "result", result ?? new JsonObject() },
            }
        );
    }

    public void SendError(long? id, int code, string message, object data = null)
    {
        var error = new JsonObject { { "code", code }, { "message", message } };
        if (data != null)
            error.Add("data", data);

        Send(
            new JsonObject
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "error", error },
            }
        );
    }

    public void SendNotification(string method, object parameters)
    {
        Send(
            new JsonObject
            {
                { "jsonrpc", "2.0" },
                { "method", method },
                { "params", parameters },
            }
        );
    }

    /// <summary>Sends what is queued and then closes, so an error reply reaches the client first.</summary>
    public void CloseWhenFlushed()
    {
        lock (gate)
        {
            closeWhenFlushed = true;
            Monitor.PulseAll(gate);
        }
    }

    /// <summary>Waits up to <paramref name="milliseconds"/> for the queued messages to be written.</summary>
    public void WaitForFlush(int milliseconds)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        lock (gate)
        {
            while (outgoing.Count > 0 && IsOpen)
            {
                int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0)
                    return;

                Monitor.Wait(gate, left);
            }
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0)
            return;

        lock (gate)
            Monitor.PulseAll(gate);

        try
        {
            client.Close();
        }
        catch (Exception)
        {
            // Closing a socket the peer already reset can throw; the connection is gone either way
        }

        onClosed(this);
    }

    private void ReadLoop()
    {
        var header = new byte[4];
        try
        {
            while (IsOpen)
            {
                if (!ReadExactly(header, 4))
                    break;

                int length = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
                int limit = Authenticated ? MaxFrameBytes : MaxHelloFrameBytes;
                if (length < 0 || length > limit)
                    throw new InvalidDataException($"frame length {length} is out of range");

                var payload = new byte[length];
                if (!ReadExactly(payload, length))
                    break;

                object message;
                try
                {
                    message = JsonReader.Parse(Utf8.GetString(payload));
                }
                catch (FormatException e)
                {
                    SendError(null, ErrorCodes.ParseError, e.Message);
                    continue;
                }

                if (message is Dictionary<string, object> request)
                    onMessage(this, request);
                else
                    SendError(null, ErrorCodes.InvalidRequest, "A message must be a JSON object");

                // hello runs on this thread, so from the next read on an authenticated client may stay idle for hours
                if (Authenticated && stream.ReadTimeout != Timeout.Infinite)
                    stream.ReadTimeout = Timeout.Infinite;
            }
        }
        catch (Exception)
        {
            // A reset or malformed stream ends the connection; the client reconnects if it wants to
        }
        finally
        {
            Close();
        }
    }

    private bool ReadExactly(byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
                return false;

            offset += read;
        }

        return true;
    }

    private void WriteLoop()
    {
        var header = new byte[4];
        try
        {
            while (true)
            {
                byte[] payload;
                lock (gate)
                {
                    while (outgoing.Count == 0 && IsOpen && !closeWhenFlushed)
                        Monitor.Wait(gate);

                    if (!IsOpen)
                        return;

                    if (outgoing.Count == 0)
                        break;

                    payload = outgoing.Peek();
                }

                int length = payload.Length;
                header[0] = (byte)length;
                header[1] = (byte)(length >> 8);
                header[2] = (byte)(length >> 16);
                header[3] = (byte)(length >> 24);
                stream.Write(header, 0, 4);
                stream.Write(payload, 0, length);

                lock (gate)
                {
                    queuedBytes -= outgoing.Dequeue().Length;
                    Monitor.PulseAll(gate);
                }
            }

            stream.Flush();
        }
        catch (Exception)
        {
            // The reader sees the same failure and reports the close
        }

        Close();
    }
}
