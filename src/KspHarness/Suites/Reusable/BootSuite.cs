using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using KspHarness.Installs;
using KspHarness.Library;

namespace KspHarness.Suites.Reusable;

/// <summary>
/// What every install must do before anything else: reach the main menu with every plugin loaded, no exception from
/// mod code, a part for every PART config and, with ModuleManager, no patch errors. A mod set opts in by deriving:
/// <c>public sealed class FoundationBoot : BootSuite&lt;Foundation&gt;;</c>
/// </summary>
public abstract class BootSuite<TInstall> : KspSuite<TInstall>
    where TInstall : InstallDefinition, new()
{
    /// <summary>
    /// Regular expressions for boot errors from mod code that are known to be harmless; an error whose message matches
    /// one is noted instead of failing <see cref="No_exception_from_mod_code_while_booting"/>.
    /// </summary>
    protected virtual IReadOnlyList<string> KnownBootErrors => [];

    // KSPCommunityFixes' fast loader stops the stock loader by throwing, and says so in the message.
    private static readonly string[] BuiltInKnownBootErrors = [@"this is intended and not an error"];

    [Test(FreshGame = true)]
    public async Task Reaches_the_main_menu()
    {
        var scene = await Game.Run<string>("return HighLogic.LoadedScene.ToString();", cancellationToken: Cancel);
        Check.Equal("MAINMENU", scene, "scene");
        Evidence.Note($"{(Game.Info.Retooled ? "Retooled" : "stock")} {Game.Info.Ksp}, Unity {Game.Info.Unity}");
    }

    [Test]
    public async Task Every_plugin_in_GameData_loaded()
    {
        var gameData = Instance.GameDataDir;
        var installed = Directory
            .EnumerateFiles(gameData, "*.dll", SearchOption.AllDirectories)
            .Select(path =>
                (Path: path, Key: Path.GetRelativePath(gameData, path).Replace(Path.DirectorySeparatorChar, '/'))
            )
            // KSP loads no plugins from PluginData folders, and native libraries are not plugins.
            .Where(file =>
                !file.Key.Contains("/PluginData/", StringComparison.OrdinalIgnoreCase)
                && !file.Key.StartsWith("PluginData/", StringComparison.OrdinalIgnoreCase)
            )
            .Where(file => IsManagedAssembly(file.Path))
            .Select(file => file.Key)
            .ToList();

        var report = await this.RunAsync<PluginReport>(PluginsSnippet, [], TimeSpan.FromMinutes(2));
        var loaded = report.Loaded.ToDictionary(
            entry => entry.File,
            entry => entry.Problem,
            StringComparer.OrdinalIgnoreCase
        );
        var provided = new HashSet<string>(report.ProvidedByGame, StringComparer.OrdinalIgnoreCase);

        // Retooled logs the DLLs it skips because it ships them itself (KSPCommunityFixes); they count as loaded.
        foreach (var log in Game.Log.LogsSince(GameStart))
        {
            var match = ProvidedByGame.Match(log.Message);
            if (match.Success)
            {
                provided.Add(
                    Path.GetRelativePath(gameData, match.Groups[1].Value).Replace(Path.DirectorySeparatorChar, '/')
                );
            }
        }

        var missing = installed.Where(file => !loaded.ContainsKey(file) && !provided.Contains(file)).ToList();
        var providedInstalled = installed.Where(provided.Contains).ToList();
        Evidence.Note(
            $"{installed.Count} plugin DLLs installed, {report.Loaded.Count} assemblies loaded"
                + (providedInstalled.Count > 0 ? $", provided by the game: {string.Join(", ", providedInstalled)}" : "")
        );

        Check.Empty(missing, "installed plugins the game did not load");
        Check.Empty(
            report.Loaded.Where(entry => entry.Problem != null).Select(entry => $"{entry.File}: {entry.Problem}"),
            "plugins whose types failed to load"
        );
    }

    [Test]
    public Task No_exception_from_mod_code_while_booting()
    {
        var known = BuiltInKnownBootErrors.Concat(KnownBootErrors).Select(pattern => new Regex(pattern)).ToList();
        var fromMods = ModErrors(GameStart);
        var accepted = fromMods.Where(e => known.Any(regex => regex.IsMatch(e.Message))).ToList();
        var errors = fromMods.Except(accepted).ToList();

        Evidence.Note(
            $"{Game.Log.ErrorsSince(GameStart).Count} errors logged since boot, {fromMods.Count} from mod code"
                + (accepted.Count > 0 ? $", {accepted.Count} of them known" : "")
        );
        if (errors.Count > 0)
        {
            Evidence.Write("mod-errors.txt", string.Join("\n\n", errors.Select(e => $"{e}\n{e.Stack}")));
        }

        Check.Empty(
            errors.Select(e => $"[{string.Join(", ", e.Mods)}] {SuiteSnippets.FirstLine(e.Message)}"),
            "errors and exceptions whose stack runs through a mod"
        );
        return Task.CompletedTask;
    }

    [Test]
    public async Task Part_database_matches_the_PART_configs()
    {
        var report = await this.RunAsync<PartReport>(PartsSnippet, [], TimeSpan.FromMinutes(2));
        Evidence.Note($"{report.Loaded.Count} parts loaded, {report.Configs.Count} distinct PART config names");

        var loaded = new HashSet<string>(report.Loaded);
        var configs = new HashSet<string>(report.Configs);
        Check.Empty(configs.Where(name => !loaded.Contains(name)).Order(), "PART configs that did not become parts");
        Check.Empty(loaded.Where(name => !configs.Contains(name)).Order(), "loaded parts with no PART config");
    }

    [Test]
    public Task ModuleManager_patched_without_errors()
    {
        var installed = Directory.EnumerateFiles(Instance.GameDataDir, "ModuleManager*.dll").Any();
        if (!installed)
        {
            Evidence.Note("ModuleManager is not installed");
            return Task.CompletedTask;
        }

        var problems = new List<string>();
        var logs = Game.Log.LogsSince(GameStart);
        var done = logs.Any(log =>
            log.Message.StartsWith("[ModuleManager] Total loading Time", StringComparison.Ordinal)
            || log.Message.StartsWith("[ModuleManager] Post patch ran", StringComparison.Ordinal)
        );
        if (!done)
        {
            problems.Add(
                "ModuleManager never reported that it finished (no \"Post patch ran\" or \"Total loading Time\")"
            );
        }

        problems.AddRange(
            logs.Where(log =>
                    log.Message.StartsWith("[ModuleManager]", StringComparison.Ordinal)
                    && (log.IsError || MmErrorText.IsMatch(log.Message))
                )
                .Select(log => SuiteSnippets.FirstLine(log.Message))
        );

        // ModuleManager 4 sends all its output to this file on every boot. A cache load embeds the patch log of the
        // run that made the cache, and MM writes a cache only after a run without errors.
        var patchLog = Path.Combine(Instance.Root, "Logs", "ModuleManager", "ModuleManager.log");
        if (File.Exists(patchLog))
        {
            var lines = File.ReadAllLines(patchLog);
            var status = lines.LastOrDefault(line => MmStatus.IsMatch(line));
            Evidence.Note(
                status is null
                    ? "no ModuleManager status line"
                    : Regex.Replace(status, @"<[^>]+>|^\[\w+ [\d:.]+\]", "").Trim()
            );
            Evidence.CopyFile("ModuleManager.log", patchLog);
            problems.AddRange(
                lines
                    .Where(line =>
                        line.Contains("[ERR", StringComparison.Ordinal)
                        || line.Contains("[EXC", StringComparison.Ordinal)
                        || MmErrorText.IsMatch(line)
                    )
                    .Select(line => Regex.Replace(line, "<[^>]+>", "").Trim())
            );
        }
        else
        {
            Evidence.Note($"no ModuleManager log at {patchLog}");
        }

        Check.Empty(problems.Distinct(), "ModuleManager patch errors");
        return Task.CompletedTask;
    }

    private static readonly Regex ProvidedByGame = new(@"^\[AssemblyLoader\] skipping (.+\.dll): provided by the game");

    private static readonly Regex MmStatus = new(@"ModuleManager: \d+ patch(es)? (applied|loaded from cache)");

    private static readonly Regex MmErrorText = new(
        @"found (<[^>]+>)?[1-9]\d* error|encountered (<[^>]+>)?[1-9]\d* exception"
            + @"|Errors in patch|error(s)? related to GameData/"
    );

    private static bool IsManagedAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private sealed record LoadedPlugin(string File, string? Problem);

    private sealed record PluginReport(List<LoadedPlugin> Loaded, List<string> ProvidedByGame);

    private sealed record PartReport(List<string> Loaded, List<string> Configs);

    // The url is the plugin's folder under GameData; the path can point elsewhere, because Retooled loads a relinked
    // copy. Retooled records the DLLs it skipped because the game provides them in ModAssemblies.skipDecisions.
    private static readonly LibrarySnippet PluginsSnippet = LibrarySnippet.Define(
        """
        var loaded = new List<object>();
        foreach (var entry in AssemblyLoader.loadedAssemblies)
        {
            string problem = null;
            if (entry.assembly == null)
            {
                problem = "not loaded";
            }
            else
            {
                try
                {
                    entry.assembly.GetTypes();
                }
                catch (System.Reflection.ReflectionTypeLoadException e)
                {
                    problem = e.LoaderExceptions.FirstOrDefault()?.Message ?? e.Message;
                }
            }

            string folder = string.IsNullOrEmpty(entry.url) ? "" : entry.url + "/";
            loaded.Add(new { File = folder + System.IO.Path.GetFileName(entry.path), Problem = problem });
        }

        var provided = new List<string>();
        var modAssemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("KSP.Retooled.Modding.ModAssemblies", false))
            .FirstOrDefault(type => type != null);
        var hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var decisions = modAssemblies?.GetField("skipDecisions", hidden)
            ?.GetValue(null) as IDictionary<string, bool>;
        if (decisions != null)
        {
            string gameData = System.IO.Path.GetFullPath(KSPUtil.ApplicationRootPath + "GameData");
            foreach (var pair in decisions.Where(pair => pair.Value))
            {
                string full = System.IO.Path.GetFullPath(pair.Key);
                if (full.StartsWith(gameData, StringComparison.OrdinalIgnoreCase))
                {
                    provided.Add(full.Substring(gameData.Length).TrimStart('/', '\\').Replace('\\', '/'));
                }
            }
        }

        return new { Loaded = loaded, ProvidedByGame = provided };
        """
    );

    // Part names in the loaded list have '.' where the config has '_'.
    private static readonly LibrarySnippet PartsSnippet = LibrarySnippet.Define(
        """
        var loaded = PartLoader.LoadedPartsList.Select(part => part.name).Distinct().ToList();
        var configs = GameDatabase.Instance.GetConfigs("PART")
            .Select(config => config.name.Replace('_', '.'))
            .Distinct()
            .ToList();
        return new { Loaded = loaded, Configs = configs };
        """
    );
}
