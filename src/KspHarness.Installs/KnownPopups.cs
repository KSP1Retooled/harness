namespace KspHarness.Installs;

/// <summary>
/// A question the game or a mod asks once and then remembers in a file, and the files that hold each answer. The
/// instance builder writes the answer for every known popup whose mod is in the instance, so that an unattended game
/// never stops on it; <see cref="InstallBuilder.AnswerPopup"/> picks another answer.
/// </summary>
public sealed class KnownPopup
{
    private readonly Func<string, bool> isPresent;
    private readonly Action<string, string, StartingInstall> write;
    private readonly Action<string> remove;

    internal KnownPopup(
        string id,
        string description,
        IReadOnlyList<string> answers,
        Func<string, bool> isPresent,
        Action<string, string, StartingInstall> write,
        Action<string> remove
    )
    {
        Id = id;
        Description = description;
        Answers = answers;
        this.isPresent = isPresent;
        this.write = write;
        this.remove = remove;
    }

    /// <summary>The name definitions use, <c>&lt;mod&gt;.&lt;popup&gt;</c>.</summary>
    public string Id { get; }

    public string Description { get; }

    /// <summary>The answers it accepts; the first is the one written by default.</summary>
    public IReadOnlyList<string> Answers { get; }

    public string DefaultAnswer => Answers[0];

    /// <summary>True when the instance at <paramref name="gameDir"/> has the code that shows the popup.</summary>
    public bool IsPresent(string gameDir) => isPresent(gameDir);

    /// <summary>Writes the files that hold <paramref name="answer"/>, replacing any earlier answer.</summary>
    public void Write(string gameDir, string answer, StartingInstall start) => write(gameDir, answer, start);

    /// <summary>Removes what <see cref="Write"/> writes, so the popup shows again.</summary>
    public void Remove(string gameDir) => remove(gameDir);
}

/// <summary>The first-run popups the instance builder answers with files.</summary>
public static class KnownPopups
{
    /// <summary>KSP's Unity Analytics privacy notice on the first visit to the main menu (stock and Retooled).</summary>
    public const string KspPrivacy = "KSP.Privacy";

    /// <summary>
    /// Stock KSP's What's New window on the main menu, and the expansion adverts it shows when an expansion is
    /// missing. Retooled never shows them.
    /// </summary>
    public const string KspWhatsNew = "KSP.WhatsNew";

    /// <summary>KSPCommunityFixes asks while loading whether to cache PNG textures; loading waits for the answer.</summary>
    public const string KspcfTextureCache = "KSPCommunityFixes.TextureCache";

    /// <summary>Toolbar Controller's intro window, shown at the space center on every start until hidden.</summary>
    public const string ToolbarControllerIntro = "ToolbarController.Intro";

    /// <summary>Click Through Blocker's focus model window, shown at the space center in each new game.</summary>
    public const string ClickThroughBlockerFocus = "ClickThroughBlocker.Focus";

    /// <summary>Changes whenever an entry writes different files, so that instances built with the old ones rebuild.</summary>
    internal const int Version = 1;

    private const string KspcfFolder = "GameData/KSPCommunityFixes";
    private const string ToolbarFolder = "GameData/001_ToolbarControl";
    private const string CtbFolder = "GameData/000_ClickThroughBlocker";

    private static readonly string[] PrivacyKeys = ["SHOW_ANALYTICS_DIALOG"];

    private static readonly string[] WhatsNewKeys =
    [
        "SHOW_WHATSNEW_DIALOG",
        "SHOW_WHATSNEW_DIALOG_VersionsShown",
        "MISSION_SHOW_EXPANSION_INFO",
        "SERENITY_SHOW_EXPANSION_INFO",
    ];

    public static IReadOnlyList<KnownPopup> All { get; } =
    [
        // MainMenu shows the notice when the setting is true, or when it is missing and Unity's PlayerPrefs (shared
        // by every KSP on the machine) hold no opt-out choice.
        new KnownPopup(
            KspPrivacy,
            "KSP's Unity Analytics privacy notice on the main menu",
            ["hide"],
            _ => true,
            (dir, _, _) => SetSettings(dir, PrivacyKeys.Select(key => KeyValuePair.Create(key, "False"))),
            dir => RemoveSettings(dir, PrivacyKeys)
        ),
        // What's New shows unless it is switched off and the running version is listed as shown. The expansion
        // adverts are switched off with it, because MainMenu offers them only once What's New is off.
        new KnownPopup(
            KspWhatsNew,
            "stock KSP's What's New window and expansion adverts on the main menu",
            ["hide"],
            _ => true,
            (dir, _, start) =>
                SetSettings(
                    dir,
                    [
                        KeyValuePair.Create("SHOW_WHATSNEW_DIALOG", "False"),
                        KeyValuePair.Create("SHOW_WHATSNEW_DIALOG_VersionsShown", start.KspVersion ?? "1.12.5"),
                        KeyValuePair.Create("MISSION_SHOW_EXPANSION_INFO", "False"),
                        KeyValuePair.Create("SERENITY_SHOW_EXPANSION_INFO", "False"),
                    ]
                ),
            dir => RemoveSettings(dir, WhatsNewKeys)
        ),
        // The file KSPCommunityFixes writes when the question is answered (KSPCFFastLoader.SetOptIn).
        new KnownPopup(
            KspcfTextureCache,
            "KSPCommunityFixes' PNG texture cache question while loading",
            ["yes", "no"],
            dir => File.Exists(Path.Combine(dir, KspcfFolder, "Plugins", "KSPCommunityFixes.dll")),
            (dir, answer, _) =>
                WriteFile(
                    dir,
                    KspcfFolder + "/PluginData/PNGTextureCache.cfg",
                    $"userOptInChoiceDone = True\ntextureCacheEnabled = {(answer == "yes" ? "True" : "False")}\n"
                ),
            dir => DeleteFile(dir, KspcfFolder + "/PluginData/PNGTextureCache.cfg")
        ),
        // ToolbarControl.LoadData reads showWindowAtStartup from this file; the mods' toolbar entries it also keeps
        // there are added back as each mod registers.
        new KnownPopup(
            ToolbarControllerIntro,
            "Toolbar Controller's intro window at the space center",
            ["hide"],
            dir => File.Exists(Path.Combine(dir, ToolbarFolder, "Plugins", "ToolbarControl.dll")),
            (dir, _, _) =>
                WriteFile(
                    dir,
                    ToolbarFolder + "/PluginData/ToolbarControl.cfg",
                    "ToolbarControlData\n{\n\tshowWindowAtStartup = False\n}\n"
                ),
            dir => DeleteFile(dir, ToolbarFolder + "/PluginData/ToolbarControl.cfg")
        ),
        // The global default its "Save as global default" button writes: with it, every new game takes the focus
        // model from the file and does not ask. "mouse" is the mod's own default.
        new KnownPopup(
            ClickThroughBlockerFocus,
            "Click Through Blocker's focus model window in each new game",
            ["mouse", "click"],
            dir => File.Exists(Path.Combine(dir, CtbFolder, "Plugins", "ClickThroughBlocker.dll")),
            (dir, answer, _) =>
                WriteFile(
                    dir,
                    CtbFolder + "/Global.cfg",
                    $"focusFollowsClick = {(answer == "click" ? "True" : "False")}\nuniversalClickBlocking = False\n"
                ),
            dir => DeleteFile(dir, CtbFolder + "/Global.cfg")
        ),
    ];

    public static KnownPopup Get(string id)
    {
        return All.FirstOrDefault(popup => string.Equals(popup.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"no known popup '{id}'; known: {string.Join(", ", All.Select(popup => popup.Id))}",
                nameof(id)
            );
    }

    /// <summary>
    /// Writes each present popup's answer, or removes it where <paramref name="answers"/> maps the popup to null.
    /// </summary>
    internal static IReadOnlyList<string> Apply(
        string gameDir,
        IReadOnlyDictionary<string, string?> answers,
        StartingInstall start
    )
    {
        var applied = new List<string>();
        foreach (var popup in All.Where(popup => popup.IsPresent(gameDir)))
        {
            var answer = answers[popup.Id];
            if (answer is null)
            {
                popup.Remove(gameDir);
            }
            else
            {
                popup.Write(gameDir, answer, start);
                applied.Add($"{popup.Id}={answer}");
            }
        }

        return applied;
    }

    private static void SetSettings(string gameDir, IEnumerable<KeyValuePair<string, string>> values)
    {
        SettingsFile.Apply(Path.Combine(gameDir, SettingsFile.FileName), values);
    }

    private static void RemoveSettings(string gameDir, IEnumerable<string> keys)
    {
        var path = Path.Combine(gameDir, SettingsFile.FileName);
        if (File.Exists(path))
        {
            SettingsFile.Remove(path, keys);
        }
    }

    // Instance files can be hard links into the starting install, so a file is deleted before it is written.
    private static void WriteFile(string gameDir, string relative, string text)
    {
        var path = RelativePaths.Resolve(gameDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        File.WriteAllText(path, text.Replace("\n", "\r\n"));
    }

    private static void DeleteFile(string gameDir, string relative)
    {
        var path = RelativePaths.Resolve(gameDir, relative);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
