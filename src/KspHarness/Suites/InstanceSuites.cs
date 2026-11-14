using System.Reflection;

namespace KspHarness.Suites;

/// <summary>
/// The suite types of one install, loaded into that game's <see cref="GameAssemblies.ContextFor"/>, so that game
/// types named in tests (and the typed results decoded into them) are the instance's own, whichever game another
/// install in the same run uses. Types the harness declares (base suites included) are shared with the runner.
/// </summary>
internal sealed class InstanceSuites
{
    private readonly string managedDir;

    public InstanceSuites(string managedDir)
    {
        this.managedDir = managedDir;
    }

    /// <summary>A new suite object for the test, of the instance's copy of the test's suite type.</summary>
    public (KspSuite Suite, MethodInfo Method) Create(SuiteTest test)
    {
        var type = Map(test.Suite);
        var suite = (KspSuite)Activator.CreateInstance(type)!;

        // Tests take no parameters, so the name finds the method; a virtual one dispatches as it would on the original.
        var method =
            type.GetMethod(test.Method.Name, BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes)
            ?? throw new MissingMethodException(type.FullName, test.Method.Name);
        return (suite, method);
    }

    private Type Map(Type suite)
    {
        if (suite.Assembly == typeof(KspSuite).Assembly || string.IsNullOrEmpty(suite.Assembly.Location))
        {
            return suite;
        }

        var copy = GameAssemblies.LoadInto(managedDir, suite.Assembly);
        return copy.GetType(suite.FullName!, throwOnError: true)!;
    }
}
