using System.Text;

namespace KspHarness.Approvals;

/// <summary>A line-based unified diff, short enough for an exception message.</summary>
public static class TextDiff
{
    // Above this many cells the middle of the two texts is shown as replaced rather than aligned line by line.
    private const long MaxTableCells = 16_000_000;

    public static string Unified(
        string expected,
        string actual,
        string expectedName = "approved",
        string actualName = "received",
        int context = 3,
        int maxLines = 60
    )
    {
        var a = Lines(expected);
        var b = Lines(actual);
        var ops = EditScript(a, b);

        var output = new StringBuilder();
        output.Append("--- ").Append(expectedName).Append('\n');
        output.Append("+++ ").Append(actualName).Append('\n');

        var lines = 0;
        var omitted = 0;
        foreach (var hunk in Hunks(ops, context))
        {
            var header = $"@@ -{hunk.AStart + 1},{hunk.ACount} +{hunk.BStart + 1},{hunk.BCount} @@";
            foreach (var line in hunk.Lines.Prepend(header))
            {
                if (lines < maxLines)
                {
                    output.Append(line).Append('\n');
                    lines++;
                }
                else
                {
                    omitted++;
                }
            }
        }

        if (omitted > 0)
        {
            output.Append($"... {omitted} more diff lines\n");
        }

        return output.ToString();
    }

    private static string[] Lines(string text)
    {
        var normalized = ApprovalText.Normalize(text);
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        return normalized.Length == 0 ? [] : normalized.Split('\n');
    }

    private enum OpKind
    {
        Equal,
        Delete,
        Insert,
    }

    private readonly record struct Op(OpKind Kind, int A, int B, string Text);

    private sealed record Hunk(int AStart, int ACount, int BStart, int BCount, List<string> Lines);

    private static List<Op> EditScript(string[] a, string[] b)
    {
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (
            suffix < a.Length - prefix
            && suffix < b.Length - prefix
            && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]
        )
        {
            suffix++;
        }

        var ops = new List<Op>();
        for (var i = 0; i < prefix; i++)
        {
            ops.Add(new Op(OpKind.Equal, i, i, a[i]));
        }

        var n = a.Length - prefix - suffix;
        var m = b.Length - prefix - suffix;
        if ((long)(n + 1) * (m + 1) <= MaxTableCells)
        {
            AlignMiddle(a, b, prefix, n, m, ops);
        }
        else
        {
            for (var i = 0; i < n; i++)
            {
                ops.Add(new Op(OpKind.Delete, prefix + i, prefix, a[prefix + i]));
            }

            for (var j = 0; j < m; j++)
            {
                ops.Add(new Op(OpKind.Insert, prefix + n, prefix + j, b[prefix + j]));
            }
        }

        for (var k = 0; k < suffix; k++)
        {
            ops.Add(new Op(OpKind.Equal, a.Length - suffix + k, b.Length - suffix + k, a[a.Length - suffix + k]));
        }

        return ops;
    }

    /// <summary>Longest common subsequence over the lines between the common prefix and suffix.</summary>
    private static void AlignMiddle(string[] a, string[] b, int offset, int n, int m, List<Op> ops)
    {
        var table = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                table[i, j] =
                    a[offset + i] == b[offset + j]
                        ? table[i + 1, j + 1] + 1
                        : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        int x = 0,
            y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[offset + x] == b[offset + y])
            {
                ops.Add(new Op(OpKind.Equal, offset + x, offset + y, a[offset + x]));
                x++;
                y++;
            }
            // Deletions first where both are equally good, so a changed line reads as - then +.
            else if (x < n && (y == m || table[x + 1, y] >= table[x, y + 1]))
            {
                ops.Add(new Op(OpKind.Delete, offset + x, offset + y, a[offset + x]));
                x++;
            }
            else
            {
                ops.Add(new Op(OpKind.Insert, offset + x, offset + y, b[offset + y]));
                y++;
            }
        }
    }

    private static IEnumerable<Hunk> Hunks(List<Op> ops, int context)
    {
        var index = 0;
        while (true)
        {
            var firstChange = ops.FindIndex(index, op => op.Kind != OpKind.Equal);
            if (firstChange < 0)
            {
                yield break;
            }

            var start = Math.Max(index, firstChange - context);

            // A hunk runs on while the next change is within two contexts of the last one.
            var end = firstChange;
            var lastChange = firstChange;
            while (end < ops.Count)
            {
                if (ops[end].Kind != OpKind.Equal)
                {
                    lastChange = end;
                }
                else if (end - lastChange > 2 * context)
                {
                    break;
                }

                end++;
            }

            end = Math.Min(ops.Count, lastChange + context + 1);

            var lines = new List<string>();
            int aCount = 0,
                bCount = 0;
            for (var i = start; i < end; i++)
            {
                var op = ops[i];
                switch (op.Kind)
                {
                    case OpKind.Equal:
                        lines.Add(" " + op.Text);
                        aCount++;
                        bCount++;
                        break;
                    case OpKind.Delete:
                        lines.Add("-" + op.Text);
                        aCount++;
                        break;
                    default:
                        lines.Add("+" + op.Text);
                        bCount++;
                        break;
                }
            }

            yield return new Hunk(ops[start].A, aCount, ops[start].B, bCount, lines);
            index = end;
        }
    }
}
