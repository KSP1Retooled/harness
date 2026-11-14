using System.ComponentModel;
using System.Text.Json.Nodes;
using KspHarness.Installs;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KspHarness.Cli.Mcp;

/// <summary>The MCP tools: each maps onto the driver library or a short snippet of the in-game script API.</summary>
[McpServerToolType]
internal sealed class HarnessTools(GameState state)
{
    private const string SnippetHelp = """
        The code is the body of an async C# method that runs on the game's main thread, compiled against the game's own
        assemblies (Assembly-CSharp, UnityEngine, mods in GameData) with private members accessible. Usings already
        in scope: System, System.Collections.Generic, System.Linq, System.Threading.Tasks, UnityEngine,
        KSP.UI.Screens, KspHarness.Script; add others with leading `using X;` lines.
        A bare expression with no `;` is returned (`FlightGlobals.ActiveVessel.altitude`). Otherwise `return` a value,
        or return nothing to get null. `await` works: `await Tick.FixedUpdate;` (also Tick.Update, Tick.LateUpdate,
        Tick.EndOfFrame), `await Wait.GameSeconds(5);`, `await Wait.Until(() => cond);`,
        `await Wait.Scene(GameScenes.FLIGHT);`, `await Ui.ClickText("Launch");`, `await Dialogs.Click("OK");`.
        Code between two awaits runs within one frame. Each call compiles a new method, so locals do not survive from
        one call to the next, and neither do static fields of types you declare. Keep state in the game's own objects.
        The result is JSON: numbers, strings, booleans, arrays; enums as {"$":"enum","name":...}; vectors as
        {"$":"vec","v":[x,y,z]}; tuples, anonymous types and dictionaries as {"$":"obj","fields":{...}}; other objects
        (Vessel, Part, GameObject) as {"$":"handle","type":...,"text":ToString()}. Return the fields you need, e.g.
        `new { v.vesselName, v.altitude, situation = v.situation.ToString() }`, not the object itself.
        """;

    [
        McpServerTool(Name = "instance_build"),
        Description(
            """
                Builds a KSP instance, or reuses one built earlier with the same settings. An instance is a copy of a
                starting install (a stock KSP 1.12.5 or KSP Retooled folder, which is never modified), with its large files
                hard-linked and the CKAN modules installed. Returns the instance folder to pass to game_launch. Reusing an
                instance takes seconds; a first build with CKAN modules takes minutes.
                """
        )
    ]
    public Task<CallToolResult> InstanceBuild(
        [Description("The starting install folder.")] string start,
        [Description("Instance name (folder prefix). Default \"Plain\".")] string? name = null,
        [Description("CKAN identifiers, each \"Identifier\" or \"Identifier=version\".")] string[]? ckan = null,
        CancellationToken cancellationToken = default
    )
    {
        return Guard(async () =>
        {
            var log = new List<string>();
            var instance = await InstanceCommands.BuildAsync(
                start,
                name,
                ckan ?? [],
                HarnessHome.FromEnvironment(),
                line => log.Add(line),
                cancellationToken
            );
            var result = new JsonObject
            {
                ["instance"] = instance.Root,
                ["start"] = instance.Record.StartKind,
                ["ckan"] = new JsonArray(
                    instance.Record.Ckan.Select(m => (JsonNode)$"{m.Identifier} {m.Version}").ToArray()
                ),
                ["log"] = new JsonArray(log.TakeLast(20).Select(l => (JsonNode)l).ToArray()),
            };
            return Text(result.ToJsonString(SnippetRunner.JsonOptions));
        });
    }

    [
        McpServerTool(Name = "game_launch"),
        Description(
            """
                Launches KSP in a built instance (from instance_build) with the harness mod, and attaches to it. By default it
                waits for the main menu, which takes one to several minutes. The game keeps running after this server stops;
                call game_quit to stop it. If a game is already running in the instance, this fails: use game_attach instead.
                """
        )
    ]
    public Task<CallToolResult> GameLaunch(
        [Description("The instance folder.")] string instance,
        [Description("Run in a 1280x720 window instead of full screen.")] bool windowed = true,
        [Description("Wait for the settled main menu before returning.")] bool waitMenu = true,
        CancellationToken cancellationToken = default
    )
    {
        return Guard(async () =>
        {
            var request = new LaunchRequest
            {
                InstanceDir = instance,
                Windowed = windowed,
                Width = windowed ? 1280 : null,
                Height = windowed ? 720 : null,
                WaitForMenu = waitMenu,
            };

            SessionFile session;
            GameSession game;
            try
            {
                (session, game) = await GameControl.LaunchAsync(request, cancellationToken: cancellationToken);
            }
            catch (GameNotAtMenuException e)
            {
                throw new ToolException(e.Message + " (game_attach, then screenshot or dialogs_list, or game_quit)");
            }

            await state.SetAsync(session.Instance, game);
            await GameControl.CatchUpLogAsync(game, cancellationToken);
            return Text((await StatusAsync(game, session, cancellationToken)).ToJsonString(SnippetRunner.JsonOptions));
        });
    }

    [
        McpServerTool(Name = "game_attach"),
        Description(
            """
                Attaches to the game that `ksph launch` or game_launch started in an instance, using
                <instance>/.harness/session.json. The mod serves one client at a time, so attaching here disconnects a ksph
                console that is attached to the same game.
                """
        )
    ]
    public Task<CallToolResult> GameAttach(
        [Description("The instance folder.")] string instance,
        CancellationToken cancellationToken = default
    )
    {
        return Guard(async () =>
        {
            var game = await GameControl.AttachAsync(instance, cancellationToken);
            await state.SetAsync(game.InstanceDir, game);
            await GameControl.CatchUpLogAsync(game, cancellationToken);
            var status = await StatusAsync(game, SessionFile.Read(game.InstanceDir), cancellationToken);
            return Text(status.ToJsonString(SnippetRunner.JsonOptions));
        });
    }

    [
        McpServerTool(Name = "game_status", ReadOnly = true),
        Description(
            """
                Whether a game is attached and running, its pid, its KSP version, whether it is Retooled or stock, the loaded
                scene, and the log cursor. Pass the cursor to logs as `since` to get only the entries after it.
                """
        )
    ]
    public Task<CallToolResult> GameStatus(CancellationToken cancellationToken = default)
    {
        return Guard(async () =>
        {
            var game = state.Current;
            if (game is null)
            {
                return Text("""{ "attached": false }""");
            }

            var status = await StatusAsync(game, SessionFile.Read(game.InstanceDir), cancellationToken);
            return Text(status.ToJsonString(SnippetRunner.JsonOptions));
        });
    }

    [
        McpServerTool(Name = "game_quit", Destructive = true),
        Description(
            """
                Quits the attached game (or the one running in `instance`) and waits for it to exit. If it has not exited
                after a minute, it is killed.
                """
        )
    ]
    public Task<CallToolResult> GameQuit(
        [Description("The instance folder; default the attached game's.")] string? instance = null,
        CancellationToken cancellationToken = default
    )
    {
        return Guard(async () =>
        {
            var target =
                instance
                ?? state.InstanceDir
                ?? throw new ToolException("no game is attached; pass the instance folder");
            if (state.InstanceDir is { } attached && Path.GetFullPath(attached) == Path.GetFullPath(target))
            {
                await state.ClearAsync();
            }

            var quit = await GameControl.QuitAsync(target, cancellationToken: cancellationToken);
            return Text(quit ? "the game exited" : "no game was running in that instance");
        });
    }

    [
        McpServerTool(Name = "run_csharp"),
        Description("Runs C# in the attached game and returns the result as JSON.\n" + SnippetHelp)
    ]
    public Task<CallToolResult> RunCSharp(
        [Description("The snippet body, as described above.")] string code,
        [Description("Real-time limit in seconds (default 300).")] double timeoutSeconds = 300,
        CancellationToken cancellationToken = default
    )
    {
        return Guard(async () =>
        {
            var game = state.Require();
            var value = await SnippetRunner.RunAsync(
                game,
                code,
                timeout: TimeSpan.FromSeconds(timeoutSeconds),
                cancellationToken: cancellationToken
            );
            return Text(SnippetRunner.Format(value));
        });
    }

    [
        McpServerTool(Name = "screenshot", ReadOnly = true),
        Description(
            """
                Captures the next rendered frame (UI included) and returns it as a PNG image. It can also save the image to a
                file on this machine.
                """
        )
    ]
    public Task<CallToolResult> Screenshot(
        [Description("Where to save the PNG as well; omit to only return it.")] string? path = null,
        [Description(
            """
                1 captures the window as shown. Above 1, the game renders its cameras again that many times larger,
                without KSP's UI, and fails on loading screens.
                """
        )]
            int superSize = 1,
        CancellationToken cancellationToken = default
    )
    {
        return Guard(async () =>
        {
            var game = state.Require();
            var full = path is null ? null : Path.GetFullPath(path);
            var png = await game.ScreenshotAsync(full, superSize, cancellationToken);
            var result = new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png")] };
            if (full is not null)
            {
                result.Content.Add(new TextContentBlock { Text = $"saved to {full}" });
            }

            return result;
        });
    }

    [
        McpServerTool(Name = "ui_describe", ReadOnly = true),
        Description(
            """
                Describes the visible UI as a text tree: canvases, objects that show text (quoted) and controls (in brackets),
                plus the main menu's 3D buttons. Each line names a hierarchy path that ui_click accepts as `path`.
                """
        )
    ]
    public Task<CallToolResult> UiDescribe(
        [Description(
            "Only the subtrees at this hierarchy path (segments split on '/', * and ? wildcards, ** for any depth)."
        )]
            string? path = null,
        CancellationToken cancellationToken = default
    )
    {
        return Guard(async () =>
        {
            var game = state.Require();
            var source = path is null ? "return Ui.Describe();" : $"return Ui.Describe({ConsoleRepl.Literal(path)});";
            return Text(await game.Run<string>(source, cancellationToken: cancellationToken));
        });
    }

    [
        McpServerTool(Name = "ui_click"),
        Description(
            """
                Clicks a control as a mouse click would (uGUI buttons, toggles, and the main menu's 3D buttons such as
                "Start Game"), then waits one frame. Give either `text` (the one control that shows that text, ignoring case,
                wildcards allowed) or `path` (a hierarchy path from ui_describe). The result "clicked" means that the click was
                delivered, not that the game acted on it. For example, the main menu ignores its 3D buttons while a window such
                as What's New is open, so close such windows first (ui_click with path ".../ButtonClose"), and check the
                result with screenshot or ui_describe. A scene change takes several frames: follow up with run_csharp
                `await Wait.Scene(GameScenes.SPACECENTER);` or with game_status.
                """
        )
    ]
    public Task<CallToolResult> UiClick(
        [Description("The text the control shows.")] string? text = null,
        [Description("The control's hierarchy path.")] string? path = null,
        CancellationToken cancellationToken = default
    )
    {
        return Guard(async () =>
        {
            var game = state.Require();
            if ((text is null) == (path is null))
            {
                throw new ToolException("give either text or path");
            }

            var source = text is not null
                ? $"await Ui.ClickText({ConsoleRepl.Literal(text)});"
                : $$"""
                    var target = Ui.Find({{ConsoleRepl.Literal(path!)}});
                    if (target == null) throw new InvalidOperationException("nothing active at that path");
                    await Ui.Click(target);
                    """;
            await game.Run(source, cancellationToken: cancellationToken);
            return Text("clicked");
        });
    }

    [
        McpServerTool(Name = "dialogs_list", ReadOnly = true),
        Description("The open popup dialogs (PopupDialog): name, title, message and button captions, as JSON.")
    ]
    public Task<CallToolResult> DialogsList(CancellationToken cancellationToken = default)
    {
        return Guard(async () =>
        {
            var game = state.Require();
            return Text(
                SnippetRunner.Format(
                    await SnippetRunner.RunAsync(game, "Dialogs.Open", cancellationToken: cancellationToken)
                )
            );
        });
    }

    [
        McpServerTool(Name = "dialog_click"),
        Description(
            """
                Clicks a popup dialog's button by its caption (ignoring case, wildcards allowed). When more than one open
                dialog has such a button, also pass `dialog` (the dialog's name or title).
                """
        )
    ]
    public Task<CallToolResult> DialogClick(
        [Description("The button's caption.")] string button,
        [Description("The dialog's name or title.")] string? dialog = null,
        CancellationToken cancellationToken = default
    )
    {
        return Guard(async () =>
        {
            var game = state.Require();
            var source = dialog is null
                ? $"await Dialogs.Click({ConsoleRepl.Literal(button)});"
                : $"await Dialogs.Click({ConsoleRepl.Literal(dialog)}, {ConsoleRepl.Literal(button)});";
            await game.Run(source, cancellationToken: cancellationToken);
            return Text("clicked");
        });
    }

    [
        McpServerTool(Name = "db_query", ReadOnly = true),
        Description(
            """
                Config nodes from the game's live database (after ModuleManager has patched it), as cfg text. The query uses
                ModuleManager's node syntax: `PART[mk1pod_v2]`, `PART[mk1pod_v2]/MODULE[ModuleCommand]`,
                `PART:HAS[@MODULE[ModuleEngines*]]`, `RESOURCE_DEFINITION[LiquidFuel]`.
                """
        )
    ]
    public Task<CallToolResult> DbQuery(
        [Description("The node query.")] string query,
        CancellationToken cancellationToken = default
    )
    {
        return Guard(async () =>
        {
            var game = state.Require();
            var nodes = await game.Run<List<string>>(
                $"return Database.Query({ConsoleRepl.Literal(query)});",
                cancellationToken: cancellationToken
            );
            return Text(nodes.Count == 0 ? "no matching nodes" : string.Join("\n", nodes));
        });
    }

    [
        McpServerTool(Name = "db_values", ReadOnly = true),
        Description(
            """
                Values from the game's live config database as a JSON array of strings, e.g.
                `PART[mk1pod_v2]/MODULE[ModuleCommand]/minimumCrew` or `PART[*]/mass`.
                """
        )
    ]
    public Task<CallToolResult> DbValues(
        [Description("The value query: a node path ending in a value name.")] string query,
        CancellationToken cancellationToken = default
    )
    {
        return Guard(async () =>
        {
            var game = state.Require();
            var values = await game.Run<List<string>>(
                $"return Database.Values({ConsoleRepl.Literal(query)});",
                cancellationToken: cancellationToken
            );
            return Text(
                new JsonArray(values.Select(v => (JsonNode)v).ToArray()).ToJsonString(SnippetRunner.JsonOptions)
            );
        });
    }

    [
        McpServerTool(Name = "logs", ReadOnly = true),
        Description(
            """
                The game's log entries as JSON, with each entry's seq and the mods (name@version) whose code is in its stack.
                Returns { entries, next }. Without `since`, it returns the latest `limit` entries. With `since`, it returns the
                entries from that cursor on, oldest first. A cursor is the `next` of an earlier call, or game_status's
                logCursor.
                """
        )
    ]
    public Task<CallToolResult> Logs(
        [Description("Only entries with seq >= this cursor.")] long? since = null,
        [Description(
            "Comma-separated levels: Log, Warning, Error, Exception, Assert, or \"errors\" for the last three."
        )]
            string? levels = null,
        [Description("Only entries whose stack runs through a mod whose tag contains this text.")] string? mod = null,
        [Description("At most this many entries (default 100).")] int limit = 100,
        [Description("Include stack traces.")] bool stacks = false
    )
    {
        return Guard(() =>
        {
            var game = state.Current ?? throw new ToolException("no game is attached: call game_launch or game_attach");
            var levelList = (levels ?? "").Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );
            var cursor = game.Log.Cursor;
            var (page, next) = LogText.Page(cursor, game.Log.LogsSince(since ?? 0), since, levelList, mod, limit);

            var result = new JsonObject
            {
                ["entries"] = new JsonArray(page.Select(e => (JsonNode)LogText.Json(e, stacks)).ToArray()),
                ["next"] = next,
            };
            return Task.FromResult(Text(result.ToJsonString(SnippetRunner.JsonOptions)));
        });
    }

    private static async Task<JsonObject> StatusAsync(
        GameSession game,
        SessionFile? session,
        CancellationToken cancellationToken
    )
    {
        var status = new JsonObject
        {
            ["attached"] = true,
            ["connected"] = game.IsConnected,
            ["instance"] = game.InstanceDir,
            ["pid"] = game.Info.Pid,

            // Only the session file naming this game's pid speaks for it: a newer launch in the instance rewrites it.
            ["running"] = session is not null && session.Pid == game.Info.Pid && session.IsAlive(),
            ["ksp"] = game.Info.Ksp,
            ["retooled"] = game.Info.Retooled,
            ["scene"] = game.Info.Scene,
            ["logCursor"] = game.Log.Cursor,
            ["playerLog"] = session?.PlayerLog,
        };

        if (session is not null && session.Pid != game.Info.Pid && session.IsAlive())
        {
            status["playerLog"] = null;
            status["newerGame"] = new JsonObject
            {
                ["pid"] = session.Pid,
                ["hint"] =
                    "another game was launched in this instance since this one attached; game_attach attaches to it",
            };
        }

        if (game.IsConnected)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                status["scene"] = await game.Run<string>(
                    "return HighLogic.LoadedScene.ToString();",
                    cancellationToken: timeout.Token
                );
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                status["scene"] = "(the game did not answer within 20 s; it may be loading)";
            }
        }

        return status;
    }

    private static CallToolResult Text(string text)
    {
        return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
    }

    private static async Task<CallToolResult> Guard(Func<Task<CallToolResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception e)
        {
            var message = e is ToolException or InputCompileException ? e.Message : SnippetRunner.Describe(e);
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = message }] };
        }
    }
}
