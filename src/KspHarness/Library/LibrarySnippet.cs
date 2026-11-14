using System.Runtime.CompilerServices;

namespace KspHarness.Library;

/// <summary>
/// A snippet the library itself runs, kept as source so that fixing one needs no mod build. Its position is where it
/// is defined, so compile errors and in-game stacks point into the library's source.
/// </summary>
internal sealed record LibrarySnippet(string Source, string File, int Line)
{
    public static LibrarySnippet Define(
        string source,
        [CallerFilePath] string file = "",
        [CallerLineNumber] int line = 0)
    {
        return new LibrarySnippet(source, file, line);
    }
}
