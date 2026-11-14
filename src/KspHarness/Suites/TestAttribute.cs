namespace KspHarness.Suites;

/// <summary>
/// Marks a public instance method of a <see cref="KspSuite{TInstall}"/> as a test. The method takes no parameters and
/// returns <see cref="Task"/>; it passes when it completes and fails when it throws.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class TestAttribute : Attribute
{
    /// <summary>
    /// Starts a new game process for this test, for checks that only hold right after boot (the first dialogs, the
    /// boot log). The first test on a freshly started game does not start another.
    /// </summary>
    public bool FreshGame { get; init; }

    /// <summary>Real time the reset and the test may take together before the test fails and the game restarts.</summary>
    public int TimeoutSeconds { get; init; } = 600;
}
