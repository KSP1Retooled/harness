using System.Text.Json.Nodes;
using KspHarness.Cli.Mcp;
using KspHarness.Installs;

namespace KspHarness.Cli;

public static class Program
{
    public const string Usage = """
        ksph: build KSP instances and drive a running game through the KSP Harness mod.

          ksph instance build --start <dir> [--name N] [--ckan id[,id=version...]] [--home <dir>]
                                  build or reuse an instance; prints its folder
          ksph instance list [--home <dir>]
          ksph launch --instance <dir> [--windowed] [--width W] [--height H] [--wait-menu] [--timeout <s>]
                                  start the game and leave it running; prints pid, port and token as JSON
          ksph attach-info <instance>
          ksph quit <instance>
          ksph run --instance <dir> [file.csx | -e "code"] [--timeout <s>] [--raw]
                                  run a snippet body (or stdin) in the game; prints the result as JSON
                                  (--raw prints a string result as plain text)
          ksph console --instance <dir>
          ksph mcp                MCP server on stdin/stdout for agents

        The harness home (instances, caches) is --home, else KSPHARNESS_HOME. CKAN is CKAN_EXE, else on the PATH.
        Exit codes: 0 ok, 1 failed, 2 bad arguments.
        """;

    public static async Task<int> Main(string[] args)
    {
        using var cancel = new CancellationTokenSource();

        // The console handles Ctrl+C itself, to cancel one snippet rather than the session.
        if (args.Length == 0 || args[0] != "console")
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cancel.Cancel();
            };
        }

        try
        {
            return await RunAsync(args, Console.In, Console.Out, Console.Error, cancel.Token);
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine($"ksph: {e.Message}");
            Console.Error.WriteLine("run `ksph help` for usage");
            return 2;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Console.Error.WriteLine("cancelled");
            return 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"ksph: {SnippetRunner.Describe(e)}");
            return 1;
        }
    }

    internal static async Task<int> RunAsync(
        string[] args,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken
    )
    {
        var command = args.Length == 0 ? "help" : args[0];
        var rest = args.Skip(1).ToArray();
        switch (command)
        {
            case "help" or "--help" or "-h":
                stdout.WriteLine(Usage);
                return 0;
            case "instance":
                return await InstanceAsync(rest, stdout, stderr, cancellationToken);
            case "launch":
                return await LaunchAsync(rest, stdout, stderr, cancellationToken);
            case "attach-info":
            {
                var session = GameControl.RunningSession(InstanceArgument(CommandLine.Parse(rest, ["--instance"])));
                stdout.WriteLine(session.ToJson());
                return 0;
            }

            case "quit":
            {
                var instance = InstanceArgument(CommandLine.Parse(rest, ["--instance"]));
                var quit = await GameControl.QuitAsync(instance, stderr.WriteLine, cancellationToken);
                stderr.WriteLine(quit ? "the game exited" : "no game was running in that instance");
                return 0;
            }

            case "run":
                return await RunSnippetAsync(rest, stdin, stdout, stderr, cancellationToken);
            case "console":
                return await ConsoleAsync(rest, stdin, stdout, stderr, cancellationToken);
            case "mcp":
                CommandLine.Parse(rest).AtMost(0);
                return await McpServer.RunAsync(cancellationToken);
            default:
                throw new UsageException($"unknown command '{command}'");
        }
    }

    private static async Task<int> InstanceAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken
    )
    {
        var verb = args.Length == 0 ? "" : args[0];
        switch (verb)
        {
            case "build":
            {
                var line = CommandLine.Parse(args.Skip(1), ["--start", "--name", "--ckan", "--home"]).AtMost(0);
                var instance = await InstanceCommands.BuildAsync(
                    line.Required("--start"),
                    line.Value("--name"),
                    line.List("--ckan"),
                    Home(line),
                    stderr.WriteLine,
                    cancellationToken
                );
                stdout.WriteLine(instance.Root);
                return 0;
            }

            case "list":
            {
                var line = CommandLine.Parse(args.Skip(1), ["--home"]).AtMost(0);
                foreach (var instance in InstanceCommands.List(Home(line)))
                {
                    var session = SessionFile.Read(instance.Root, stderr.WriteLine);
                    var running = session is not null && session.IsAlive() ? $"  running pid {session.Pid}" : "";
                    var mods =
                        instance.Record.Ckan.Count == 0
                            ? "no CKAN modules"
                            : string.Join(", ", instance.Record.Ckan.Select(m => m.Identifier));
                    stdout.WriteLine($"{instance.Root}  {instance.Record.StartKind}  {mods}{running}");
                }

                return 0;
            }

            default:
                throw new UsageException("use `ksph instance build` or `ksph instance list`");
        }
    }

    private static async Task<int> LaunchAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken
    )
    {
        var line = CommandLine
            .Parse(args, ["--instance", "--width", "--height", "--timeout"], ["--windowed", "--wait-menu"])
            .AtMost(0);
        var windowed = line.Has("--windowed");
        var request = new LaunchRequest
        {
            InstanceDir = line.Required("--instance"),
            Windowed = windowed,
            Width = line.Int("--width") ?? (windowed ? 1280 : null),
            Height = line.Int("--height") ?? (windowed ? 720 : null),
            WaitForMenu = line.Has("--wait-menu"),
            BootTimeout = TimeSpan.FromSeconds(line.Int("--timeout") ?? 600),
        };

        try
        {
            var (session, game) = await GameControl.LaunchAsync(request, stderr.WriteLine, cancellationToken);
            await game.DisposeAsync();
            stdout.WriteLine(session.ToJson());
            return 0;
        }
        catch (GameNotAtMenuException e)
        {
            // The game stays up, so its session is printed as for a launch that succeeded, for attach and quit.
            stderr.WriteLine($"ksph: {e.Message}");
            stdout.WriteLine(e.Session.ToJson());
            return 1;
        }
    }

    private static async Task<int> RunSnippetAsync(
        string[] args,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken
    )
    {
        var line = CommandLine.Parse(args, ["--instance", "-e", "--timeout"], ["--raw"]).AtMost(1);
        var inline = line.Value("-e");
        var fromFile = line.Positionals.Count == 1 && line.Positionals[0] != "-";
        if (inline is not null && line.Positionals.Count > 0)
        {
            throw new UsageException("give a file or -e, not both");
        }

        var file = fromFile ? Path.GetFullPath(line.Positionals[0]) : null;
        var code =
            inline
            ?? (
                file is not null
                    ? await File.ReadAllTextAsync(file, cancellationToken)
                    : await stdin.ReadToEndAsync(cancellationToken)
            );
        var timeout = line.Int("--timeout") is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;

        await using var game = await GameControl.AttachAsync(line.Required("--instance"), cancellationToken);
        try
        {
            var value = await SnippetRunner.RunAsync(game, code, file, timeout, cancellationToken);
            var plain = line.Has("--raw") && value is JsonValue text && text.TryGetValue<string>(out var s) ? s : null;
            stdout.WriteLine(plain ?? SnippetRunner.Format(value));
            return 0;
        }
        catch (Exception e) when (e is SnippetCompileException or InputCompileException or SnippetException)
        {
            stderr.WriteLine(SnippetRunner.Describe(e));
            return 1;
        }
    }

    private static async Task<int> ConsoleAsync(
        string[] args,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken
    )
    {
        var instance = InstanceArgument(CommandLine.Parse(args, ["--instance"]));
        await using var game = await GameControl.AttachAsync(instance, cancellationToken);
        await GameControl.CatchUpLogAsync(game, cancellationToken);
        var repl = new ConsoleRepl(game, stdin, stdout, stderr);

        // Ctrl+C stops a running snippet or command; at the prompt it does nothing, so a slip does not end the
        // session. A second one while a cancelled run has not stopped (a game that does not answer) ends ksph.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = repl.CancelRunning() != CancelOutcome.AlreadyCancelled;
        };

        return await repl.RunAsync(cancellationToken);
    }

    private static HarnessHome Home(CommandLine line)
    {
        return line.Value("--home") is { } home ? new HarnessHome(home) : HarnessHome.FromEnvironment();
    }

    /// <summary>The instance folder from <c>--instance</c> or the one positional argument.</summary>
    internal static string InstanceArgument(CommandLine line)
    {
        var option = line.Value("--instance");
        if (option is not null)
        {
            line.AtMost(0);
            return option;
        }

        line.AtMost(1);
        return line.Positionals.Count == 1 ? line.Positionals[0] : throw new UsageException("name the instance folder");
    }
}
