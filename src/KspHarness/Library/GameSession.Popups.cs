using KspHarness.Library;
using KspHarness.Snippets;

namespace KspHarness;

public sealed partial class GameSession
{
    private readonly Dictionary<string, bool> loadedAssemblies = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ClosedPopup> closedPopups = [];

    /// <summary>
    /// The policy that <see cref="WaitForMainMenuAsync"/> applies while the game loads, and that suites apply in their
    /// default reset. <see cref="PopupPolicy.None"/> turns it off.
    /// </summary>
    public PopupPolicy Popups { get; set; } = PopupPolicy.Default;

    /// <summary>Everything a popup policy closed or left open on this session, in order.</summary>
    public IReadOnlyList<ClosedPopup> ClosedPopups
    {
        get
        {
            lock (closedPopups)
            {
                return closedPopups.ToArray();
            }
        }
    }

    /// <summary>
    /// Closes the popups open now and those that open within <see cref="PopupPolicy.Settle"/>, by
    /// <paramref name="policy"/> (default: <see cref="Popups"/>), and returns what it closed or left open.
    /// </summary>
    public async Task<IReadOnlyList<ClosedPopup>> DismissDialogsAsync(
        PopupPolicy? policy = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default
    )
    {
        policy ??= Popups;
        var swept = await SweepAsync(policy, waitForMenu: false, timeout ?? TimeSpan.FromMinutes(1), cancellationToken);
        var closed = await RunClosersAsync(policy, cancellationToken);
        Record(closed);
        return [.. swept, .. closed];
    }

    // Waiting for the main menu is part of the sweep, so that a dialog that stops the loading screen is answered.
    private async Task<IReadOnlyList<ClosedPopup>> WaitForMainMenuSweepingAsync(
        TimeSpan? timeout,
        CancellationToken cancellationToken
    )
    {
        return await SweepAsync(Popups, waitForMenu: true, timeout ?? DefaultSceneTimeout, cancellationToken);
    }

    private async Task<IReadOnlyList<ClosedPopup>> SweepAsync(
        PopupPolicy policy,
        bool waitForMenu,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        var rules = policy.Dialogs.SelectMany(rule => new[] { rule.Dialog, rule.Button ?? "" }).ToArray();
        object?[] arguments =
        [
            rules,
            policy.DefaultButtons.ToArray(),
            policy.AnswerOthers,
            policy.AnswerOthers ? policy.CloseButtons.ToArray() : Array.Empty<string>(),
            waitForMenu,
            policy.Settle.TotalSeconds,
        ];

        var snippet = PopupSnippets.Sweep;
        var report = await RunWithArguments<string[]>(
            snippet.Source,
            arguments,
            RunOptions.Default with
            {
                RealTimeout = timeout,
            },
            cancellationToken,
            snippet.File,
            snippet.Line
        );

        var closed = new List<ClosedPopup>();
        for (var i = 0; i + 2 < report.Length; i += 3)
        {
            closed.Add(new ClosedPopup(report[i], report[i + 1], report[i + 2]));
        }

        Record(closed);
        return closed;
    }

    private async Task<IReadOnlyList<ClosedPopup>> RunClosersAsync(
        PopupPolicy policy,
        CancellationToken cancellationToken
    )
    {
        if (!policy.AnswerOthers || policy.Closers.Count == 0)
        {
            return [];
        }

        // A closer names the mod's types, so it compiles only where the mod is loaded; ask once per session.
        var unknown = policy
            .Closers.Select(closer => closer.Assembly)
            .Where(name => !loadedAssemblies.ContainsKey(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknown.Length > 0)
        {
            var snippet = PopupSnippets.LoadedAssemblies;
            var loaded = await RunWithArguments<string[]>(
                snippet.Source,
                [unknown],
                RunOptions.Default,
                cancellationToken,
                snippet.File,
                snippet.Line
            );
            foreach (var name in unknown)
            {
                loadedAssemblies[name] = loaded.Contains(name, StringComparer.OrdinalIgnoreCase);
            }
        }

        var closed = new List<ClosedPopup>();
        foreach (var closer in policy.Closers.Where(closer => loadedAssemblies[closer.Assembly]))
        {
            // Closers name members of one version of a mod, and they only back up the instance builder's answers,
            // so one that another version breaks is reported like a dialog the sweep could not answer.
            try
            {
                if (await Run<bool>(closer.Source, cancellationToken: cancellationToken, file: closer.Name, line: 1))
                {
                    closed.Add(new ClosedPopup("imgui", closer.Name, "closed"));
                }
            }
            catch (Exception e) when (e is SnippetCompileException or SnippetException)
            {
                var reason = e is SnippetCompileException compile ? compile.Errors[0].ToString() : e.Message;
                closed.Add(new ClosedPopup("imgui", closer.Name, "could not run: " + reason));
            }
        }

        return closed;
    }

    private void Record(IEnumerable<ClosedPopup> closed)
    {
        lock (closedPopups)
        {
            closedPopups.AddRange(closed);
        }
    }
}
