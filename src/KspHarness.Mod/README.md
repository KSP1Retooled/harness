# KspHarness.Mod

The in-game driver mod: a loopback socket server that loads snippet assemblies and runs them on the main thread.
The wire protocol is in `Protocol/`, the value encoding in `Values/` and the script API in `Script/`.

## Build

```sh
dotnet build src/KspHarness.Mod -c Release -p:KspDir=<stock KSP 1.12.5 root>
```

`KSP_DIR` works instead of `-p:KspDir`. Build against a **stock** install, because KSP Retooled relinks the same DLL.
The mod compiles in the `KspHarness.ConfigNodes` sources.

## Install and start

The driver library deploys the mod into instances itself. To install it by hand, copy
`bin/Release/KspHarness.Mod.dll` to `GameData/KspHarness/Plugins/`, then start the game with a port and a token of
your choice (the values are examples):

```sh
KSP_x64.exe -ksp-harness-port 52188 -ksp-harness-token 0123456789abcdef0123456789abcdef
```

On Linux start `./KSP.x86_64`, and on macOS `KSP.app/Contents/MacOS/KSP`, with the same arguments.

Without both arguments the mod logs one line and does nothing. With them, it also copies Unity's `-screen-width`,
`-screen-height` and `-screen-fullscreen` into KSP's settings, so the window keeps that size whatever settings.cfg
says. `GameSession.ConnectAsync(port, token, instanceDir)` attaches to a game started this way.
