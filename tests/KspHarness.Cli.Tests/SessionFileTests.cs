using System.Diagnostics;

namespace KspHarness.Cli.Tests;

public sealed class SessionFileTests
{
    [Fact]
    public void Round_trips_through_the_instance_folder()
    {
        var folder = Directory.CreateTempSubdirectory("ksph-tests-").FullName;
        try
        {
            var session = new SessionFile
            {
                Pid = 1234,
                Port = 5555,
                Token = "00ff",
                Instance = folder,
                StartedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            };

            session.Write();
            session.Write();
            var read = SessionFile.Read(folder);

            Assert.Equal(session, read);
            Assert.True(File.Exists(Path.Combine(folder, ".harness", "session.json")));
            Assert.Contains("\"token\": \"00ff\"", File.ReadAllText(SessionFile.PathFor(folder)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_live_process_started_before_the_session_is_alive_and_a_reused_pid_is_not()
    {
        using var self = Process.GetCurrentProcess();
        var started = self.StartTime.ToUniversalTime();

        var current = new SessionFile { Pid = self.Id, Port = 1, Token = "", Instance = "x", StartedUtc = started };
        var reused = current with { StartedUtc = started.AddHours(-1) };

        Assert.True(current.IsAlive());
        Assert.False(reused.IsAlive());
    }

    [Fact]
    public void No_session_file_means_no_running_game()
    {
        var folder = Directory.CreateTempSubdirectory("ksph-tests-").FullName;
        try
        {
            Assert.Null(SessionFile.Read(folder));
            var error = Assert.Throws<InvalidOperationException>(() => GameControl.RunningSession(folder));
            Assert.Contains("ksph launch", error.Message);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ \"pid\": 12")]
    [InlineData("null")]
    public async Task A_session_file_that_does_not_parse_means_no_running_game(string content)
    {
        var folder = Directory.CreateTempSubdirectory("ksph-tests-").FullName;
        try
        {
            var path = SessionFile.PathFor(folder);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            var warnings = new List<string>();

            Assert.Null(SessionFile.Read(folder, warnings.Add));
            Assert.Contains(path, Assert.Single(warnings));

            Assert.False(await GameControl.QuitAsync(folder, cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Writing_replaces_the_file_and_leaves_nothing_beside_it()
    {
        var folder = Directory.CreateTempSubdirectory("ksph-tests-").FullName;
        try
        {
            var session = new SessionFile { Pid = 1, Port = 2, Token = "t", Instance = folder };
            session.Write();
            (session with { Pid = 3 }).Write();

            var path = SessionFile.PathFor(folder);
            Assert.Equal(3, SessionFile.Read(folder)!.Pid);
            Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Quitting_in_a_folder_that_does_not_exist_finds_no_game()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ksph-tests-missing-" + Guid.NewGuid().ToString("N"));

        Assert.False(await GameControl.QuitAsync(folder, cancellationToken: TestContext.Current.CancellationToken));
    }
}
