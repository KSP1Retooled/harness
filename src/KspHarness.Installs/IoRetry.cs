namespace KspHarness.Installs;

/// <summary>
/// Retries renames and deletes of folders that were just written. Defender, the Search indexer or a child process
/// that has just exited can hold a handle inside such a tree for a moment, and the rename or delete then fails with a
/// sharing violation that is gone a second later.
/// </summary>
internal static class IoRetry
{
    private const int Attempts = 10;

    /// <summary>
    /// Runs <paramref name="action"/> until it succeeds, retrying sharing errors for about five seconds. When
    /// <paramref name="settled"/> returns true before an attempt or after a failed one, the action is no longer needed
    /// and the method returns false without running it again.
    /// </summary>
    public static bool Run(Action action, Func<bool>? settled = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            if (settled?.Invoke() == true)
            {
                return false;
            }

            try
            {
                action();
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (settled?.Invoke() == true)
                {
                    return false;
                }

                if (attempt == Attempts)
                {
                    throw;
                }
            }

            Thread.Sleep(Math.Min(50 << (attempt - 1), 1000));
        }
    }

    public static void DeleteTree(string path)
    {
        Run(() =>
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        });
    }
}
