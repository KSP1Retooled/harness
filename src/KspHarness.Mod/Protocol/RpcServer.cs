using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace KspHarness.Mod.Protocol;

/// <summary>
/// Listens on the loopback interface and authenticates clients. A client that passes <c>hello</c> replaces the
/// previous one, so a driver that lost its socket can reconnect without waiting for the old one to time out.
/// </summary>
internal sealed class RpcServer
{
    public const int Protocol = 1;

    private readonly int port;
    private readonly string token;
    private readonly Action<Request> onHello;
    private readonly Action<Request> onRequest;
    private readonly Action<Connection> onClosed;
    private readonly object gate = new object();
    private TcpListener listener;
    private Connection active;

    public RpcServer(
        int port,
        string token,
        Action<Request> onHello,
        Action<Request> onRequest,
        Action<Connection> onClosed)
    {
        this.port = port;
        this.token = token;
        this.onHello = onHello;
        this.onRequest = onRequest;
        this.onClosed = onClosed;
    }

    /// <summary>The authenticated client, or null.</summary>
    public Connection Active
    {
        get
        {
            lock (gate)
                return active;
        }
    }

    public void Start()
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        new Thread(AcceptLoop) { IsBackground = true, Name = "KspHarness listener" }.Start();
    }

    public void Stop()
    {
        try
        {
            listener?.Stop();
        }
        catch (Exception)
        {
            // Stopping during shutdown can race with the accept thread
        }

        Active?.Close();
    }

    /// <summary>Makes <paramref name="connection"/> the client that events go to, closing the previous one.</summary>
    public void Activate(Connection connection)
    {
        Connection previous;
        lock (gate)
        {
            previous = active;
            active = connection;
        }

        if (previous != null && previous != connection)
            previous.Close();
    }

    private void AcceptLoop()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (Exception)
            {
                return;
            }

            var connection = new Connection(client, OnMessage, OnClosed);
            connection.Start();
        }
    }

    private void OnClosed(Connection connection)
    {
        lock (gate)
        {
            if (active == connection)
                active = null;
        }

        onClosed(connection);
    }

    private void OnMessage(Connection connection, Dictionary<string, object> message)
    {
        message.TryGetValue("method", out object methodValue);
        bool hasId = message.TryGetValue("id", out object idValue) && idValue != null;
        string method = methodValue as string;

        // The driver sends no notifications; ignore any rather than answer them
        if (!hasId)
            return;

        if (!(idValue is long id))
        {
            connection.SendError(null, ErrorCodes.InvalidRequest, "Request ids must be integers");
            return;
        }

        if (method == null)
        {
            connection.SendError(id, ErrorCodes.InvalidRequest, "A request needs a method");
            return;
        }

        message.TryGetValue("params", out object paramsValue);
        var parameters = paramsValue as Dictionary<string, object> ?? new Dictionary<string, object>();
        var request = new Request(connection, id, method, parameters);

        if (connection.Authenticated)
        {
            onRequest(request);
            return;
        }

        if (method != "hello")
        {
            Refuse(connection, id, "The first request must be hello");
            return;
        }

        parameters.TryGetValue("token", out object tokenValue);
        if (!TokenMatches(tokenValue as string))
        {
            Debug.LogWarning("[KspHarness] Refused a client with a wrong token");
            Refuse(connection, id, "Wrong token");
            return;
        }

        parameters.TryGetValue("protocol", out object protocolValue);
        if (!(protocolValue is long protocol) || protocol != Protocol)
        {
            connection.SendError(id, ErrorCodes.InvalidParams, $"This mod speaks protocol {Protocol}");
            connection.CloseWhenFlushed();
            return;
        }

        connection.Authenticated = true;
        onHello(request);
    }

    private static void Refuse(Connection connection, long id, string message)
    {
        connection.SendError(id, ErrorCodes.NotAuthenticated, message);
        connection.CloseWhenFlushed();
    }

    private bool TokenMatches(string offered)
    {
        if (offered == null || offered.Length != token.Length)
            return false;

        // Constant time, so the reply time does not reveal how much of a guess was right
        int difference = 0;
        for (int i = 0; i < token.Length; i++)
            difference |= char.ToLowerInvariant(offered[i]) ^ char.ToLowerInvariant(token[i]);

        return difference == 0;
    }
}
