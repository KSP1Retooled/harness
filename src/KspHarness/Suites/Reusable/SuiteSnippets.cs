using KspHarness.Library;

namespace KspHarness.Suites.Reusable;

/// <summary>Runs the reusable suites' snippets, which are kept as source like the library's own.</summary>
internal static class SuiteSnippets
{
    public static Task<T> RunAsync<T>(
        this KspSuite suite,
        LibrarySnippet snippet,
        object?[] arguments,
        TimeSpan timeout
    )
    {
        var options = RunOptions.Default with { RealTimeout = timeout };
        return suite.Game.RunWithArguments<T>(
            snippet.Source,
            arguments,
            options,
            suite.Cancel,
            snippet.File,
            snippet.Line
        );
    }

    /// <summary>The first line of a log message, at most 200 characters.</summary>
    public static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].TrimEnd('\r');
        return line.Length > 200 ? line[..200] + "..." : line;
    }
}
