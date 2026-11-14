using System.Collections.Concurrent;
using KspHarness.Installs;
using KspHarness.Snippets;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace KspHarness.Tests.Infrastructure;

/// <summary>
/// The starting installs the compile tests run against, from the environment: <c>KSP_DIR</c> for stock and
/// <c>KSPHARNESS_RETOOLED_DIR</c> for Retooled. Tests that need one skip when it is not set.
/// </summary>
internal static class TestInstalls
{
    public const string Stock = "stock";
    public const string Retooled = "retooled";

    private static readonly ConcurrentDictionary<string, Lazy<string>> StandIns = new();

    public static TheoryData<string> All => [Stock, Retooled];

    public static string Dir(string install)
    {
        var variable = install == Stock ? "KSP_DIR" : "KSPHARNESS_RETOOLED_DIR";
        var dir = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(dir) || GameLayout.FindByManagedDir(dir) is null)
        {
            Assert.Skip($"set {variable} to a KSP install to run the {install} compile tests");
        }

        return dir!;
    }

    public static SnippetCompiler Compiler(string install, string? cacheDirectory = null)
    {
        var dir = Dir(install);
        return new SnippetCompiler(ReferenceSet.FromInstance(dir, ModStandIn(install)), cacheDirectory);
    }

    /// <summary>
    /// The driver's script stubs compiled against the install's own base library, standing in for the mod's DLL, which
    /// builds separately and only against stock.
    /// </summary>
    public static string ModStandIn(string install)
    {
        var dir = Dir(install);
        return StandIns.GetOrAdd(install, _ => new Lazy<string>(() => BuildStandIn(dir, install))).Value;
    }

    public static string TempDir(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "test-temp", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string BuildStandIn(string installDir, string install)
    {
        var managed = GameLayout.FindByManagedDir(installDir)!.ManagedDir;
        var stubs = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "ScriptStubs"), "*.cs");
        var trees = stubs.Select(path =>
            CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Latest), path)
        );

        // Only the base library and the facades: the stubs name no game types.
        var references = new[] { "mscorlib.dll", "System.dll", "System.Core.dll", "netstandard.dll" }
            .Select(name => Path.Combine(managed, name))
            .Where(File.Exists)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create(
            "KspHarness.Mod",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var output = Path.Combine(AppContext.BaseDirectory, "mod-stand-in", install);
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, "KspHarness.Mod.dll");
        using (var stream = File.Create(path))
        {
            var result = compilation.Emit(stream);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    "the script stubs do not compile against the game's base library:\n"
                        + string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                );
            }
        }

        return path;
    }
}
