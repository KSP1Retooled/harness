namespace KspHarness.Installs.Tests;

public sealed class ArchiveCacheTests
{
    [Theory]
    [InlineData(new[] { "GameData/Mod/a.cfg", "README.md" }, "GameData/")]
    [InlineData(new[] { "Mod-1.0/GameData/Mod/a.cfg", "Mod-1.0/README.md" }, "Mod-1.0/GameData/")]
    [InlineData(new[] { "Mod/a.cfg", "Mod/b.cfg" }, "")]
    [InlineData(new[] { "a.dll" }, "")]
    [InlineData(new[] { "One/GameData/a.cfg", "Two/x.cfg" }, "")]
    public void Finds_the_GameData_folder_CKAN_would_install(string[] paths, string expected)
    {
        Assert.Equal(expected, ArchiveCache.FindGameDataPrefix(paths));
    }

    [Fact]
    public void Unpacks_only_GameData_content()
    {
        using var dir = new TempDir();
        string zip = Zips.Create(dir.Combine("mod.zip"), ("GameData/Mod/a.cfg", "A"), ("README.md", "readme"));

        int count = ArchiveCache.Unpack(zip, dir.Combine("GameData"));

        Assert.Equal(1, count);
        Assert.Equal("A", File.ReadAllText(dir.Combine("GameData", "Mod", "a.cfg")));
        Assert.False(File.Exists(dir.Combine("GameData", "README.md")));
    }

    [Fact]
    public void Rejects_entries_that_leave_the_folder()
    {
        using var dir = new TempDir();
        string zip = Zips.Create(dir.Combine("evil.zip"), ("../outside.txt", "x"));

        Assert.Throws<InvalidDataException>(() => ArchiveCache.Unpack(zip, dir.Combine("GameData")));
        Assert.False(File.Exists(dir.Combine("outside.txt")));
    }

    [Fact]
    public async Task Downloads_once_and_verifies()
    {
        using var dir = new TempDir();
        string zip = Zips.Create(dir.Combine("mod.zip"), ("Mod/a.cfg", "A"));
        string sha = await ArchiveCache.HashFileAsync(zip, TestContext.Current.CancellationToken);
        var cache = new ArchiveCache(dir.Combine("cache"));
        string url = new Uri(zip).AbsoluteUri;

        string cached = await cache.GetAsync(url, sha, TestContext.Current.CancellationToken);
        File.Delete(zip);
        string again = await cache.GetAsync(url, sha, TestContext.Current.CancellationToken);

        Assert.Equal(cached, again);
        Assert.Equal(sha, Path.GetFileName(cached));
    }

    [Fact]
    public async Task Hash_mismatch_fails_and_caches_nothing()
    {
        using var dir = new TempDir();
        string zip = Zips.Create(dir.Combine("mod.zip"), ("Mod/a.cfg", "A"));
        var cache = new ArchiveCache(dir.Combine("cache"));

        await Assert.ThrowsAsync<InstallException>(() =>
            cache.GetAsync(new Uri(zip).AbsoluteUri, new string('0', 64), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(dir.Combine("cache")));
    }

    [Fact]
    public async Task A_damaged_cached_archive_is_downloaded_again()
    {
        using var dir = new TempDir();
        string zip = Zips.Create(dir.Combine("mod.zip"), ("Mod/a.cfg", "A"));
        string sha = await ArchiveCache.HashFileAsync(zip, TestContext.Current.CancellationToken);
        var cache = new ArchiveCache(dir.Combine("cache"));
        string url = new Uri(zip).AbsoluteUri;

        string cached = await cache.GetAsync(url, sha, TestContext.Current.CancellationToken);
        File.WriteAllText(cached, "truncated");
        string again = await cache.GetAsync(url, sha, TestContext.Current.CancellationToken);

        Assert.Equal(cached, again);
        Assert.Equal(sha, await ArchiveCache.HashFileAsync(again, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_downloads_of_one_archive_all_succeed()
    {
        using var dir = new TempDir();
        string zip = Zips.Create(dir.Combine("mod.zip"), ("Mod/a.cfg", new string('A', 1 << 20)));
        string sha = await ArchiveCache.HashFileAsync(zip, TestContext.Current.CancellationToken);
        string url = new Uri(zip).AbsoluteUri;

        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            new ArchiveCache(dir.Combine("cache")).GetAsync(url, sha, TestContext.Current.CancellationToken))));

        Assert.All(paths, path => Assert.Equal(sha, Path.GetFileName(path)));
        Assert.Single(Directory.GetFiles(dir.Combine("cache")));
    }
}
