using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using KspHarness.Installs;
using KspHarness.Library;

namespace KspHarness.Suites.Reusable;

/// <summary>
/// Picks every part from the editor's catalog the way a player does (<c>EditorLogic.SpawnPart</c>, one part held on
/// the cursor at a time, over a root part), checks that its modules started without an error logged, then deletes
/// it. A mod set opts in by deriving: <c>public sealed class FoundationParts : PartCatalogSuite&lt;Foundation&gt;;</c>
/// </summary>
public abstract class PartCatalogSuite<TInstall> : KspSuite<TInstall>
    where TInstall : InstallDefinition, new()
{
    /// <summary>
    /// A regular expression over part names (<c>AvailablePart.name</c>) choosing the parts to check, or null for every
    /// part the editor lists (all categories but <c>none</c>, hidden parts included).
    /// </summary>
    protected virtual string? PartFilter => null;

    /// <summary>
    /// Regular expressions for errors that are known and accepted; an error whose message matches one is noted in the
    /// table instead of failing the part.
    /// </summary>
    protected virtual IReadOnlyList<string> KnownPartErrors => [];

    /// <summary>The part each check starts from as the root of an otherwise empty craft.</summary>
    protected virtual string RootPart => "probeCoreOcto.v2";

    /// <summary>Parts per snippet run; each part takes one frame.</summary>
    protected virtual int BatchSize => 100;

    /// <summary>Starts a sandbox game and opens the VAB with an empty craft.</summary>
    public override async Task ResetAsync()
    {
        await Game.ReturnToMainMenuAsync(cancellationToken: Cancel);
        await Game.StartSandboxAsync("ksph-part-catalog", cancellationToken: Cancel);
        await this.RunAsync<object?>(OpenEditorSnippet, ["VAB"], TimeSpan.FromMinutes(5));
    }

    [Test(TimeoutSeconds = 1800)]
    public async Task Every_part_spawns_in_the_editor()
    {
        var names = await this.RunAsync<List<string>>(PartNamesSnippet, [], TimeSpan.FromMinutes(1));
        if (PartFilter is { } filter)
        {
            var regex = new Regex(filter);
            names = names.Where(name => regex.IsMatch(name)).ToList();
        }

        Check.True(names.Count > 0, "no parts to check");
        var known = KnownPartErrors.Select(pattern => new Regex(pattern)).ToList();
        var clock = Stopwatch.StartNew();
        var results = new List<PartCheck>();
        var cursor = Game.Log.Cursor;
        foreach (var batch in names.Chunk(BatchSize))
        {
            results.AddRange(
                await this.RunAsync<List<PartCheck>>(SpawnSnippet, [batch, RootPart], TimeSpan.FromMinutes(5))
            );
        }

        var seconds = clock.Elapsed.TotalSeconds;
        var failures = results
            .Select(result => (Result: result, Problems: Problems(result, known)))
            .Where(item => item.Problems.Count > 0)
            .ToList();

        // The snippet hears every message the game logs while a part is held; the driver's log adds which mods are on
        // each stack, for the report.
        var modErrors = ModErrors(cursor);
        Evidence.Note(
            $"{results.Count} parts in {seconds:0.0} s ({results.Count / Math.Max(seconds, 0.001):0} per second), "
                + $"{failures.Count} with problems, {modErrors.Count} errors through mod code"
        );
        Evidence.Write("parts.txt", Table(results, known));
        if (modErrors.Count > 0)
        {
            Evidence.Write("mod-errors.txt", string.Join("\n\n", modErrors.Select(e => $"{e}\n{e.Stack}")));
        }

        Check.Empty(
            failures.Select(item => $"{item.Result.Name} ({item.Result.Folder}): {string.Join("; ", item.Problems)}"),
            "parts that failed in the editor"
        );
    }

    private static List<string> Problems(PartCheck result, IReadOnlyList<Regex> known)
    {
        var problems = new List<string>();
        if (result.Failure is not null)
        {
            problems.Add(result.Failure);
        }
        else if (!result.Started)
        {
            problems.Add("its modules did not finish starting (Part.editorStarted is false)");
        }

        problems.AddRange(
            result.Errors.Where(error => !known.Any(regex => regex.IsMatch(error))).Select(SuiteSnippets.FirstLine)
        );
        return problems;
    }

    private static string Table(IEnumerable<PartCheck> results, IReadOnlyList<Regex> known)
    {
        var text = new StringBuilder();
        text.AppendLine("part\tfolder\tmodules\tstarted\tproblems");
        foreach (var result in results)
        {
            var problems = Problems(result, known);
            var accepted = result.Errors.Count(error => known.Any(regex => regex.IsMatch(error)));
            text.Append(result.Name)
                .Append('\t')
                .Append(result.Folder)
                .Append('\t')
                .Append(result.Modules)
                .Append('\t')
                .Append(result.Started ? "yes" : "no")
                .Append('\t')
                .Append(problems.Count == 0 ? "ok" : string.Join(" | ", problems))
                .Append(accepted > 0 ? $" ({accepted} known errors)" : "")
                .AppendLine();
        }

        return text.ToString();
    }

    private sealed record PartCheck(
        string Name,
        string Folder,
        int Modules,
        bool Started,
        string? Failure,
        List<string> Errors
    );

    private static readonly LibrarySnippet OpenEditorSnippet = LibrarySnippet.Define(
        """
        var facility = Args.Get<string>(__args, 0) == "SPH" ? EditorFacility.SPH : EditorFacility.VAB;
        EditorDriver.StartEditor(facility);
        await Wait.Scene(GameScenes.EDITOR);
        await Wait.Until(() => EditorLogic.fetch != null && EditorLogic.fetch.fsm != null && EditorLogic.fetch.fsm.Started);
        return null;
        """
    );

    private static readonly LibrarySnippet PartNamesSnippet = LibrarySnippet.Define(
        """
        return PartLoader.LoadedPartsList
            .Where(part => part.category != PartCategories.none && part.partPrefab != null)
            .Select(part => part.name)
            .ToList();
        """
    );

    // Each part is picked up at the end of a frame, so that Unity runs its Start (and with it every module's OnStart)
    // before the next Update, where it is checked and deleted; its OnDestroy has run by the end of that frame, so every
    // message logged in between belongs to that part. The root keeps SpawnPart on its "part from the catalog" path.
    private static readonly LibrarySnippet SpawnSnippet = LibrarySnippet.Define(
        """
        string[] names = Args.Get<string[]>(__args, 0);
        string rootName = Args.Get<string>(__args, 1);
        var editor = EditorLogic.fetch;
        if (HighLogic.LoadedScene != GameScenes.EDITOR || editor == null)
        {
            throw new InvalidOperationException(
                "the part catalog needs the editor, but the game is in " + HighLogic.LoadedScene);
        }

        if (EditorLogic.SelectedPart != null)
        {
            editor.DestroySelectedPart();
        }

        if (editor.ship.parts.Count == 0)
        {
            var root = PartLoader.getPartInfoByName(rootName) ?? PartLoader.getPartInfoByName(names[0]);
            editor.SpawnPart(root);
            await Wait.Frames(2);
        }

        var results = new List<object>();
        List<string> errors = null;
        Application.LogCallback listener = (message, stack, type) =>
        {
            if (errors != null && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert))
            {
                string frame = (stack ?? "").Split('\n').FirstOrDefault(line => line.Trim().Length > 0) ?? "";
                errors.Add(type + ": " + message + (frame.Length > 0 ? " at " + frame.Trim() : ""));
            }
        };

        Application.logMessageReceived += listener;
        try
        {
            await Tick.EndOfFrame;
            foreach (string name in names)
            {
                errors = new List<string>();
                var info = PartLoader.getPartInfoByName(name);
                string folder = info?.partUrl == null ? "" : info.partUrl.Split('/')[0];
                string failure = null;
                Part part = null;
                if (info == null)
                {
                    failure = "no such part";
                }
                else
                {
                    try
                    {
                        editor.SpawnPart(info);
                        part = EditorLogic.SelectedPart;
                        if (part == null)
                        {
                            failure = "SpawnPart left no part on the cursor (editor state " + editor.fsm.currentStateName + ")";
                        }
                    }
                    catch (Exception e)
                    {
                        failure = "SpawnPart threw " + e.GetType().Name + ": " + e.Message;
                    }
                }

                await Tick.Update;
                bool started = part != null && part.editorStarted;
                int modules = part != null ? part.Modules.Count : 0;
                try
                {
                    if (EditorLogic.SelectedPart != null)
                    {
                        editor.DestroySelectedPart();
                    }
                    else if (part != null)
                    {
                        Object.Destroy(part.gameObject);
                    }
                }
                catch (Exception e)
                {
                    failure = (failure == null ? "" : failure + "; ")
                        + "deleting it threw " + e.GetType().Name + ": " + e.Message;
                }

                await Tick.EndOfFrame;
                results.Add(new
                {
                    Name = name,
                    Folder = folder,
                    Modules = modules,
                    Started = started,
                    Failure = failure,
                    Errors = errors,
                });
                errors = null;
            }
        }
        finally
        {
            Application.logMessageReceived -= listener;
        }

        return results;
        """
    );
}
