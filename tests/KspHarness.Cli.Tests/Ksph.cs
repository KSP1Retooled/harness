using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace KspHarness.Cli.Tests;

/// <summary>Runs the built <c>ksph</c> as a separate process, as a user or an agent would.</summary>
internal static class Ksph
{
    public static ProcessStartInfo StartInfo(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ksph.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return start;
    }

    /// <summary>Runs a command to its end; returns the exit code, standard output and standard error.</summary>
    public static async Task<(int Code, string Out, string Err)> RunAsync(TimeSpan timeout, params string[] args)
    {
        using var process = Process.Start(StartInfo(args))!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"ksph {string.Join(' ', args)} did not finish within {timeout}");
        }

        return (process.ExitCode, (await output).Trim(), (await errors).Trim());
    }
}

/// <summary>A minimal MCP client over a <c>ksph mcp</c> process: newline-delimited JSON-RPC on its stdio.</summary>
internal sealed class McpClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly StringBuilder errors = new();
    private int nextId;

    private McpClient(Process process)
    {
        this.process = process;
        process.ErrorDataReceived += (_, e) =>
        {
            lock (errors)
            {
                errors.AppendLine(e.Data);
            }
        };
        process.BeginErrorReadLine();
    }

    public string Errors
    {
        get
        {
            lock (errors)
            {
                return errors.ToString();
            }
        }
    }

    public static async Task<McpClient> StartAsync()
    {
        var client = new McpClient(Process.Start(Ksph.StartInfo("mcp"))!);
        await client.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "tests", ["version"] = "1" },
        });
        await client.NotifyAsync("notifications/initialized");
        return client;
    }

    public async Task<JsonNode> RequestAsync(string method, JsonObject? parameters = null, TimeSpan? timeout = null)
    {
        var id = ++nextId;
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        await process.StandardInput.WriteLineAsync(message.ToJsonString());
        await process.StandardInput.FlushAsync();

        using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancel.Token)
                ?? throw new InvalidOperationException($"ksph mcp closed its output; stderr:\n{Errors}");
            var reply = JsonNode.Parse(line)!;
            if (reply["id"]?.GetValue<int>() != id)
            {
                continue;
            }

            if (reply["error"] is { } error)
            {
                throw new InvalidOperationException($"{method} failed: {error.ToJsonString()}");
            }

            return reply["result"]!;
        }
    }

    public Task<JsonNode> CallAsync(string tool, JsonObject? arguments = null, TimeSpan? timeout = null)
    {
        return RequestAsync("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments ?? new JsonObject() }, timeout);
    }

    public async Task NotifyAsync(string method)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        await process.StandardInput.WriteLineAsync(message.ToJsonString());
        await process.StandardInput.FlushAsync();
    }

    public static string TextOf(JsonNode result)
    {
        return string.Join("\n", result["content"]!.AsArray()
            .Where(c => c?["type"]?.GetValue<string>() == "text")
            .Select(c => c!["text"]!.GetValue<string>()));
    }

    public async ValueTask DisposeAsync()
    {
        process.StandardInput.Close();
        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
        {
            process.Kill(entireProcessTree: true);
        }

        process.Dispose();
        await Task.CompletedTask;
    }
}
