# KSP Harness

Builds KSP 1.12.5 installs with mods (stock or KSP Retooled), launches the game with a small driver mod, and runs C#
in it from tests, a shell, an interactive console or an MCP client. A check that you made once by hand can then run
again unattended as a regression test.

| Folder | What it is |
|---|---|
| `src/KspHarness.Mod` | The driver mod that runs inside the game (net472). |
| `src/KspHarness` | The driver library: sessions, snippets, library steps, the suite runner. |
| `src/KspHarness.Installs` | Install definitions and the instance builder (CKAN, archives, hard links). |
| `src/KspHarness.ConfigNodes` | KSP config file parser, writer and ModuleManager-style queries. |
| `src/KspHarness.Cli` | `ksph`, the command line and MCP server. |
| `tests/` | Offline tests, and game tests that boot real installs. |

The mod tests live in a separate repository, [ksp1retooled-modtests](../ksp1retooled-modtests), which is checked out
next to this one.

## What you need

- The .NET 10 SDK.
- A stock KSP 1.12.5 install for the build to reference. The build only reads it.
- One or more **starting installs**: KSP folders that instances are copied from. They are only ever read. A stock
  1.12.5 install works, and so does a KSP Retooled install.
- A **harness home**: a folder for instances, caches and run results. Put it on the same volume as the starting
  installs, so that instances can hard-link the game's large files instead of copying them.
- [CKAN](https://github.com/KSP-CKAN/CKAN) 1.36 or later, for definitions that install mods.

Set these environment variables (the values are examples):

```sh
export KSP_DIR=/path/to/stock-1.12.5          # build references
export KSPHARNESS_HOME=/path/to/harness-home  # instances, caches, run results
export CKAN_EXE=/path/to/ckan.exe             # otherwise ckan.exe (Windows) or ckan on the PATH
```

The driver runs the game build for the machine it is on: `KSP_x64.exe` on Windows, `KSP.x86_64` on Linux and
`KSP.app` on macOS, so the starting installs must be that platform's build. On Linux and macOS a `ckan.exe` runs under
`mono` from the PATH.

## Build

```sh
dotnet build KspHarness.slnx
```

The build compiles the driver mod against `KSP_DIR`'s assemblies and copies it next to the library, under
`mod/GameData/KspHarness`. Launching a game deploys it into the instance. Without `KSP_DIR` the mod project fails.

Format the code with [CSharpier](https://csharpier.com) before committing; CI checks it:

```sh
dotnet tool restore
dotnet csharpier format .
```

## Offline tests

```sh
dotnet test KspHarness.slnx
```

These run without starting a game. Tests that compile snippets against a real install use `KSP_DIR` for stock and
`KSPHARNESS_RETOOLED_DIR` for KSP Retooled, and skip when the variable is not set.

## Game tests

`tests/KspHarness.GameTests` builds an instance from each starting install, boots it in a window and runs snippets in
it. They skip unless the starting installs are named:

```sh
export KSPHARNESS_TEST_STOCK=/path/to/stock-1.12.5
export KSPHARNESS_TEST_RETOOLED=/path/to/retooled     # needs CKAN_EXE
dotnet test tests/KspHarness.GameTests --logger "console;verbosity=detailed"
```

Each install boots once, which takes one to a few minutes, and the two games run one after the other.

## ksph

Run the command line from the source tree, or install it as a dotnet tool:

```sh
dotnet run --project src/KspHarness.Cli -- help
dotnet pack src/KspHarness.Cli -c Release -o artifacts   # needs KSP_DIR, so that the mod ships inside
dotnet tool install -g KspHarness.Cli --add-source artifacts
```

```sh
inst=$(ksph instance build --start /path/to/stock-1.12.5 --ckan ModuleManager)
ksph launch --instance "$inst" --windowed --wait-menu    # the game keeps running after ksph exits
ksph run --instance "$inst" -e "FlightGlobals.Vessels.Count"
ksph console --instance "$inst"                           # :help lists the commands
ksph quit "$inst"
```

[src/KspHarness.Cli/README.md](src/KspHarness.Cli/README.md) explains snippets and the session file.

## MCP server

`ksph mcp` serves tools over standard input and output, so that an agent can build instances, launch and attach to
games, run C#, take screenshots, click the UI, answer dialogs, query the config database and read the log. For
Claude Code:

```sh
claude mcp add ksp -e KSPHARNESS_HOME=/path/to/harness-home -- ksph mcp
```

## Writing tests

Write tests as suites against install definitions, and run them with the suite runner. See
[src/KspHarness/README.md](src/KspHarness/README.md) for the library, and the modtests repository for a working setup.
