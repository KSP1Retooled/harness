namespace KspHarness.Installs.Tests;

public sealed class LockFileTests
{
    [Fact]
    public void A_damaged_lock_file_names_itself_and_the_way_out()
    {
        using var dir = new TempDir();
        string path = dir.Write("Plain.stock.lock.json", "{ \"format\": 1, \"start\": \"st");

        var error = Assert.Throws<InstallException>(() => LockFile.Load(path));

        Assert.Contains(path, error.Message);
        Assert.Contains("LockMode.Update", error.Message);
    }

    [Fact]
    public void Save_replaces_the_file_and_leaves_no_temporary_files()
    {
        using var dir = new TempDir();
        string path = dir.Combine("locks", "Plain.stock.lock.json");
        var first = new LockFile { Start = "stock", Layers = [new LockLayer { Definition = "A" }] };
        var second = new LockFile { Start = "stock", Layers = [new LockLayer { Definition = "B" }] };

        first.Save(path);
        second.Save(path);

        Assert.Equal("B", LockFile.Load(path)!.Layers[0].Definition);
        Assert.Equal([path], Directory.GetFiles(dir.Combine("locks")));
    }
}
