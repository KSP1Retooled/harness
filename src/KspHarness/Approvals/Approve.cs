namespace KspHarness.Approvals;

/// <summary>
/// Approved files: checks a value or screenshot against a file committed with the tests, for results too large to
/// write in the test. On a mismatch the received file is written next to the approved one and the check throws
/// <see cref="ApprovalException"/>; with <c>KSPHARNESS_APPROVE=1</c> the received file becomes the approved one.
/// </summary>
public static class Approve
{
    /// <summary>
    /// Compares <paramref name="value"/>, as text (<see cref="ApprovalText.Serialize"/>), with
    /// <c>&lt;Suite&gt;.&lt;Test&gt;.&lt;name&gt;.approved.txt</c>.
    /// </summary>
    public static void Value(object? value, string name, ApprovalContext ctx)
    {
        var approvedPath = ctx.PathFor(name, "approved", ".txt");
        var receivedPath = ctx.PathFor(name, "received", ".txt");
        var received = ApprovalText.Serialize(value);
        var approved = File.Exists(approvedPath) ? ApprovalText.Normalize(File.ReadAllText(approvedPath)) : null;

        if (approved == received)
        {
            File.Delete(receivedPath);
            return;
        }

        if (ctx.ApproveMode)
        {
            WriteText(approvedPath, received);
            File.Delete(receivedPath);
            return;
        }

        WriteText(receivedPath, received);
        if (approved is null)
        {
            throw Missing(name, approvedPath, receivedPath);
        }

        var diff = TextDiff.Unified(approved, received, Path.GetFileName(approvedPath), Path.GetFileName(receivedPath));
        throw new ApprovalException(
            $"'{name}' differs from its approved file; received {receivedPath}\n{diff}",
            approvedPath,
            receivedPath
        );
    }

    /// <summary>
    /// Compares a PNG screenshot with <c>&lt;Suite&gt;.&lt;Test&gt;.&lt;name&gt;.approved.png</c> within
    /// <paramref name="tolerance"/> (<see cref="ScreenshotTolerance.Default"/> when null). On a mismatch it also writes
    /// <c>.diff.png</c>, which marks the differing pixels in red.
    /// </summary>
    public static ImageComparison? Screenshot(
        byte[] png,
        string name,
        ScreenshotTolerance? tolerance,
        ApprovalContext ctx
    )
    {
        var approvedPath = ctx.PathFor(name, "approved", ".png");
        var receivedPath = ctx.PathFor(name, "received", ".png");
        var diffPath = ctx.PathFor(name, "diff", ".png");
        var received = RgbaImage.FromPng(png);

        if (!File.Exists(approvedPath))
        {
            if (ctx.ApproveMode)
            {
                WriteBytes(approvedPath, png);
                return null;
            }

            WriteBytes(receivedPath, png);
            throw Missing(name, approvedPath, receivedPath);
        }

        ImageComparison? comparison = null;
        string? incomparable = null;
        try
        {
            comparison = ImageComparison.Compare(
                RgbaImage.FromPng(File.ReadAllBytes(approvedPath)),
                received,
                tolerance
            );
        }
        catch (IncomparableImagesException e)
        {
            incomparable = e.Message;
        }

        if (comparison is { Matches: true } || ctx.ApproveMode)
        {
            if (comparison is not { Matches: true })
            {
                WriteBytes(approvedPath, png);
            }

            File.Delete(receivedPath);
            File.Delete(diffPath);
            return comparison;
        }

        WriteBytes(receivedPath, png);
        if (comparison is null)
        {
            File.Delete(diffPath);
            throw new ApprovalException(
                $"screenshot '{name}' cannot be compared: {incomparable}; received {receivedPath}",
                approvedPath,
                receivedPath
            );
        }

        WriteBytes(diffPath, comparison.Diff.ToPng());
        throw new ApprovalException(
            $"screenshot '{name}' differs from its approved image: {comparison.Describe()}; "
                + $"received {receivedPath}, diff {diffPath}",
            approvedPath,
            receivedPath,
            diffPath
        );
    }

    private static ApprovalException Missing(string name, string approvedPath, string receivedPath)
    {
        return new ApprovalException(
            $"'{name}' has no approved file yet; check {receivedPath} and rename it to {Path.GetFileName(approvedPath)}, "
                + $"or rerun with {ApprovalContext.ApproveVariable}=1",
            approvedPath,
            receivedPath
        );
    }

    private static void WriteText(string path, string text)
    {
        WriteBytes(path, System.Text.Encoding.UTF8.GetBytes(text));
    }

    private static void WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Delete(path);
        File.WriteAllBytes(path, bytes);
    }
}
