using System.Reflection;
using System.Runtime.CompilerServices;

namespace KspHarness.GameTests;

internal static class GameAssemblyReferences
{
    // The tests reference the game's assemblies without copying them. Test methods that name game types must load
    // them to compile, even the ones that skip because no game is configured, so resolve them from where the build
    // found them.
    [ModuleInitializer]
    internal static void Register()
    {
        var managed = typeof(GameAssemblyReferences).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "KspManagedDir")?.Value;
        if (!string.IsNullOrEmpty(managed) && Directory.Exists(managed))
        {
            GameAssemblies.AddDirectory(managed);
        }
    }
}
