namespace KspHarness.Installs;

/// <summary>The folder that holds built instances and download caches.</summary>
public sealed class HarnessHome
{
    public const string EnvironmentVariable = "KSPHARNESS_HOME";

    public HarnessHome(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string InstancesDir => Path.Combine(Root, "instances");

    public string ArchiveCacheDir => Path.Combine(Root, "cache", "archives");

    /// <summary>CKAN logs of builds in progress, and of builds that failed after their folder was deleted.</summary>
    public string BuildLogsDir => Path.Combine(Root, "logs");

    /// <summary><c>KSPHARNESS_HOME</c>, or a <c>KspHarness</c> folder in the user's local application data.</summary>
    public static HarnessHome FromEnvironment()
    {
        string? configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new HarnessHome(configured);
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new HarnessHome(Path.Combine(local, "KspHarness"));
    }
}
