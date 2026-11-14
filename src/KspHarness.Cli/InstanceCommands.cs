using KspHarness.Installs;

namespace KspHarness.Cli;

/// <summary>
/// An instance described on the command line: a starting install plus CKAN modules (<c>id</c> or <c>id=version</c>).
/// The name names the instance folder and its lock file.
/// </summary>
public sealed class CommandLineInstall(string name, IReadOnlyList<string> ckan) : InstallDefinition
{
    public const string DefaultName = "Plain";

    public override string Name => name;

    public IReadOnlyList<string> CkanModules => ckan;

    public override void Configure(InstallBuilder install)
    {
        foreach (var module in ckan)
        {
            var equals = module.IndexOf('=');
            if (equals < 0)
            {
                install.Ckan(module);
            }
            else
            {
                install.Ckan(module[..equals], module[(equals + 1)..]);
            }
        }
    }
}

public static class InstanceCommands
{
    /// <summary>
    /// Builds (or reuses) the instance. Its lock file sits in <c>&lt;home&gt;/locks</c>, so building the same name and
    /// modules again reuses the instance without asking CKAN.
    /// </summary>
    public static Task<Instance> BuildAsync(
        string startDir,
        string? name,
        IReadOnlyList<string> ckan,
        HarnessHome home,
        Action<string>? log,
        CancellationToken cancellationToken = default
    )
    {
        name = string.IsNullOrWhiteSpace(name) ? CommandLineInstall.DefaultName : name.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.StartsWith('.'))
        {
            throw new UsageException($"'{name}' cannot name an instance folder");
        }

        var start = StartingInstall.Open(startDir);
        var lockFile = Path.Combine(home.Root, "locks", $"{name}.{start.Kind}.lock.json");
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        return InstanceBuilder.BuildAsync(
            new CommandLineInstall(name, ckan),
            start,
            home,
            lockFile,
            new InstanceBuildOptions { Log = log },
            cancellationToken
        );
    }

    /// <summary>The ready instances in the home, newest first.</summary>
    public static IReadOnlyList<Instance> List(HarnessHome home)
    {
        if (!Directory.Exists(home.InstancesDir))
        {
            return [];
        }

        return Directory
            .EnumerateDirectories(home.InstancesDir)
            .Where(dir => !Path.GetFileName(dir).StartsWith('.') && Instance.IsReady(dir))
            .Select(dir => Instance.Open(dir))
            .OrderByDescending(instance => instance.Record.CreatedUtc)
            .ToList();
    }
}
