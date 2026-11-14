using System.Globalization;
using Microsoft.CodeAnalysis.CSharp;

namespace KspHarness.Cli;

/// <summary>What a Ctrl+C found to cancel in the console.</summary>
internal enum CancelOutcome
{
    NothingRunning,
    Cancelled,

    /// <summary>The run was cancelled already and has not stopped: the game may not be answering.</summary>
    AlreadyCancelled,
}

/// <summary>An interactive loop that runs each input as a snippet in the attached game and prints the result as JSON.</summary>
internal sealed class ConsoleRepl(GameSession game, TextReader input, TextWriter output, TextWriter errors)
{
    public const string Help = """
        Each input is a snippet body, run on the game's main thread; the result prints as JSON.
          FlightGlobals.Vessels.Count            a bare expression is returned
          var v = FlightGlobals.ActiveVessel;    statements run at a blank line or ;;
          return v.altitude;                     ... or as soon as they end with a return or throw
          await Tick.FixedUpdate;                awaits work (Tick, Wait, Ui, Dialogs, Screenshot)
          using KSP.UI.Screens.Flight;           leading using lines add namespaces
        Commands:
          :screenshot [path]   save a screenshot (default .harness/screenshots/<time>.png in the instance)
          :log [n]             the last n log lines (default 20; the mod keeps the last 5000)
          :errors              errors and exceptions since this console attached, with the mods in their stacks
          :ui [path]           describe the UI (or the objects under a hierarchy path)
          :click <text>        click the one control that shows this text
          :dialogs             open popup dialogs
          :db <query>          config nodes from the game database (PART[mk1pod_v2]/MODULE[ModuleCommand])
          :scene               the loaded scene
          :help                this text
          :quit                leave the console; the game keeps running (ksph quit stops it)
        Ctrl+C cancels a running snippet or command; a second Ctrl+C while it is still stopping ends the console
        (the game keeps running).
        """;

    private CancellationTokenSource? running;

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var attachedAt = game.Log.Cursor;
        output.WriteLine(
            $"attached to KSP {game.Info.Ksp}{(game.Info.Retooled ? " (Retooled)" : "")}, pid {game.Info.Pid}, scene {game.Info.Scene}. :help for help."
        );
        var reader = new ReplInput();
        while (!cancellationToken.IsCancellationRequested)
        {
            output.Write(reader.IsContinuing ? "... " : "ksp> ");
            var line = await input.ReadLineAsync(cancellationToken);
            ReplStep? step;
            try
            {
                step = line is null ? reader.End() : reader.Feed(line);
            }
            catch (Exception e)
            {
                errors.WriteLine(e.Message);
                reader.Clear();
                continue;
            }

            switch (step)
            {
                case ReplStep.Command { Name: "quit" or "q" or "exit" }:
                    return 0;
                case ReplStep.Command command:
                    await Guarded(token => RunCommandAsync(command, attachedAt, token), cancellationToken);
                    break;
                case ReplStep.Snippet snippet:
                    await Guarded(token => RunSnippetAsync(snippet.Code, token), cancellationToken);
                    break;
            }

            if (!game.IsConnected)
            {
                errors.WriteLine("the connection to the game closed");
                return 1;
            }

            if (line is null)
            {
                return 0;
            }
        }

        return 0;
    }

    /// <summary>
    /// Cancels the snippet or command that is running. Called from the Ctrl+C handler's thread, so it may race the
    /// end of the run.
    /// </summary>
    public CancelOutcome CancelRunning()
    {
        var current = Volatile.Read(ref running);
        if (current is null)
        {
            return CancelOutcome.NothingRunning;
        }

        try
        {
            if (current.IsCancellationRequested)
            {
                return CancelOutcome.AlreadyCancelled;
            }

            current.Cancel();
            return CancelOutcome.Cancelled;
        }
        catch (ObjectDisposedException)
        {
            return CancelOutcome.NothingRunning;
        }
    }

    private async Task RunSnippetAsync(string code, CancellationToken cancellationToken)
    {
        var value = await SnippetRunner.RunAsync(game, code, cancellationToken: cancellationToken);
        output.WriteLine(SnippetRunner.Format(value));
    }

    private async Task RunCommandAsync(ReplStep.Command command, long attachedAt, CancellationToken cancellationToken)
    {
        switch (command.Name)
        {
            case "help" or "h" or "?":
                output.WriteLine(Help);
                break;
            case "screenshot":
            {
                var path =
                    command.Argument.Length > 0
                        ? Path.GetFullPath(command.Argument)
                        : DefaultScreenshotPath(game.InstanceDir);
                await game.ScreenshotAsync(path, cancellationToken: cancellationToken);
                output.WriteLine(path);
                break;
            }

            case "log":
            {
                var count = 20;
                if (
                    command.Argument.Length > 0
                    && (!int.TryParse(command.Argument, CultureInfo.InvariantCulture, out count) || count < 1)
                )
                {
                    errors.WriteLine(":log takes a positive count");
                    break;
                }

                foreach (var entry in game.Log.LogsSince(0).TakeLast(count))
                {
                    output.WriteLine(LogText.Line(entry));
                }

                break;
            }

            case "errors":
            {
                var found = game.Log.ErrorsSince(attachedAt);
                foreach (var entry in found)
                {
                    output.WriteLine(LogText.Line(entry));
                    if (entry.Stack.Length > 0)
                    {
                        output.WriteLine(LogText.Indent(entry.Stack));
                    }
                }

                output.WriteLine($"{found.Count} error(s) since the console attached");
                break;
            }

            case "ui":
            {
                var source =
                    command.Argument.Length > 0
                        ? $"return Ui.Describe({Literal(command.Argument)});"
                        : "return Ui.Describe();";
                output.WriteLine(await game.Run<string>(source, cancellationToken: cancellationToken));
                break;
            }

            case "click" when command.Argument.Length > 0:
                await game.Run(
                    $"await Ui.ClickText({Literal(command.Argument)});",
                    cancellationToken: cancellationToken
                );
                output.WriteLine("clicked");
                break;
            case "dialogs":
            {
                var value = await SnippetRunner.RunAsync(game, "Dialogs.Open", cancellationToken: cancellationToken);
                output.WriteLine(SnippetRunner.Format(value));
                break;
            }

            case "db" when command.Argument.Length > 0:
            {
                var nodes = await game.Run<List<string>>(
                    $"return Database.Query({Literal(command.Argument)});",
                    cancellationToken: cancellationToken
                );
                output.WriteLine(nodes.Count == 0 ? "no matching nodes" : string.Join(Environment.NewLine, nodes));
                break;
            }

            case "scene":
            {
                var value = await SnippetRunner.RunAsync(
                    game,
                    "HighLogic.LoadedScene.ToString()",
                    cancellationToken: cancellationToken
                );
                output.WriteLine(SnippetRunner.Format(value));
                break;
            }

            case "click"
            or "db":
                errors.WriteLine($":{command.Name} needs an argument; :help lists the commands");
                break;
            default:
                errors.WriteLine($"unknown command :{command.Name}; :help lists the commands");
                break;
        }
    }

    /// <summary>Runs a snippet or command under its own cancellation, which <see cref="CancelRunning"/> triggers.</summary>
    private async Task Guarded(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Volatile.Write(ref running, cancel);
        try
        {
            await action(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            errors.WriteLine("cancelled");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            errors.WriteLine(SnippetRunner.Describe(e));
        }
        finally
        {
            Interlocked.Exchange(ref running, null);
        }
    }

    internal static string DefaultScreenshotPath(string instanceDir)
    {
        var name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".png";
        return Path.Combine(instanceDir, Installs.Instance.HarnessFolder, "screenshots", name);
    }

    internal static string Literal(string text) => SymbolDisplay.FormatLiteral(text, quote: true);
}
