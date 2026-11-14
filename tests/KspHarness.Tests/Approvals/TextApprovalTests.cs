using KspHarness.Approvals;
using KspHarness.ConfigNodes;

namespace KspHarness.Tests.Approvals;

public sealed class TextApprovalTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ksph-approvals-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private ApprovalContext Context(bool? approve = false) => new(folder, "Suite", "Test") { Approve = approve };

    private string Approved(string name) => Path.Combine(folder, $"Suite.Test.{name}.approved.txt");

    private string Received(string name) => Path.Combine(folder, $"Suite.Test.{name}.received.txt");

    [Fact]
    public void Objects_serialise_as_indented_json_with_sorted_keys()
    {
        var text = ApprovalText.Serialize(new { zeta = 1, alpha = new { b = true, a = "x" }, list = new[] { 1.5, 2 } });

        Assert.Equal(
            """
            {
              "alpha": {
                "a": "x",
                "b": true
              },
              "list": [
                1.5,
                2
              ],
              "zeta": 1
            }

            """.ReplaceLineEndings("\n"),
            text);
    }

    [Fact]
    public void Config_nodes_serialise_as_cfg_text()
    {
        var root = ConfigParser.Parse("PART\n{\n name = tank\n MODULE\n {\n  name = ModuleFoo\n }\n}\n");

        var text = ApprovalText.Serialize(root.GetNode("PART"));

        Assert.Equal(ConfigWriter.WriteNode(root.GetNode("PART")!), text);
        Assert.Contains("name = ModuleFoo", text);
    }

    [Fact]
    public void A_matching_value_passes_and_removes_an_old_received_file()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Approved("parts"), "{\r\n  \"a\": 1\r\n}\r\n");
        File.WriteAllText(Received("parts"), "stale");

        Approve.Value(new Dictionary<string, int> { ["a"] = 1 }, "parts", Context());

        Assert.False(File.Exists(Received("parts")));
    }

    [Fact]
    public void A_missing_approved_file_fails_and_writes_the_received_one()
    {
        var e = Assert.Throws<ApprovalException>(() => Approve.Value("hello", "greeting", Context()));

        Assert.Contains("no approved file", e.Message);
        Assert.Contains(ApprovalContext.ApproveVariable, e.Message);
        Assert.Equal(Received("greeting"), e.ReceivedPath);
        Assert.Equal("hello\n", File.ReadAllText(Received("greeting")));
        Assert.False(File.Exists(Approved("greeting")));
    }

    [Fact]
    public void A_mismatch_fails_with_a_unified_diff()
    {
        var lines = Enumerable.Range(0, 20).Select(i => $"line {i}").ToList();
        Directory.CreateDirectory(folder);
        File.WriteAllText(Approved("lines"), string.Join("\n", lines) + "\n");
        lines[10] = "changed";

        var e = Assert.Throws<ApprovalException>(() => Approve.Value(string.Join("\n", lines), "lines", Context()));

        Assert.Contains(
            """
            --- Suite.Test.lines.approved.txt
            +++ Suite.Test.lines.received.txt
            @@ -8,7 +8,7 @@
             line 7
             line 8
             line 9
            -line 10
            +changed
             line 11
             line 12
             line 13

            """.ReplaceLineEndings("\n"),
            e.Message);
        Assert.True(File.Exists(Received("lines")));
    }

    [Fact]
    public void Approve_mode_accepts_the_received_value()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Approved("value"), "old\n");
        File.WriteAllText(Received("value"), "old received\n");

        Approve.Value(new[] { "new" }, "value", Context(approve: true));

        Assert.Equal("[\n  \"new\"\n]\n", File.ReadAllText(Approved("value")));
        Assert.False(File.Exists(Received("value")));
        Approve.Value(new[] { "new" }, "value", Context());
    }

    [Fact]
    public void Diffs_show_separate_hunks_and_stop_at_the_line_limit()
    {
        var a = string.Join("\n", Enumerable.Range(0, 100).Select(i => $"{i}"));
        var b = string.Join("\n", Enumerable.Range(0, 100).Select(i => i is 5 or 50 ? "x" : $"{i}"));

        var diff = TextDiff.Unified(a, b);
        Assert.Equal(2, diff.Split('\n').Count(line => line.StartsWith("@@", StringComparison.Ordinal)));
        Assert.Contains("@@ -48,7 +48,7 @@", diff);

        var all = string.Join("\n", Enumerable.Range(0, 100).Select(i => $"other {i}"));
        Assert.Contains("more diff lines", TextDiff.Unified(a, all, maxLines: 10));
    }

    [Fact]
    public void The_directory_comes_from_the_override_then_the_assembly_attribute()
    {
        var fallback = ApprovalContext.ResolveDirectory(typeof(TextApprovalTests).Assembly);
        Assert.Equal(Path.Combine(Environment.CurrentDirectory, "approved"), fallback);

        // The only test that touches the static override, so it cannot race with another.
        ApprovalContext.DirectoryOverride = folder;
        try
        {
            var context = ApprovalContext.For(this);
            Assert.Equal(folder, context.Directory);
            Assert.Equal("TextApprovalTests", context.Suite);
            Assert.Equal(nameof(The_directory_comes_from_the_override_then_the_assembly_attribute), context.Test);
        }
        finally
        {
            ApprovalContext.DirectoryOverride = null;
        }
    }
}
