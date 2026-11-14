namespace KspHarness.Cli.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void Options_flags_and_positionals_parse_in_any_order()
    {
        var line = CommandLine.Parse(
            ["file.csx", "--instance", "C:/inst", "--windowed", "--name=Foo"],
            ["--instance", "--name"],
            ["--windowed"]);

        Assert.Equal("C:/inst", line.Value("--instance"));
        Assert.Equal("Foo", line.Value("--name"));
        Assert.True(line.Has("--windowed"));
        Assert.Equal(["file.csx"], line.Positionals);
    }

    [Fact]
    public void Short_option_takes_the_next_argument_even_when_it_looks_like_code()
    {
        var line = CommandLine.Parse(["-e", "return -1;"], ["-e"]);

        Assert.Equal("return -1;", line.Value("-e"));
    }

    [Fact]
    public void Double_dash_ends_options()
    {
        var line = CommandLine.Parse(["--", "--instance"], ["--instance"]);

        Assert.Null(line.Value("--instance"));
        Assert.Equal(["--instance"], line.Positionals);
    }

    [Theory]
    [InlineData(new[] { "--bogus" }, "unknown option --bogus")]
    [InlineData(new[] { "--instance" }, "--instance needs a value")]
    [InlineData(new[] { "--instance", "a", "--instance", "b" }, "--instance is given twice")]
    [InlineData(new[] { "--windowed=yes" }, "--windowed takes no value")]
    public void Bad_command_lines_are_usage_errors(string[] args, string message)
    {
        var error = Assert.Throws<UsageException>(() => CommandLine.Parse(args, ["--instance"], ["--windowed"]));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void Lists_split_on_commas_and_trim()
    {
        var line = CommandLine.Parse(["--ckan", "ModuleManager, KSPCommunityFixes=1.37.3"], ["--ckan"]);

        Assert.Equal(["ModuleManager", "KSPCommunityFixes=1.37.3"], line.List("--ckan"));
    }

    [Fact]
    public void Numbers_must_be_positive()
    {
        Assert.Equal(1280, CommandLine.Parse(["--width", "1280"], ["--width"]).Int("--width"));
        Assert.Throws<UsageException>(() => CommandLine.Parse(["--width", "wide"], ["--width"]).Int("--width"));
        Assert.Throws<UsageException>(() => CommandLine.Parse(["--width", "0"], ["--width"]).Int("--width"));
    }

    [Fact]
    public void Extra_positionals_are_rejected()
    {
        var error = Assert.Throws<UsageException>(() => CommandLine.Parse(["a", "b"]).AtMost(1));

        Assert.Equal("unexpected argument 'b'", error.Message);
    }

    [Fact]
    public void Instance_comes_from_the_option_or_the_one_positional()
    {
        Assert.Equal("dir", Program.InstanceArgument(CommandLine.Parse(["dir"], ["--instance"])));
        Assert.Equal("dir", Program.InstanceArgument(CommandLine.Parse(["--instance", "dir"], ["--instance"])));
        Assert.Throws<UsageException>(() => Program.InstanceArgument(CommandLine.Parse([], ["--instance"])));
        Assert.Throws<UsageException>(() => Program.InstanceArgument(CommandLine.Parse(["a", "--instance", "b"], ["--instance"])));
    }

    [Fact]
    public async Task Unknown_commands_are_usage_errors()
    {
        await Assert.ThrowsAsync<UsageException>(
            () => Program.RunAsync(["frobnicate"], TextReader.Null, TextWriter.Null, TextWriter.Null, CancellationToken.None));
    }

    [Fact]
    public void Launch_arguments_follow_the_spec()
    {
        var request = new LaunchRequest { InstanceDir = "x", Windowed = true, Width = 1280, Height = 720 };

        var arguments = GameControl.Arguments(request, 5000, "abc", "C:/log.txt");

        Assert.Equal(
            ["-ksp-harness-port", "5000", "-ksp-harness-token", "abc", "-screen-fullscreen", "0",
                "-screen-width", "1280", "-screen-height", "720", "-logFile", Path.GetFullPath("C:/log.txt")],
            arguments);
    }

    [Fact]
    public void Detached_launch_on_windows_uses_shell_execution()
    {
        var start = GameControl.StartInfo(@"C:\i\KSP_x64.exe", @"C:\i", ["-a", "b c"], windows: true);

        Assert.Equal(@"C:\i\KSP_x64.exe", start.FileName);
        Assert.True(start.UseShellExecute);
        Assert.Equal(@"C:\i", start.WorkingDirectory);
        Assert.Equal(["-a", "b c"], start.ArgumentList);
    }

    [Fact]
    public void Detached_launch_elsewhere_drops_the_standard_streams()
    {
        string[] arguments = ["-logFile", "/i/.harness/player $HOME.log", "-screen-width", "1280"];

        var start = GameControl.StartInfo("/i/KSP.app/Contents/MacOS/KSP", "/i", arguments, windows: false);

        Assert.Equal("/bin/sh", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal("/i", start.WorkingDirectory);
        Assert.Equal(
            ["-c", "exec \"$0\" \"$@\" </dev/null >/dev/null 2>&1", "/i/KSP.app/Contents/MacOS/KSP", .. arguments],
            start.ArgumentList);
    }

    [Fact]
    public void Detached_launch_passes_arguments_through_the_shell_unchanged()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        string script = Path.Combine(Path.GetTempPath(), $"ksph-args-{Guid.NewGuid():N}.sh");
        string output = script + ".out";
        File.WriteAllText(script, "#!/bin/sh\nfor a in \"$@\"; do printf '%s\\n' \"$a\"; done > \"$KSPH_ARGS_OUT\"\n");
        File.SetUnixFileMode(script, (UnixFileMode)Convert.ToInt32("755", 8));
        string[] arguments = ["-logFile", "/tmp/a b/$HOME 'x'.log", "*"];

        try
        {
            var start = GameControl.StartInfo(script, Path.GetTempPath(), arguments, windows: false);
            start.Environment["KSPH_ARGS_OUT"] = output;
            using var process = System.Diagnostics.Process.Start(start)!;
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(arguments, File.ReadAllLines(output));
        }
        finally
        {
            File.Delete(script);
            File.Delete(output);
        }
    }

    [Fact]
    public void The_menu_wait_gets_what_is_left_of_the_boot_timeout()
    {
        Assert.Equal(TimeSpan.FromMinutes(7), GameControl.Remaining(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(3)));
        Assert.Equal(TimeSpan.FromSeconds(1), GameControl.Remaining(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(11)));
    }
}
