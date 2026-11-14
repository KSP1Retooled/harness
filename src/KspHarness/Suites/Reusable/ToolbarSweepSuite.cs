using System.Text;
using KspHarness.Installs;
using KspHarness.Library;

namespace KspHarness.Suites.Reusable;

/// <summary>
/// Clicks every app launcher button a mod added (ToolbarControl's buttons included, which sit on the stock launcher
/// when the Blizzy toolbar is not installed) in the space center, the VAB and flight. Each click must open something,
/// log no error from that mod, and close again on a second click without leaving an input lock behind. A mod set opts
/// in by deriving: <c>public sealed class FoundationToolbar : ToolbarSweepSuite&lt;Foundation&gt;;</c>
/// </summary>
/// <remarks>
/// A new uGUI window shows up as new active <c>RectTransform</c>s outside the launcher. IMGUI windows cannot be listed,
/// so they are seen in screenshots: pixels that change when the button is clicked, outside the button itself and
/// outside 16-pixel blocks that already changed between two screenshots taken just before (animation, clouds).
/// </remarks>
public abstract class ToolbarSweepSuite<TInstall> : KspSuite<TInstall>
    where TInstall : InstallDefinition, new()
{
    /// <summary>
    /// Button ids (as the report prints them) or owning mod names whose buttons act without opening a window, such as
    /// ClickThroughBlocker's mode toggle; they are still clicked and checked for errors and input locks.
    /// </summary>
    protected virtual IReadOnlyCollection<string> ButtonsWithoutWindow => [];

    /// <summary>Button ids or owning mod names not to click at all.</summary>
    protected virtual IReadOnlyCollection<string> SkippedButtons => [];

    /// <summary>The craft launched for the flight sweep.</summary>
    protected virtual string FlightCraft => "Kerbal X";

    /// <summary>The share of compared pixels that must change for a click to count as opening an IMGUI window.</summary>
    protected virtual double OpenedPixelFraction => 0.002;

    /// <summary>
    /// The fewest mod buttons each scene must show. Fewer fails the test, since a mod whose button never registered is
    /// what the sweep is there to catch.
    /// </summary>
    protected virtual int MinimumModButtons => 1;

    private const string SaveName = "ksph-toolbar-sweep";

    [Test]
    public async Task Space_center_buttons_open_and_close()
    {
        await Game.StartSandboxAsync(SaveName, cancellationToken: Cancel);
        await SweepAsync("SPACECENTER");
    }

    [Test]
    public async Task Editor_buttons_open_and_close()
    {
        await Game.StartSandboxAsync(SaveName, cancellationToken: Cancel);
        await Game.Run(
            """
            EditorDriver.StartEditor(EditorFacility.VAB);
            await Wait.Scene(GameScenes.EDITOR);
            """,
            cancellationToken: Cancel);
        await SweepAsync("EDITOR");
    }

    [Test]
    public async Task Flight_buttons_open_and_close()
    {
        await Game.StartSandboxAsync(SaveName, cancellationToken: Cancel);
        await Game.LaunchCraftAsync(FlightCraft, cancellationToken: Cancel);
        await SweepAsync("FLIGHT");
    }

    private async Task SweepAsync(string scene)
    {
        var dismissed = await this.RunAsync<List<string>>(SettleSnippet, [], TimeSpan.FromMinutes(1));
        if (dismissed.Count > 0)
        {
            Evidence.Note($"{scene}: dismissed {string.Join(", ", dismissed)} first");
        }

        var buttons = await this.RunAsync<List<ButtonInfo>>(ListSnippet, [], TimeSpan.FromMinutes(1));
        var report = new StringBuilder();
        var failures = new List<string>();
        var fromGame = buttons.Where(button => !button.IsMod).Select(button => button.Id).ToList();
        var modButtons = buttons.Where(button => button.IsMod).ToList();
        Evidence.Note($"{scene}: {modButtons.Count} mod buttons"
            + (fromGame.Count > 0
                ? $"; skipped {fromGame.Count} added by the game itself: {string.Join(", ", fromGame)}"
                : ""));
        if (modButtons.Count < MinimumModButtons)
        {
            failures.Add($"{scene}: {modButtons.Count} mod buttons on the app launcher, expected at least {MinimumModButtons}");
        }

        // The report is written however the sweep ends, so the buttons checked before a failure keep their lines.
        try
        {
            var number = 0;
            foreach (var button in modButtons)
            {
                number++;
                var problems = Matches(SkippedButtons, button) ? null : await CheckButtonAsync(scene, number, button, report);
                if (problems is null)
                {
                    report.AppendLine($"{button.Id} [{button.Owner}]: skipped");
                }
                else if (problems.Count > 0)
                {
                    failures.Add($"{scene} {button.Id} [{button.Owner}]: {string.Join("; ", problems)}");
                }
            }
        }
        finally
        {
            Evidence.Write($"{scene.ToLowerInvariant()}-buttons.txt", report.ToString());
        }

        Check.Empty(failures, "app launcher buttons that misbehaved");
    }

    // Clicks one button and returns what was wrong with it; its line, and the other mods' errors meanwhile, go to the
    // report.
    private async Task<List<string>> CheckButtonAsync(string scene, int number, ButtonInfo button, StringBuilder report)
    {
        var cursor = Game.Log.Cursor;
        var problems = new List<string>();
        ButtonCheck check;
        try
        {
            check = await this.RunAsync<ButtonCheck>(CheckSnippet, [button.Id], TimeSpan.FromMinutes(2));
        }
        catch (Exception e) when (!Cancel.IsCancellationRequested && Game.IsConnected)
        {
            // One button that breaks the check (a click handler that throws into the snippet, a snippet timeout) is
            // that button's problem; the sweep goes on with the next.
            problems.Add($"the check threw {e.GetType().Name}: {SuiteSnippets.FirstLine(e.Message)}");
            report.AppendLine($"{button.Id} [{button.Owner}] \"{button.Label}\": {ModErrors(cursor).Count} mod errors"
                + $" -- {string.Join("; ", problems)}");
            return problems;
        }

        var errors = ModErrors(cursor);

        // An owner that is not an assembly name cannot be matched to an error's stack, so every mod error logged during
        // the click counts against the button.
        var own = button.OwnerIsAssembly
            ? errors.Where(e => e.Mods.Any(mod => mod.StartsWith(button.Owner + "@", StringComparison.Ordinal))).ToList()
            : errors.ToList();

        var shot = $"{scene.ToLowerInvariant()}-{number:00}-{Safe(button.Id)}.png";
        if (check.OpenShot is { Length: > 0 })
        {
            Evidence.Write(shot, check.OpenShot);
        }

        var opened = check.UguiOpened > 0 || check.PixelsOpened >= OpenedPixelFraction;

        // A button that acts instead of opening a window can change the screen for good (clearing input locks unhides
        // parts of the UI), so only errors and input locks are checked for it.
        if (!Matches(ButtonsWithoutWindow, button))
        {
            if (!opened)
            {
                problems.Add("nothing opened");
            }

            if (check.UguiLeft > 0)
            {
                problems.Add($"{check.UguiLeft} uGUI objects stayed after the second click "
                    + $"({string.Join(", ", check.UguiLeftPaths)})");
            }

            if (check.UguiOpened == 0 && check.PixelsOpened >= OpenedPixelFraction && check.PixelsClosed >= 0.5)
            {
                problems.Add($"the window still shows after the second click ({check.PixelsClosed:P0} of its pixels)");
            }
        }

        if (check.LocksLeft.Count > 0)
        {
            problems.Add($"input locks left: {string.Join(", ", check.LocksLeft)}");
        }

        if (own.Count > 0)
        {
            var from = button.OwnerIsAssembly ? button.Owner : "mods during the click";
            problems.Add($"{own.Count} errors from {from}: {SuiteSnippets.FirstLine(own[0].Message)}");
        }

        if (!string.IsNullOrEmpty(check.Note))
        {
            problems.Add(check.Note);
        }

        if (problems.Count > 0 && check.ClosedShot is { Length: > 0 })
        {
            Evidence.Write(Path.ChangeExtension(shot, null) + "-closed.png", check.ClosedShot);
        }

        var owner = button.OwnerIsAssembly ? button.Owner : $"{button.Owner}, not an assembly name";
        var line = $"{button.Id} [{owner}] \"{button.Label}\": uGUI +{check.UguiOpened}, "
            + $"pixels {check.PixelsOpened:P2} "
            + $"open, {check.PixelsClosed:P0} of those left after closing (noise {check.Noise:P2}), "
            + $"{errors.Count} mod errors, {check.Seconds:0.0} s, {shot}"
            + (problems.Count > 0 ? $" -- {string.Join("; ", problems)}" : " -- ok");
        report.AppendLine(line);
        foreach (var error in errors.Except(own))
        {
            report.AppendLine($"    other mod error: {error}");
        }

        return problems;
    }

    private static bool Matches(IReadOnlyCollection<string> list, ButtonInfo button)
    {
        return list.Contains(button.Id, StringComparer.OrdinalIgnoreCase)
            || list.Contains(button.Owner, StringComparer.OrdinalIgnoreCase);
    }

    private static string Safe(string id)
    {
        var text = new string(id.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        return text.Length > 60 ? text[..60] : text;
    }

    private sealed record ButtonInfo(string Id, string Owner, string Label, bool IsMod, bool OwnerIsAssembly);

    private sealed record ButtonCheck(
        int UguiOpened,
        int UguiLeft,
        List<string> UguiLeftPaths,
        double PixelsOpened,
        double PixelsClosed,
        double Noise,
        List<string> LocksLeft,
        string? Note,
        double Seconds,
        byte[]? OpenShot,
        byte[]? ClosedShot);

    // Shared by both snippets: every active mod button on the stock launcher with an id that stays the same across
    // scenes and runs, and the mod that owns it. A ToolbarControl button's callbacks belong to ToolbarControl, so its
    // owner comes from the ToolbarControl object that made it. Buttons that would share an id (one handler for several
    // buttons) get "#2", "#3" in launcher order. Mods add their buttons from onGUIApplicationLauncherReady, some of them
    // a few frames later, so the list is taken a little after the launcher is ready.
    private const string Identify =
        """
        await Wait.Until(() => ApplicationLauncher.Ready && ApplicationLauncher.Instance != null);
        await Wait.Frames(10);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
        var mods = new Dictionary<System.Reflection.Assembly, string>();
        foreach (var entry in AssemblyLoader.loadedAssemblies)
        {
            if (entry.assembly != null && !(entry.url ?? "").StartsWith("Squad", StringComparison.OrdinalIgnoreCase)
                && entry.assembly.GetName().Name != "KspHarness.Mod")
            {
                mods[entry.assembly] = entry.assembly.GetName().Name;
            }
        }

        Func<Delegate, string> ownerOf = callback =>
            callback == null || callback.Method.DeclaringType == null
                ? null
                : callback.Method.DeclaringType.Assembly.GetName().Name;
        Func<Delegate, bool> isReal = callback =>
            callback != null && callback.Method.DeclaringType != null
            && callback.Method.DeclaringType.Assembly != typeof(ApplicationLauncher).Assembly;
        var managed = new Dictionary<ApplicationLauncherButton, string[]>();
        var tcType = AssemblyLoader.loadedAssemblies
            .Select(a => a.assembly?.GetType("ToolbarControl_NS.ToolbarControl", false))
            .FirstOrDefault(t => t != null);
        if (tcType != null && tcType.GetField("tcList", flags)?.GetValue(null) is System.Collections.IList controls)
        {
            foreach (object control in controls)
            {
                if (!(tcType.GetField("stockButton", flags)?.GetValue(control) is ApplicationLauncherButton stock))
                {
                    continue;
                }

                var callbacks = new[] { "onTrue", "m_onLeftClick", "onFalse", "m_onRightClick" }
                    .Select(field => tcType.GetField(field, flags)?.GetValue(control) as Delegate)
                    .Where(callback => callback != null);
                string nameSpace = tcType.GetField("nameSpace", flags)?.GetValue(control) as string ?? "";
                string toolbarId = tcType.GetField("toolbarId", flags)?.GetValue(control) as string ?? "";
                string tip = tcType.GetField("toolTip", flags)?.GetValue(control) as string ?? "";
                // A mod can also hand its click handler to the stock button directly.
                var direct = new Delegate[] { stock.onTrue, stock.onLeftClick, stock.onFalse, stock.onRightClick }
                    .Where(callback => isReal(callback) && callback.Method.DeclaringType.Assembly != tcType.Assembly);
                string owner = callbacks.Concat(direct).Select(ownerOf).FirstOrDefault();
                managed[stock] = new[] { nameSpace + "." + toolbarId, owner ?? nameSpace, tip, owner == null ? "" : "assembly" };
            }
        }

        var found = new List<(ApplicationLauncherButton Button, string Id, string Owner, string Label, bool IsMod, bool OwnerIsAssembly)>();
        var idCounts = new Dictionary<string, int>();
        foreach (var button in ApplicationLauncher.Instance.appListMod)
        {
            if (button == null || !button.gameObject.activeInHierarchy)
            {
                continue;
            }

            string id;
            if (managed.TryGetValue(button, out string[] tc))
            {
                id = tc[0];
                found.Add((button, id, tc[1], tc[2], true, tc[3] == "assembly"));
            }
            else
            {
                var callback = new Delegate[] { button.onTrue, button.onLeftClick, button.onFalse, button.onRightClick }
                    .FirstOrDefault(isReal);
                string owner = ownerOf(callback) ?? "Assembly-CSharp";
                id = callback == null ? "button" : callback.Method.DeclaringType.FullName + "." + callback.Method.Name;
                bool isMod = callback != null && mods.ContainsKey(callback.Method.DeclaringType.Assembly);
                found.Add((button, id, owner, "", isMod, true));
            }

            idCounts.TryGetValue(id, out int seen);
            idCounts[id] = ++seen;
            if (seen > 1)
            {
                var entry = found[found.Count - 1];
                found[found.Count - 1] = (entry.Button, id + "#" + seen, entry.Owner, entry.Label, entry.IsMod, entry.OwnerIsAssembly);
            }
        }

        """;

    // A new scene keeps changing for a while: dialogs such as the space center's welcome fade in, and the UI fills
    // in. Clicks are only compared against a screen that has stopped doing that, without dialogs in the way.
    private static readonly LibrarySnippet SettleSnippet = LibrarySnippet.Define(
        """
        var dismissed = new List<string>();
        var quiet = DateTime.UtcNow;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow - quiet < TimeSpan.FromSeconds(2) && DateTime.UtcNow < deadline)
        {
            var open = Dialogs.Open;
            if (open.Count > 0)
            {
                // Tutorial greetings ignore Escape, so a dialog with a single button gets that button.
                var dialog = open[0];
                string name = string.IsNullOrEmpty(dialog.Name) ? dialog.Title : dialog.Name;
                bool clicked = false;
                if (dialog.Buttons.Length == 1)
                {
                    try
                    {
                        await Dialogs.Click(name, dialog.Buttons[0]);
                        clicked = true;
                    }
                    catch (InvalidOperationException)
                    {
                        // A disabled or ambiguous button: fall back to Escape.
                    }
                }

                if (!clicked)
                {
                    Dialogs.Dismiss(name);
                }

                if (!dismissed.Contains(name))
                {
                    dismissed.Add(name);
                }

                quiet = DateTime.UtcNow;
            }

            await Wait.Frames(5);
        }

        return dismissed;
        """);

    private static readonly LibrarySnippet ListSnippet = LibrarySnippet.Define(Identify +
        """
        return found.Select(entry => new { entry.Id, entry.Owner, entry.Label, entry.IsMod, entry.OwnerIsAssembly }).ToList();
        """);

    private static readonly LibrarySnippet CheckSnippet = LibrarySnippet.Define(Identify +
        """
        string wanted = Args.Get<string>(__args, 0);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var matches = found.Where(entry => entry.Id == wanted).ToList();
        if (matches.Count != 1)
        {
            throw new InvalidOperationException(matches.Count + " active launcher buttons have the id " + wanted);
        }

        var target = matches[0].Button;
        var toggle = target.toggleButton;
        if (toggle.CurrentState == KSP.UI.UIRadioButton.State.True)
        {
            target.SetFalse(true);
        }

        // The launcher's own buttons and screen messages change on their own; neither is a window.
        var launcher = ApplicationLauncher.Instance.transform;
        var messages = ScreenMessages.Instance != null ? ScreenMessages.Instance.transform : null;
        Func<Dictionary<int, RectTransform>> ugui = () => Object.FindObjectsOfType<RectTransform>()
            .Where(rect => !rect.IsChildOf(launcher) && (messages == null || !rect.IsChildOf(messages)))
            .ToDictionary(rect => rect.GetInstanceID());

        // Screen pixels of the button, padded, so its own highlight does not count as a window.
        var corners = new Vector3[4];
        ((RectTransform)target.transform).GetWorldCorners(corners);
        var camera = KSP.UI.UIMasterController.Instance.uiCamera;
        var low = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        var high = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
        var buttonRect = Rect.MinMaxRect(low.x - 12, low.y - 12, high.x + 12, high.y + 12);

        byte[] openPng = null;
        byte[] lastPng = null;
        int width = 0;
        async Task<Color32[]> Shot(bool keep)
        {
            byte[] png = await Screenshot.Capture();
            var texture = new Texture2D(2, 2);
            texture.LoadImage(png);
            width = texture.width;
            var pixels = texture.GetPixels32();
            Object.Destroy(texture);
            if (keep)
            {
                openPng = png;
            }

            lastPng = png;

            return pixels;
        }

        bool Differs(Color32 a, Color32 b) => Math.Abs(a.r - b.r) > 24 || Math.Abs(a.g - b.g) > 24 || Math.Abs(a.b - b.b) > 24;

        await Wait.Frames(5);
        var before = new HashSet<string>(InputLockManager.lockStack.Keys);
        var first = await Shot(false);
        await Wait.Frames(8);
        var second = await Shot(false);
        await Wait.Frames(8);
        var third = await Shot(false);

        int height = third.Length / width;
        int blocksX = (width + 15) / 16;
        var noisy = new bool[blocksX * ((height + 15) / 16)];
        int noisePixels = 0;
        for (int i = 0; i < third.Length; i++)
        {
            if (Differs(first[i], third[i]) || Differs(second[i], third[i]))
            {
                noisy[(i / width / 16) * blocksX + (i % width) / 16] = true;
                noisePixels++;
            }
        }

        // Pixels the click changed, outside noisy blocks and the button; the window, if one opened.
        var window = new bool[third.Length];
        double Changed(Color32[] after)
        {
            int changed = 0;
            int compared = 0;
            for (int i = 0; i < third.Length; i++)
            {
                int x = i % width;
                int y = i / width;
                if (noisy[(y / 16) * blocksX + x / 16] || buttonRect.Contains(new Vector2(x, y)))
                {
                    continue;
                }

                compared++;
                if (Differs(third[i], after[i]))
                {
                    window[i] = true;
                    changed++;
                }
            }

            return compared == 0 ? 0 : (double)changed / compared;
        }

        // The share of the window's pixels that still differ from before the click. Counting only those keeps a
        // dialog that fades in elsewhere meanwhile from looking like a window left open.
        double Remaining(Color32[] after)
        {
            int inWindow = 0;
            int remaining = 0;
            for (int i = 0; i < third.Length; i++)
            {
                if (window[i])
                {
                    inWindow++;
                    if (Differs(third[i], after[i]))
                    {
                        remaining++;
                    }
                }
            }

            return inWindow == 0 ? 0 : (double)remaining / inWindow;
        }

        var closedUi = ugui();
        await Ui.Click(toggle.gameObject);
        await Wait.Frames(10);
        await Wait.RealSeconds(0.3);
        var openUi = ugui();
        var added = openUi.Keys.Where(id => !closedUi.ContainsKey(id)).ToList();
        var opened = await Shot(true);
        double pixelsOpened = Changed(opened);

        string note = null;
        if (toggle.CurrentState == KSP.UI.UIRadioButton.State.True || added.Count > 0 || pixelsOpened > 0)
        {
            await Ui.Click(toggle.gameObject);
        }

        await Wait.Frames(10);
        await Wait.RealSeconds(0.3);
        if (toggle.CurrentState == KSP.UI.UIRadioButton.State.True)
        {
            note = "the button stayed on after the second click";
            target.SetFalse(true);
            await Wait.Frames(5);
        }

        var afterUi = ugui();
        var left = added.Where(afterUi.ContainsKey).ToList();
        var closed = await Shot(false);
        double pixelsClosed = Remaining(closed);
        // The game locks input while its window is in the background ("EditorDriver_AppFocus", "ksc_ApplicationFocus"),
        // which comes and goes with other windows, not with the button.
        var locksLeft = InputLockManager.lockStack.Keys
            .Where(key => !before.Contains(key))
            .Where(key => !key.EndsWith("_AppFocus", StringComparison.Ordinal)
                && !key.EndsWith("_ApplicationFocus", StringComparison.Ordinal))
            .ToList();

        return new
        {
            UguiOpened = added.Count,
            UguiLeft = left.Count,
            UguiLeftPaths = left.Take(5).Select(id => Ui.PathOf(afterUi[id].gameObject)).ToList(),
            PixelsOpened = pixelsOpened,
            PixelsClosed = pixelsClosed,
            Noise = (double)noisePixels / third.Length,
            LocksLeft = locksLeft,
            Note = note,
            Seconds = clock.Elapsed.TotalSeconds,
            OpenShot = openPng,
            ClosedShot = lastPng,
        };
        """);
}
