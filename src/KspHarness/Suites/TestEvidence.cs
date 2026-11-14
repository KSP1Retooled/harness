namespace KspHarness.Suites;

/// <summary>
/// What a test leaves behind: notes that go to the console and the run summary, and files under the test's folder in
/// the run (<c>&lt;home&gt;/runs/&lt;run id&gt;/&lt;suite&gt;/&lt;test&gt;/</c>). The folder is created on the first write.
/// </summary>
public sealed class TestEvidence
{
    private readonly List<string> notes = [];
    private readonly List<string> files = [];

    internal TestEvidence(string folder)
    {
        Folder = folder;
    }

    public string Folder { get; }

    public IReadOnlyList<string> Notes => notes;

    /// <summary>Files written so far, relative to <see cref="Folder"/>.</summary>
    public IReadOnlyList<string> Files => files;

    public void Note(string text)
    {
        notes.Add(text);
    }

    public string Write(string name, string text)
    {
        var path = Prepare(name);
        File.WriteAllText(path, text);
        return path;
    }

    public string Write(string name, byte[] bytes)
    {
        var path = Prepare(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public string CopyFile(string name, string source)
    {
        var path = Prepare(name);
        File.Copy(source, path, overwrite: true);
        return path;
    }

    /// <summary>Saves the game's next frame as a PNG in the evidence folder.</summary>
    public async Task<string> ScreenshotAsync(GameSession game, string name, CancellationToken cancellationToken)
    {
        var path = Prepare(name);
        await game.ScreenshotAsync(path, cancellationToken: cancellationToken);
        return path;
    }

    private string Prepare(string name)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, name);
        if (!files.Contains(name))
        {
            files.Add(name);
        }

        return path;
    }
}
