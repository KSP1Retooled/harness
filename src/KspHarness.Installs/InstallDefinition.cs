namespace KspHarness.Installs;

/// <summary>
/// Describes what an instance adds to a starting install. Subclasses need a public parameterless constructor so that
/// other definitions can include them.
/// </summary>
public abstract class InstallDefinition
{
    public abstract void Configure(InstallBuilder install);

    /// <summary>The instance folder prefix and the name shown in logs.</summary>
    public virtual string Name => GetType().Name;
}
