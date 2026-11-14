using System.Reflection;
using System.Runtime.CompilerServices;

namespace KspHarness.Approvals;

/// <summary>
/// Where a test's approved files live and what they are called:
/// <c>&lt;Directory&gt;/&lt;Suite&gt;.&lt;Test&gt;.&lt;name&gt;.approved.txt</c> (or <c>.png</c>).
/// </summary>
public sealed class ApprovalContext
{
    /// <summary>
    /// The <c>AssemblyMetadata</c> key a test assembly uses to name its approved folder, normally the project's
    /// <c>approved</c> folder so that the files are committed with the tests.
    /// </summary>
    public const string DirectoryKey = "KspHarness.ApprovedDirectory";

    /// <summary>The environment variable that, set to <c>1</c>, accepts received values as the new approved ones.</summary>
    public const string ApproveVariable = "KSPHARNESS_APPROVE";

    public ApprovalContext(string directory, string suite, string test)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(suite);
        ArgumentException.ThrowIfNullOrEmpty(test);

        Directory = Path.GetFullPath(directory);
        Suite = suite;
        Test = test;
    }

    /// <summary>
    /// The approved folder a runner chose (a command-line option), which wins over the assembly's attribute. A runner
    /// sets it once before running tests.
    /// </summary>
    public static string? DirectoryOverride { get; set; }

    public string Directory { get; }

    public string Suite { get; }

    public string Test { get; }

    /// <summary>
    /// Whether a mismatch replaces the approved file instead of failing. Null follows <see cref="ApproveVariable"/>.
    /// </summary>
    public bool? Approve { get; init; }

    internal bool ApproveMode => Approve ?? Environment.GetEnvironmentVariable(ApproveVariable) is "1" or "true";

    /// <summary>
    /// The approved folder for tests in <paramref name="assembly"/>: <see cref="DirectoryOverride"/>, else the
    /// assembly's <c>[AssemblyMetadata("KspHarness.ApprovedDirectory", dir)]</c>, else <c>./approved</c>.
    /// </summary>
    public static string ResolveDirectory(Assembly assembly)
    {
        return DirectoryOverride
            ?? assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == DirectoryKey)
                ?.Value
            ?? Path.Combine(Environment.CurrentDirectory, "approved");
    }

    /// <summary>A context for a test method of <paramref name="suite"/>, named after the suite's type.</summary>
    public static ApprovalContext For(object suite, [CallerMemberName] string test = "")
    {
        var type = suite.GetType();
        return new ApprovalContext(ResolveDirectory(type.Assembly), type.Name, test);
    }

    internal string PathFor(string name, string kind, string extension)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"'{name}' cannot be part of a file name", nameof(name));
        }

        return Path.Combine(Directory, $"{Suite}.{Test}.{name}.{kind}{extension}");
    }
}
