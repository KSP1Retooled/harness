namespace KspHarness.Suites;

/// <summary>A failed expectation in a test.</summary>
public sealed class CheckFailedException : Exception
{
    public CheckFailedException(string message)
        : base(message)
    {
    }
}

/// <summary>The few assertions a suite needs; anything else that throws fails a test just as well.</summary>
public static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new CheckFailedException(message);
        }
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new CheckFailedException($"{what}: expected {expected}, got {actual}");
        }
    }

    /// <summary>Fails listing the items (at most 20) when there are any.</summary>
    public static void Empty<T>(IEnumerable<T> items, string what)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        var shown = string.Join(Environment.NewLine, list.Take(20).Select(item => "  " + item));
        var more = list.Count > 20 ? $"{Environment.NewLine}  ... and {list.Count - 20} more" : "";
        throw new CheckFailedException($"{what}: {list.Count}{Environment.NewLine}{shown}{more}");
    }
}
