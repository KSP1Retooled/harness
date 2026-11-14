namespace KspHarness;

public sealed partial class GameSession
{
    private LiveDatabase? database;

    /// <summary>Queries over the game's live config database.</summary>
    public LiveDatabase Database => database ??= new LiveDatabase(this);

    /// <summary>
    /// Captures the next rendered frame, UI included, as PNG bytes, and writes them to <paramref name="path"/> on
    /// this machine when one is given. <paramref name="superSize"/> above 1 renders the scene's cameras again that
    /// much larger, without KSP's UI, and fails (a <see cref="SnippetException"/>) where that comes out black while
    /// the screen is not, as on loading screens.
    /// </summary>
    public async Task<byte[]> ScreenshotAsync(
        string? path = null,
        int superSize = 1,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(superSize, 1);

        var png = await Run<byte[]>(
            $"return await Screenshot.Capture({superSize});",
            cancellationToken: cancellationToken
        );
        if (path is not null)
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            // The path may be inside an instance, whose files can be hard links to a starting install
            File.Delete(path);
            await File.WriteAllBytesAsync(path, png, cancellationToken);
        }

        return png;
    }
}
