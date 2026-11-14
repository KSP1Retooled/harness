using System;

namespace KspHarness.Mod.Protocol;

internal static class ErrorCodes
{
    public const int SnippetThrew = -32000;
    public const int UnknownSnippet = -32001;
    public const int NotAuthenticated = -32002;
    public const int Cancelled = -32003;
    public const int TimedOut = -32004;
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

/// <summary>An error reply to a request, thrown by a request handler.</summary>
internal sealed class RpcException : Exception
{
    public RpcException(int code, string message, object data = null)
        : base(message)
    {
        Code = code;
        Data = data;
    }

    public int Code { get; }

    public new object Data { get; }
}
