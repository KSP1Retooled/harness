using System.Text.Json.Nodes;

namespace KspHarness;

/// <summary>An error reply from the game that has no more specific exception.</summary>
public class GameRpcException : Exception
{
    public GameRpcException(int code, string message, JsonNode? data)
        : base($"{message} (code {code})")
    {
        Code = code;
        Data = data;
    }

    protected GameRpcException(int code, string message, JsonNode? data, Exception? inner)
        : base(message, inner)
    {
        Code = code;
        Data = data;
    }

    public int Code { get; }

    public new JsonNode? Data { get; }
}

/// <summary>A snippet threw in the game.</summary>
public sealed class SnippetException : GameRpcException
{
    internal SnippetException(JsonNode? data)
        : base(
            RpcErrorCodes.SnippetThrew,
            Describe(data),
            data,
            data?["inner"] is JsonObject inner ? new SnippetException(inner) : null)
    {
        GameType = data?["type"]?.GetValue<string>() ?? "System.Exception";
        GameMessage = data?["message"]?.GetValue<string>() ?? "";
        GameStackTrace = data?["stack"]?.GetValue<string>() ?? "";
    }

    /// <summary>The full name of the exception's type in the game.</summary>
    public string GameType { get; }

    public string GameMessage { get; }

    /// <summary>The stack trace in the game; snippet frames point at the test's source lines where Mono can.</summary>
    public string GameStackTrace { get; }

    public override string StackTrace => GameStackTrace + Environment.NewLine + "--- in the driver ---"
        + Environment.NewLine + base.StackTrace;

    private static string Describe(JsonNode? data)
    {
        var type = data?["type"]?.GetValue<string>() ?? "System.Exception";
        var message = data?["message"]?.GetValue<string>() ?? "";
        return $"{type} in the game: {message}";
    }
}

/// <summary>The connection to the game closed while requests were pending, or before a request could be sent.</summary>
public sealed class GameConnectionLostException : IOException
{
    public GameConnectionLostException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>The game process exited before it accepted the driver's connection.</summary>
public sealed class GameExitedException : Exception
{
    public GameExitedException(string message)
        : base(message)
    {
    }
}

internal static class RpcErrorCodes
{
    public const int SnippetThrew = -32000;
    public const int UnknownHash = -32001;
    public const int NotAuthenticated = -32002;
    public const int Cancelled = -32003;
    public const int TimedOut = -32004;
}
