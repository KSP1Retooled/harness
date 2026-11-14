using System.Reflection;
using System.Reflection.Emit;
using KspHarness.Installs;
using KspHarness.Library;
using KspHarness.Snippets;
using KspHarness.Suites;
using KspHarness.Suites.Reusable;
using KspHarness.Tests.Infrastructure;

namespace KspHarness.Tests;

public sealed class SuiteRunnerTests
{
    public sealed class SomeInstall : InstallDefinition
    {
        public override void Configure(InstallBuilder install) { }
    }

    public class BaseSuite : KspSuite<SomeInstall>
    {
        [Test]
        public virtual Task First() => Task.CompletedTask;

        [Test]
        public virtual Task Second() => Task.CompletedTask;
    }

    public sealed class LongerSuite : BaseSuite
    {
        [Test(TimeoutSeconds = 30)]
        public override Task First() => Task.CompletedTask;

        public override Task Second() => Task.CompletedTask;

        [Test]
        public Task Third() => Task.CompletedTask;
    }

    [Fact]
    public void An_override_that_repeats_Test_is_one_test_with_its_own_attribute()
    {
        var tests = SuiteDiscovery.Find([typeof(LongerSuite)]).Single().Tests;

        Assert.Equal(["First", "Second", "Third"], tests.Select(test => test.Method.Name));
        Assert.Equal(30, tests[0].Attribute.TimeoutSeconds);
        Assert.Equal(600, tests[1].Attribute.TimeoutSeconds);
    }

    [Fact]
    public async Task An_invalid_suite_exits_with_2()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("InvalidSuites"),
            AssemblyBuilderAccess.Run
        );
        var type = assembly
            .DefineDynamicModule("InvalidSuites")
            .DefineType(
                "PrivateTestSuite",
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
                typeof(KspSuite<SomeInstall>)
            );
        type.DefineDefaultConstructor(MethodAttributes.Public);
        var method = type.DefineMethod("Hidden", MethodAttributes.Private, typeof(Task), Type.EmptyTypes);
        method.SetCustomAttribute(
            new CustomAttributeBuilder(typeof(TestAttribute).GetConstructor(Type.EmptyTypes)!, [])
        );
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        type.CreateType();

        Assert.Equal(2, await SuiteRunner.RunAsync(["--list"], assembly));
    }

    [Fact]
    public void Runs_started_in_the_same_second_get_separate_folders()
    {
        var root = TestInstalls.TempDir("run-ids");

        var first = SuiteRunner.CreateRunDirectory(root, "20261112-120000-stock");
        var second = SuiteRunner.CreateRunDirectory(root, "20261112-120000-stock");

        Assert.Equal("20261112-120000-stock", first);
        Assert.Equal("20261112-120000-stock-2", second);
        Assert.True(Directory.Exists(Path.Combine(root, second)));
    }

    public static TheoryData<string, Type> ReusableSuites()
    {
        var data = new TheoryData<string, Type>();
        foreach (var install in new[] { TestInstalls.Stock, TestInstalls.Retooled })
        {
            data.Add(install, typeof(ToolbarSweepSuite<SomeInstall>));
            data.Add(install, typeof(BootSuite<SomeInstall>));
            data.Add(install, typeof(PartCatalogSuite<SomeInstall>));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ReusableSuites))]
    public void Reusable_suite_snippets_compile_against_the_game(string install, Type suite)
    {
        var compiler = TestInstalls.Compiler(install);
        var snippets = suite
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(LibrarySnippet))
            .ToList();
        Assert.NotEmpty(snippets);

        foreach (var field in snippets)
        {
            var snippet = (LibrarySnippet)field.GetValue(null)!;
            var prepared = SnippetBuilder.FromString(
                snippet.Source,
                returnsValue: true,
                RunOptions.Default.Usings,
                snippet.File,
                snippet.Line
            );

            var compiled = compiler.Compile(prepared.Source);

            Assert.NotEmpty(compiled.Assembly);
        }
    }
}
