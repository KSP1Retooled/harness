# KspHarness

The driver library. It launches KSP with the driver mod, compiles C# snippets against the instance's own assemblies,
and runs them in the game.

```csharp
// An instance from KspHarness.Installs; the driver mod that ships with the library is deployed into it first.
await using var game = await GameSession.LaunchAsync(instance);
await game.WaitForMainMenuAsync();
await game.StartSandboxAsync("test");
await game.LaunchCraftAsync("Kerbal X");

double target = 70000;
double speed = await game.Run(async ksp =>
{
    var vessel = ksp.FlightGlobals.ActiveVessel;
    while (vessel.altitude < target)
    {
        await Tick.FixedUpdate;
    }

    return vessel.srfSpeed;
});
```

- In a lambda with one parameter, `ksp.X` means the game's `X`, so the test needs no reference to the game.
- Captured locals are sent by value. `this` cannot be sent, so copy fields into locals first.
- Source strings work too: `await game.Run<int>("return FlightGlobals.Vessels.Count;")`.
- Typed lambdas (`game.Run(() => HighLogic.LoadedScene)`) need the test project to reference the game's assemblies
  with `Private=false`. At run time, a session resolves those assemblies from its instance (`GameAssemblies`). The
  suite runner loads each install's tests with that game's own assemblies. To drive stock and Retooled games from one
  process elsewhere, use `GameAssemblies.LoadInto`.
- Retooled has some types twice: `TMPro.*` is in both Assembly-CSharp and Unity.TextMeshPro. Snippets get the
  Assembly-CSharp copy, as stock-built mods do, and reach the other copy as `_Unity_TextMeshPro::TMPro.TMP_Text`.
- `ScreenshotAsync(superSize: 2)` renders the cameras again without the UI. Keep `superSize` at 1 to get what is on
  screen.
- Snippets can use private members. Compile errors point at the test's own file and line.
- Building with `KSP_DIR` set (a stock 1.12.5 install) also builds the mod into `mod/GameData/KspHarness` next to
  the library.
- Compiled snippets are cached under `KSPHARNESS_HOME/snippets`.

## Suites

For a repository of mod tests, make an exe that calls the suite runner and write suites against install definitions:

```csharp
return await SuiteRunner.RunAsync(args, typeof(Program).Assembly);

public sealed class RealChuteTests : KspSuite<PhysicsSim>
{
    [Test]
    public async Task Chute_deploys() { ... }   // fails when it throws; use Game, Cancel, Evidence, Check
}
```

`dotnet run -- --start <install>` builds each instance, boots one game per install definition and writes results to
`KSPHARNESS_HOME/runs/<run id>/`. `--list`, `--filter <regex>` and `--keep-game` help while you write tests.
`--approved <dir>` reads approved files from another folder. When an approval fails, its received and diff files are
kept with the test's evidence.

Ready-made suites in `KspHarness.Suites.Reusable` run for any install by deriving from them:

```csharp
public sealed class FoundationBoot : BootSuite<Foundation>;            // plugins, mod errors, parts, ModuleManager
public sealed class FoundationToolbar : ToolbarSweepSuite<Foundation>; // every mod launcher button opens and closes
public sealed class FoundationParts : PartCatalogSuite<Foundation>;    // every part spawns in the VAB
```

Override their virtual members for known-harmless errors and buttons that open no window.

## Popups

Instances answer known first-run popups with files (`KnownPopups`: KSP's privacy notice and What's New,
KSPCommunityFixes' texture cache question, Toolbar Controller's intro, Click Through Blocker's focus window). The
policy in `game.Popups` closes what still shows: while `WaitForMainMenuAsync` waits, in each suite reset, and when you
call `await game.DismissDialogsAsync()`. Every dialog that it answers stays in the log:

```csharp
install.AnswerPopup(KnownPopups.KspcfTextureCache, "no")   // in a definition: another answer, or
       .AnswerKnownPopups(false)                           // none, to test the popups themselves
       .AnswerDialog("Incompatible*", "OK");               // run-time rule; LeaveDialog keeps one open
Check.Empty(Game.Log.DialogsSince(GameStart).Select(d => d.ToString()), "dialogs");   // in a test
```

## Approved files

For values too large to write in a test, and for screenshots, compare with files committed next to the tests:

```csharp
var ctx = ApprovalContext.For(this);   // <dir>/<Suite>.<Test>.<name>.approved.txt|png
Approve.Value(await Game.Database.Nodes("PART[fuelTank*]"), "tanks", ctx);
Approve.Screenshot(await Game.ScreenshotAsync(), "menu", new ScreenshotTolerance { Masks = [new(0.6, 0.12, 0.4, 0.88)] }, ctx);
```

- Name the folder with `[assembly: AssemblyMetadata("KspHarness.ApprovedDirectory", "<dir>")]` (default `./approved`).
- A mismatch writes `.received.*` (and `.diff.png`, differences in red) next to the approved file and throws with a
  diff. Rerun with `KSPHARNESS_APPROVE=1` to accept the received files.
- On the stock main menu the planet and kerbals on the right move all the time, so unmasked shots taken seconds apart
  differ in 25-35% of pixels (measured at 2560x1440). With `(0.6, 0.12, 0.4, 0.88)` and `(0.45, 0.7, 0.15, 0.3)`
  masked, the largest difference is 2/255, well inside the default tolerance (8 per channel, 0.1% of pixels). The menu
  keeps animating for a few seconds after `WaitForMainMenuAsync` returns (1% of masked pixels still differ at 3 s), so
  wait about 5 s before a menu screenshot.

The [repository README](../../README.md) covers building and the tests.
