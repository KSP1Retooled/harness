using KspHarness.Approvals;

namespace KspHarness.Suites;

/// <summary>
/// Copies the files of failed approvals into a test's evidence folder, so a run's results hold what the game produced
/// and how it differed even after the received files next to the approved ones are overwritten or cleaned up.
/// </summary>
internal static class ApprovalEvidence
{
    /// <summary>Copies the received, diff and approved files of every <see cref="ApprovalException"/> in the failure.</summary>
    public static void Collect(TestEvidence evidence, Exception failure)
    {
        foreach (var approval in Approvals(failure))
        {
            Copy(evidence, approval.ReceivedPath);
            Copy(evidence, approval.DiffPath);
            Copy(evidence, approval.ApprovedPath);
        }
    }

    private static IEnumerable<ApprovalException> Approvals(Exception failure)
    {
        var pending = new Stack<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(failure);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            if (!seen.Add(e))
            {
                continue;
            }

            if (e is ApprovalException approval)
            {
                yield return approval;
            }

            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (e.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }

    private static void Copy(TestEvidence evidence, string? path)
    {
        if (path is null || !File.Exists(path) || evidence.Files.Any(file => string.Equals(
                Path.GetFileName(file), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        try
        {
            evidence.CopyFile(Path.GetFileName(path), path);
        }
        catch (IOException e)
        {
            evidence.Note($"could not keep {path}: {e.Message}");
        }
    }
}
