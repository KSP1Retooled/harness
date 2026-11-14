namespace KspHarness.Library;

/// <summary>The snippets behind <see cref="PopupPolicy"/>.</summary>
internal static class PopupSnippets
{
    // Arguments: dialog rules as (pattern, button or "" to leave open) pairs, default buttons, whether to answer
    // dialogs no rule names and close windows, close-button paths, whether to wait for the main menu first, and the
    // settle time in seconds. Returns what it closed as (kind, name, detail) triples.
    //
    // A dialog is answered from its second sighting, a poll later, because some fill in their buttons after they
    // spawn. One with several buttons and none of the defaults is left open, because guessing could pick "Delete", and
    // so is one without buttons, which is usually a progress message.
    public static readonly LibrarySnippet Sweep = LibrarySnippet.Define(
        """
        string[] rules = Args.Get<string[]>(__args, 0);
        string[] defaults = Args.Get<string[]>(__args, 1);
        bool answerOthers = Args.Get<bool>(__args, 2);
        string[] closePaths = Args.Get<string[]>(__args, 3);
        bool waitForMenu = Args.Get<bool>(__args, 4);
        double settleSeconds = Args.Get<double>(__args, 5);

        var report = new List<string>();
        var seen = new HashSet<int>();
        var reportedLeft = new HashSet<int>();

        bool Matches(string text, string pattern) =>
            text != null && System.Text.RegularExpressions.Regex.IsMatch(text,
                "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

        string Caption(DialogInfo dialog, string pattern) => dialog.Buttons.FirstOrDefault(b => Matches(b, pattern));

        void Add(string kind, string name, string detail)
        {
            report.Add(kind);
            report.Add(name);
            report.Add(detail);
            Debug.Log("[KspHarness] popup policy: " + kind + " " + name + ": " + detail);
        }

        // The caption of the button to press, or null to leave the dialog open.
        string Choose(DialogInfo dialog, out string why)
        {
            why = null;
            for (int i = 0; i + 1 < rules.Length; i += 2)
            {
                if (!Matches(dialog.Name, rules[i]) && !Matches(dialog.Title, rules[i]))
                {
                    continue;
                }

                if (rules[i + 1].Length == 0)
                {
                    why = "left open by the rule '" + rules[i] + "'";
                    return null;
                }

                string ruled = Caption(dialog, rules[i + 1]);
                if (ruled != null)
                {
                    return ruled;
                }

                why = "the rule '" + rules[i] + "' names the button '" + rules[i + 1] + "', which it lacks";
                break;
            }

            if (!answerOthers)
            {
                why = why ?? "no rule names it";
                return null;
            }

            foreach (string preferred in defaults)
            {
                string caption = Caption(dialog, preferred);
                if (caption != null)
                {
                    return caption;
                }
            }

            if (dialog.Buttons.Length == 1)
            {
                return dialog.Buttons[0];
            }

            why = (why == null ? "" : why + "; ")
                + (dialog.Buttons.Length == 0 ? "it has no buttons" : "no default button among [" + string.Join(", ", dialog.Buttons) + "]");
            return null;
        }

        async Task SweepOnce()
        {
            var open = Dialogs.Open;
            foreach (DialogInfo dialog in open)
            {
                if (!seen.Contains(dialog.Id))
                {
                    continue;
                }

                string button = Choose(dialog, out string why);
                string label = "\"" + dialog.Title + "\"";
                try
                {
                    if (button == null)
                    {
                        if (reportedLeft.Add(dialog.Id))
                        {
                            Add("dialog", dialog.Name, label + " left open: " + why);
                        }
                    }
                    else
                    {
                        Add("dialog", dialog.Name, label + " clicked \"" + button + "\"");
                        await Dialogs.Click(dialog.Id, button);
                    }
                }
                catch (InvalidOperationException e)
                {
                    if (reportedLeft.Add(dialog.Id))
                    {
                        Add("dialog", dialog.Name, label + " could not be answered: " + e.Message);
                    }
                }
            }

            seen.Clear();
            foreach (DialogInfo dialog in open)
            {
                seen.Add(dialog.Id);
            }

            if (!answerOthers)
            {
                return;
            }

            foreach (string path in closePaths)
            {
                // Through object: the driver's compile-time stubs type the list as dynamic.
                foreach (object found in Ui.FindAll(path))
                {
                    var button = (GameObject)found;
                    string where = Ui.PathOf(button);
                    try
                    {
                        await Ui.Click(button);
                        Add("window", where, "clicked");
                    }
                    catch (InvalidOperationException e)
                    {
                        Add("window", where, "could not click: " + e.Message);
                    }
                }
            }
        }

        const double PollSeconds = 0.2;
        if (waitForMenu)
        {
            bool loaded = false;
            Exception failed = null;
            async Task WatchScene()
            {
                try
                {
                    await Wait.Scene(GameScenes.MAINMENU);
                }
                catch (Exception e)
                {
                    failed = e;
                }

                loaded = true;
            }

            var watch = WatchScene();
            while (!loaded)
            {
                await SweepOnce();
                await Wait.RealSeconds(PollSeconds);
            }

            await watch;
            if (failed != null)
            {
                throw failed;
            }
        }

        // At least two polls, so a dialog already open is answered at its second sighting.
        float until = Time.realtimeSinceStartup + (float)settleSeconds;
        int polls = 0;
        while (polls < 2 || Time.realtimeSinceStartup < until)
        {
            await SweepOnce();
            polls++;
            await Wait.RealSeconds(PollSeconds);
        }

        await SweepOnce();
        return report.ToArray();
        """);

    // Arguments: the simple names to look for. Returns the ones loaded in the game.
    public static readonly LibrarySnippet LoadedAssemblies = LibrarySnippet.Define(
        """
        string[] wanted = Args.Get<string[]>(__args, 0);
        var loaded = new HashSet<string>(AssemblyLoader.loadedAssemblies.Select(a => a.name), StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            loaded.Add(assembly.GetName().Name);
        }

        return wanted.Where(loaded.Contains).ToArray();
        """);
}
