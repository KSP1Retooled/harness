using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using KspHarness.Script;

namespace KspHarness.Tests;

/// <summary>
/// The driver's script API stubs must offer what the mod offers, so that a lambda that compiles in a test also
/// compiles for the game. Compares type and member names and parameter counts; parameter types differ where the mod
/// names game types.
/// </summary>
public sealed class ScriptSurfaceTests
{
    private const string ScriptNamespace = "KspHarness.Script";

    [Fact]
    public void Stubs_match_the_mods_public_script_api()
    {
        var modDll = FindModDll();
        if (modDll is null)
        {
            Assert.Skip("build src/KspHarness.Mod to compare the stubs with the mod");
        }

        var mod = ModSurface(modDll!);
        var stubs = StubSurface();

        Assert.Equal(mod.Keys.Order(), stubs.Keys.Order());
        foreach (var (type, members) in mod)
        {
            Assert.True(
                members.SetEquals(stubs[type]),
                $"{type}: the mod has [{string.Join(", ", members.Order())}], the stubs have [{string.Join(", ", stubs[type].Order())}]");
        }
    }

    private static string? FindModDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KspHarness.slnx")))
        {
            dir = dir.Parent;
        }

        var bin = dir is null ? null : Path.Combine(dir.FullName, "src", "KspHarness.Mod", "bin");
        if (bin is null || !Directory.Exists(bin))
        {
            return null;
        }

        return Directory.EnumerateFiles(bin, "KspHarness.Mod.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static Dictionary<string, HashSet<string>> StubSurface()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return typeof(GameTime).Assembly.GetExportedTypes()
            .Where(t => t.Namespace == ScriptNamespace)
            .ToDictionary(
                t => t.Name,
                t => t.GetMethods(flags).Select(m => $"{m.Name}/{m.GetParameters().Length}")
                    .Concat(t.GetFields(flags).Select(f => f.Name))
                    .ToHashSet());
    }

    private static Dictionary<string, HashSet<string>> ModSurface(string path)
    {
        using var pe = new PEReader(File.OpenRead(path));
        var reader = pe.GetMetadataReader();
        var result = new Dictionary<string, HashSet<string>>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            var visibility = type.Attributes & TypeAttributes.VisibilityMask;
            if (visibility != TypeAttributes.Public || reader.GetString(type.Namespace) != ScriptNamespace)
            {
                continue;
            }

            var members = new HashSet<string>();
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if ((method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
                {
                    continue;
                }

                var name = reader.GetString(method.Name);
                if (name is ".ctor" or ".cctor")
                {
                    continue;
                }

                var parameters = method.GetParameters().Count(p => reader.GetParameter(p).SequenceNumber > 0);
                members.Add($"{name}/{parameters}");
            }

            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public)
                {
                    members.Add(reader.GetString(field.Name));
                }
            }

            result[reader.GetString(type.Name)] = members;
        }

        return result;
    }
}
