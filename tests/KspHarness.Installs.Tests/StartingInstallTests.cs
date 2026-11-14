namespace KspHarness.Installs.Tests;

public sealed class StartingInstallTests
{
    [Fact]
    public void Reads_a_stock_install()
    {
        using var dir = new TempDir();
        var start = StartingInstall.Open(FakeInstall.Create(dir));

        Assert.Equal(GamePlatform.Windows, start.Layout.Platform);
        Assert.Equal("03190", start.BuildId);
        Assert.Equal("1.12.5", start.KspVersion);
        Assert.False(start.IsRetooled);
        Assert.Equal("stock", start.Kind);
        Assert.Null(start.RetooledVersion);
        Assert.Equal("KSP_x64_Data", start.Layout.DataDirRelative);
    }

    [Fact]
    public void Detects_retooled()
    {
        using var dir = new TempDir();
        var start = StartingInstall.Open(FakeInstall.Create(dir, retooled: true));

        Assert.True(start.IsRetooled);
        Assert.Equal("retooled", start.Kind);
        Assert.Equal("1.13.0-rc8", start.RetooledVersion);
        Assert.Equal("1.13.0", start.KspVersion);
    }

    [Fact]
    public void Missing_pieces_are_named()
    {
        using var dir = new TempDir();
        string root = FakeInstall.Create(dir);
        File.Delete(Path.Combine(root, "KSP_x64_Data", "Managed", "Assembly-CSharp.dll"));

        var error = Assert.Throws<InstallException>(() => StartingInstall.Open(root));
        Assert.Contains("Assembly-CSharp.dll", error.Message);

        var empty = Assert.Throws<InstallException>(() => StartingInstall.Open(dir.Combine("nothing")));
        Assert.Contains("does not exist", empty.Message);
    }

    [Fact]
    public void Missing_build_id_is_an_error()
    {
        using var dir = new TempDir();
        string root = FakeInstall.Create(dir);
        File.Delete(Path.Combine(root, "buildID64.txt"));

        Assert.Throws<InstallException>(() => StartingInstall.Open(root));
    }

    [Fact]
    public void Identity_changes_when_key_files_change()
    {
        using var dir = new TempDir();
        string root = FakeInstall.Create(dir);
        string first = StartingInstall.Open(root).IdentityHash;
        Assert.Equal(first, StartingInstall.Open(root).IdentityHash);

        File.AppendAllText(Path.Combine(root, "KSP_x64_Data", "Managed", "Assembly-CSharp.dll"), "patched");
        string patched = StartingInstall.Open(root).IdentityHash;
        Assert.NotEqual(first, patched);

        Directory.CreateDirectory(Path.Combine(root, "GameData", "NewFolder"));
        Assert.NotEqual(patched, StartingInstall.Open(root).IdentityHash);
    }

    [Fact]
    public void Linux_layout_is_recognised()
    {
        using var dir = new TempDir();
        dir.Write("linux/KSP.x86_64", "elf");
        dir.Write("linux/KSP_Data/Managed/Assembly-CSharp.dll", "code");
        dir.Write("linux/GameData/Squad/a.cfg", "");
        dir.Write("linux/buildID64.txt", "build id = 03190\n");

        var start = StartingInstall.Open(dir.Combine("linux"));
        Assert.Equal(GamePlatform.Linux, start.Layout.Platform);
        Assert.EndsWith("KSP_Data" + Path.DirectorySeparatorChar + "Managed", start.Layout.ManagedDir);
    }
}
