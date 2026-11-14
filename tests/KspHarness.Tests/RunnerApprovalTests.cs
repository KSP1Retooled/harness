using KspHarness.Approvals;
using KspHarness.Suites;
using KspHarness.Tests.Infrastructure;

namespace KspHarness.Tests;

/// <summary>The suite runner's approved-files option and the evidence it keeps of a failed approval.</summary>
public sealed class RunnerApprovalTests
{
    [Fact]
    public void Approved_option_names_the_folder()
    {
        var options = SuiteRunnerOptions.Parse(["--approved", "some/dir", "--filter", "x"]);

        Assert.Equal("some/dir", options.Approved);
        Assert.Null(SuiteRunnerOptions.Parse([]).Approved);
        Assert.Throws<ArgumentException>(() => SuiteRunnerOptions.Parse(["--approved"]));
        Assert.Contains("--approved", SuiteRunnerOptions.Usage);
    }

    [Fact]
    public void A_failed_approval_leaves_its_received_and_diff_files_in_the_evidence()
    {
        var dir = TestInstalls.TempDir("approval-evidence");
        var approved = Path.Combine(dir, "approved");
        var context = new ApprovalContext(approved, "Suite", "Test") { Approve = false };
        var approving = new ApprovalContext(approved, "Suite", "Test") { Approve = true };
        var evidence = new TestEvidence(Path.Combine(dir, "evidence"));

        Approve.Value("first", "text", approving);
        var textFailure = Assert.Throws<ApprovalException>(() => Approve.Value("second", "text", context));
        var image = new RgbaImage(4, 4);
        Approve.Screenshot(image.ToPng(), "shot", null, approving);
        image.Pixels[image.Offset(1, 1)] = 255;
        image.Pixels[image.Offset(1, 1) + 3] = 255;
        var shotFailure = Assert.Throws<ApprovalException>(() =>
            Approve.Screenshot(image.ToPng(), "shot", null, context)
        );

        // Wrapped the way a test body's failures arrive: one inside another, and several at once.
        ApprovalEvidence.Collect(
            evidence,
            new AggregateException(new InvalidOperationException("outer", textFailure), shotFailure)
        );

        Assert.Equal(
            [
                "Suite.Test.shot.approved.png",
                "Suite.Test.shot.diff.png",
                "Suite.Test.shot.received.png",
                "Suite.Test.text.approved.txt",
                "Suite.Test.text.received.txt",
            ],
            evidence.Files.Order(StringComparer.Ordinal)
        );
        Assert.Equal("second\n", File.ReadAllText(Path.Combine(evidence.Folder, "Suite.Test.text.received.txt")));
    }
}
