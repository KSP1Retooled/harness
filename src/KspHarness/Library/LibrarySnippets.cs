namespace KspHarness.Library;

/// <summary>
/// The library's own snippets. They read their arguments from <c>__args</c>, the generated method's parameter, so that
/// one compiled assembly serves every argument value.
/// </summary>
internal static class LibrarySnippets
{
    public static readonly LibrarySnippet WaitForMainMenu = LibrarySnippet.Define(
        """
        await Wait.Scene(GameScenes.MAINMENU);
        """);

    // Follows the main menu's own path through New Game > Sandbox > Start: the dialog must exist, because
    // ConfirmNewGame dismisses it. An existing save of that name is replaced, as the overwrite prompt's "yes" would.
    // The game it leaves has no intro tutorial.
    public static readonly LibrarySnippet StartSandbox = LibrarySnippet.Define(
        """
        string name = Args.Get<string>(__args, 0);
        if (HighLogic.LoadedScene != GameScenes.MAINMENU)
        {
            throw new InvalidOperationException("StartSandbox needs the main menu, but the game is in " + HighLogic.LoadedScene);
        }

        var menu = Object.FindObjectOfType<MainMenu>();
        if (menu == null)
        {
            throw new InvalidOperationException("the main menu scene has no MainMenu");
        }

        MainMenu.pName = KSPUtil.SanitizeString(name, '_', true);
        if (MainMenu.newGameMode != Game.Modes.SANDBOX || MainMenu.newGameParameters == null)
        {
            MainMenu.newGameMode = Game.Modes.SANDBOX;
            MainMenu.newGameParameters = GameParameters.GetDefaultParameters(Game.Modes.SANDBOX, GameParameters.Preset.Normal);
        }

        menu.NewGame();
        if (System.IO.Directory.Exists(KSPUtil.ApplicationRootPath + "saves/" + MainMenu.pName))
        {
            menu.OverWriteNewGame();
        }
        else
        {
            menu.ConfirmNewGame();
        }

        await Wait.Scene(GameScenes.SPACECENTER);

        // Every new game, sandbox included, gets stock's intro tutorial: a greeting at the space center now, and in
        // the editors and the tracking station later, each with an input lock. This finishes it as its own setup does
        // once all three greetings are answered, so that no later scene opens one.
        HighLogic.CurrentGame.RemoveProtoScenarioModule(typeof(ScenarioNewGameIntro));
        foreach (var intro in Object.FindObjectsOfType<ScenarioNewGameIntro>())
        {
            intro.kscComplete = true;
            intro.editorComplete = true;
            intro.tsComplete = true;
            intro.CloseTutorialWindow();
        }

        GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
        await Wait.Until(() => Object.FindObjectsOfType<ScenarioNewGameIntro>().Length == 0);
        """);

    // Follows the space center's launch with the craft's default crew, and returns once the vessel is unpacked. A
    // craft name is looked up in the save's Ships folder and then the install's, under the site's facility first.
    public static readonly LibrarySnippet Launch = LibrarySnippet.Define(
        """
        string craft = Args.Get<string>(__args, 0);
        string site = Args.Get<string>(__args, 1);
        if (HighLogic.CurrentGame == null)
        {
            throw new InvalidOperationException("Launch needs a game; start or load one first");
        }

        string root = KSPUtil.ApplicationRootPath;
        var candidates = new List<string>();
        if (craft.EndsWith(".craft", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(System.IO.Path.IsPathRooted(craft) ? craft : System.IO.Path.Combine(root, craft));
        }
        else
        {
            var facilities = site == "Runway" ? new[] { "SPH", "VAB" } : new[] { "VAB", "SPH" };
            foreach (var facility in facilities)
            {
                candidates.Add(System.IO.Path.Combine(root, "saves", HighLogic.SaveFolder, "Ships", facility, craft + ".craft"));
                candidates.Add(System.IO.Path.Combine(root, "Ships", facility, craft + ".craft"));
            }
        }

        string path = candidates.FirstOrDefault(System.IO.File.Exists);
        if (path == null)
        {
            throw new System.IO.FileNotFoundException("craft not found; looked for " + string.Join(", ", candidates));
        }

        var crew = HighLogic.CurrentGame.CrewRoster.DefaultCrewForVessel(ConfigNode.Load(path));
        FlightDriver.StartWithNewLaunch(path, HighLogic.CurrentGame.flagURL, site, crew);

        // A craft the game cannot load sends the flight scene straight back to the space center, with a dialog.
        GameScenes left = GameScenes.FLIGHT;
        await Wait.Until(() =>
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
            {
                left = HighLogic.LoadedScene;
                return true;
            }

            // The flight scene is ready before physics starts; staging a vessel that is still packed does nothing.
            return FlightGlobals.ready && FlightGlobals.ActiveVessel != null && !FlightGlobals.ActiveVessel.packed;
        });

        if (left != GameScenes.FLIGHT)
        {
            string dialogs = string.Join("; ", Dialogs.Open.Select(d => d.Title + ": " + d.Message));
            throw new InvalidOperationException("the flight did not start; the game went to " + left + " instead"
                + (dialogs.Length > 0 ? " and shows " + dialogs : "") + " (" + path + ")");
        }

        await Wait.Scene(GameScenes.FLIGHT);
        """);

    // Follows the main menu's Resume Game (and, for a save made in flight, the quickload path): the file goes through
    // the upgrade pipeline first, whose callbacks may come later, then the game starts in the scene it was saved in.
    public static readonly LibrarySnippet LoadSave = LibrarySnippet.Define(
        """
        string folder = Args.Get<string>(__args, 0);
        string file = Args.Get<string>(__args, 1);
        string described = "saves/" + folder + "/" + file + ".sfs";
        ConfigNode saved = GamePersistence.LoadSFSFile(file, folder);
        if (saved == null)
        {
            throw new System.IO.FileNotFoundException("no save " + described);
        }

        ConfigNode upgraded = null;
        bool failed = false;
        // LoadContext.SFS by value: Retooled 1.13.0-rc8 names that member flag_1.
        var sfs = (SaveUpgradePipeline.LoadContext)1;
        KSPUpgradePipeline.Process(saved, folder, sfs, node => upgraded = node, (option, node) => failed = true);
        await Wait.Until(() => upgraded != null || failed);
        if (failed)
        {
            throw new InvalidOperationException("the upgrade pipeline rejected " + described);
        }

        Game game = GamePersistence.LoadGameCfg(upgraded, folder, true, false);
        if (game == null || !game.compatible)
        {
            throw new InvalidOperationException(described + " is not compatible with this game");
        }

        // The flight scene reads the current game, so it must be set before Start, which for a save made in flight
        // resumes it with its active vessel.
        HighLogic.CurrentGame = game;
        HighLogic.SaveFolder = folder;
        GamePersistence.UpdateScenarioModules(game);
        GameEvents.onGameStatePostLoad.Fire(upgraded);
        GameScenes scene = game.startScene;
        game.Start();
        await Wait.Scene(scene);
        if (scene == GameScenes.FLIGHT)
        {
            await Wait.Until(() => FlightGlobals.ActiveVessel != null && !FlightGlobals.ActiveVessel.packed);
        }
        """);

    // Saves as the quicksave key does, minus the screen message, so that a save made in flight resumes there.
    public static readonly LibrarySnippet QuickSave = LibrarySnippet.Define(
        """
        string name = Args.Get<string>(__args, 0);
        if (HighLogic.CurrentGame == null)
        {
            throw new InvalidOperationException("QuickSave needs a running game");
        }

        bool inFlight = HighLogic.LoadedScene == GameScenes.FLIGHT;
        if (inFlight && FlightGlobals.ClearToSave() != ClearToSaveStatus.CLEAR)
        {
            throw new InvalidOperationException("the game refuses to save now: " + FlightGlobals.ClearToSave());
        }

        Game game = HighLogic.CurrentGame.Updated();
        game.startScene = inFlight ? GameScenes.FLIGHT : HighLogic.LoadedScene;
        if (string.IsNullOrEmpty(GamePersistence.SaveGame(game, name, HighLogic.SaveFolder, SaveMode.OVERWRITE)))
        {
            throw new InvalidOperationException("the game did not write the save " + name);
        }
        """);

    // The live database queries take the query as an argument, so that one assembly serves every query.
    public static readonly LibrarySnippet DatabaseQuery = LibrarySnippet.Define(
        """
        return Database.Query(Args.Get<string>(__args, 0));
        """);

    public static readonly LibrarySnippet DatabaseValues = LibrarySnippet.Define(
        """
        return Database.Values(Args.Get<string>(__args, 0));
        """);

    // Leaves without saving: a test's game is scratch.
    public static readonly LibrarySnippet ReturnToMainMenu = LibrarySnippet.Define(
        """
        if (HighLogic.LoadedScene != GameScenes.MAINMENU)
        {
            HighLogic.LoadScene(GameScenes.MAINMENU);
        }

        await Wait.Scene(GameScenes.MAINMENU);
        """);
}
