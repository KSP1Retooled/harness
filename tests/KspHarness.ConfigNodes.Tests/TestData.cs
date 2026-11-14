namespace KspHarness.ConfigNodes.Tests;

/// <summary>Real KSP files these tests read, located by environment variables; the tests skip without them.</summary>
internal static class TestData
{
    /// <summary>The root of a stock KSP 1.12.5 install, the same variable the mod build uses. Only read.</summary>
    public const string KspDirVariable = "KSP_DIR";

    /// <summary>A ModuleManager.ConfigCache file to read.</summary>
    public const string ModuleManagerCacheVariable = "KSPHARNESS_TEST_MM_CACHE";

    public static string KspDir => Environment.GetEnvironmentVariable(KspDirVariable)!;

    public static string ModuleManagerCache => Environment.GetEnvironmentVariable(ModuleManagerCacheVariable)!;

    public static bool Has(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrEmpty(value) && (Directory.Exists(value) || File.Exists(value));
    }

    /// <summary>Whether two trees have the same names, values and children in the same order.</summary>
    public static bool SameTree(CfgNode a, CfgNode b)
    {
        if (a.Name != b.Name || a.ValueList.Count != b.ValueList.Count || a.Nodes.Count != b.Nodes.Count)
            return false;

        for (var i = 0; i < a.ValueList.Count; i++)
        {
            if (a.ValueList[i].Key != b.ValueList[i].Key || a.ValueList[i].Value != b.ValueList[i].Value)
                return false;
        }

        for (var i = 0; i < a.Nodes.Count; i++)
        {
            if (!SameTree(a.Nodes[i], b.Nodes[i]))
                return false;
        }

        return true;
    }
}

/// <summary>A fact that skips unless the environment variable names an existing file or folder.</summary>
public sealed class FactWithDataAttribute : FactAttribute
{
    public FactWithDataAttribute(string variable)
    {
        if (!TestData.Has(variable))
            Skip = $"set {variable} to run this test";
    }
}
