# ksph

The KSP Harness command line: builds instances, launches the game with the driver mod and leaves it running, and
drives it from a shell, an interactive console or an MCP client.

```sh
dotnet pack src/KspHarness.Cli -c Release -o artifacts   # with KSP_DIR set, so the driver mod ships inside
dotnet tool install -g KspHarness.Cli --add-source artifacts

export KSPHARNESS_HOME=/path/to/harness-home   # instances and caches, on the same volume as the starting install
inst=$(ksph instance build --start /path/to/ksp-1.12.5 --ckan ModuleManager)
ksph launch --instance "$inst" --windowed --wait-menu    # prints pid, port and token; the game keeps running
ksph run --instance "$inst" -e "FlightGlobals.Vessels.Count"
ksph run --instance "$inst" check.csx                     # a snippet body in a file; leading `using` lines work
ksph console --instance "$inst"                           # :help lists the commands
ksph quit "$inst"
```

- A snippet is the body of an async method that runs on the game's main thread. A bare expression is returned;
  otherwise `return` a value, or get `null`. `await Tick.FixedUpdate;`, `Wait`, `Ui`, `Dialogs` and `Screenshot` work.
- Results print in the harness's JSON value encoding. Return the fields you need rather than a game object, because a
  game object comes back as a handle.
- `launch` writes `<instance>/.harness/session.json`, and `run`, `console`, `quit` and `attach-info` read it. The mod
  serves one client at a time, so a new `run` or `console` takes the connection from the previous one.
- The player log goes to `<instance>/.harness/player.log`.

## MCP

`ksph mcp` serves the tools on standard input and output. For Claude Code:

```sh
claude mcp add ksp -e KSPHARNESS_HOME=/path/to/harness-home -- ksph mcp
```

An agent calls `instance_build`, then `game_launch` (or `game_attach` for a game started with `ksph launch`). It then
uses `run_csharp`, `screenshot`, `ui_describe`, `ui_click`, `dialogs_list`, `dialog_click`, `db_query`, `db_values`
and `logs`, and calls `game_quit` at the end. A game that the server launched keeps running when the server stops.
