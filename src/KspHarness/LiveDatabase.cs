using KspHarness.ConfigNodes;
using KspHarness.Library;

namespace KspHarness;

/// <summary>
/// Queries the game's live <c>GameDatabase</c> (after ModuleManager has patched it) in the config query language
/// of <c>KspHarness.ConfigNodes</c>, with the top-level configs as the root list:
/// <c>await game.Database.Values&lt;int&gt;("PART[mk1pod_v2]/MODULE[ModuleCommand]/minimumCrew")</c>.
/// </summary>
public sealed class LiveDatabase
{
    private readonly GameSession session;

    internal LiveDatabase(GameSession session)
    {
        this.session = session;
    }

    /// <summary>Copies of the matching nodes.</summary>
    public async Task<List<CfgNode>> Nodes(string query, CancellationToken cancellationToken = default)
    {
        var texts = await RunAsync(LibrarySnippets.DatabaseQuery, query, cancellationToken);
        return texts.Select(text => ConfigParser.Parse(text).Nodes.Single()).ToList();
    }

    /// <summary>The matching values, parsed as <see cref="ValueParser"/> reads KSP's text forms.</summary>
    public async Task<List<T>> Values<T>(string query, CancellationToken cancellationToken = default)
    {
        var texts = await RunAsync(LibrarySnippets.DatabaseValues, query, cancellationToken);
        return texts.Select(ValueParser.Parse<T>).ToList();
    }

    private Task<List<string>> RunAsync(LibrarySnippet snippet, string query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return session.RunWithArguments<List<string>>(
            snippet.Source,
            [query],
            RunOptions.Default,
            cancellationToken,
            snippet.File,
            snippet.Line
        );
    }
}
