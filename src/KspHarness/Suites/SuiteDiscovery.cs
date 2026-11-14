using System.Reflection;

namespace KspHarness.Suites;

/// <summary>One test: a suite type and one of its <see cref="TestAttribute"/> methods.</summary>
public sealed record SuiteTest(Type Suite, MethodInfo Method, TestAttribute Attribute, Type Install)
{
    /// <summary><c>Suite.Test</c>, which <c>--filter</c> matches.</summary>
    public string FullName => $"{Suite.Name}.{Method.Name}";
}

/// <summary>Tests of one install definition, in the order they run.</summary>
public sealed record SuiteGroup(Type Install, IReadOnlyList<SuiteTest> Tests)
{
    public string Name => Install.Name;
}

public static class SuiteDiscovery
{
    /// <summary>
    /// Finds every test in the assembly, grouped by install definition. Groups and suites keep the order their types
    /// are declared in; tests keep source order, with tests a suite inherits before its own.
    /// </summary>
    public static IReadOnlyList<SuiteGroup> Find(Assembly assembly) => Find(assembly.GetTypes());

    internal static IReadOnlyList<SuiteGroup> Find(IEnumerable<Type> types)
    {
        var suites = types
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(KspSuite).IsAssignableFrom(type))
            .OrderBy(type => type.MetadataToken)
            .ToList();

        var tests = new List<SuiteTest>();
        foreach (var suite in suites)
        {
            var install = InstallOf(suite);
            foreach (var method in TestMethods(suite))
            {
                var attribute = method.GetCustomAttribute<TestAttribute>()!;
                Validate(suite, method);
                tests.Add(new SuiteTest(suite, method, attribute, install));
            }
        }

        return tests.GroupBy(test => test.Install).Select(group => new SuiteGroup(group.Key, group.ToList())).ToList();
    }

    private static Type InstallOf(Type suite)
    {
        for (var type = suite; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KspSuite<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        throw new InvalidOperationException($"{suite.FullName} derives from KspSuite but not from KspSuite<TInstall>");
    }

    // Reflection does not promise declaration order, but metadata tokens follow it within a type. An override that
    // repeats [Test] (to change its timeout) is the same test: it keeps the base declaration's place and its own
    // attribute.
    private static IEnumerable<MethodInfo> TestMethods(Type suite)
    {
        var chain = new List<Type>();
        for (var type = suite; type is not null && type != typeof(KspSuite); type = type.BaseType)
        {
            chain.Insert(0, type);
        }

        var tests = new List<MethodInfo>();
        var slots = new Dictionary<(Module, int), int>();
        foreach (var type in chain)
        {
            var declared = type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly
                )
                .Where(method => method.IsDefined(typeof(TestAttribute), inherit: false))
                .OrderBy(method => method.MetadataToken);

            foreach (var method in declared)
            {
                var original = method.GetBaseDefinition();
                var key = (original.Module, original.MetadataToken);
                if (slots.TryGetValue(key, out var slot))
                {
                    tests[slot] = method;
                }
                else
                {
                    slots[key] = tests.Count;
                    tests.Add(method);
                }
            }
        }

        return tests;
    }

    private static void Validate(Type suite, MethodInfo method)
    {
        if (
            !method.IsPublic
            || method.GetParameters().Length != 0
            || method.ReturnType != typeof(Task)
            || method.IsGenericMethodDefinition
        )
        {
            throw new InvalidOperationException(
                $"{suite.Name}.{method.Name}: a [Test] method must be public, take no parameters and return Task"
            );
        }

        if (suite.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException($"{suite.Name} needs a public parameterless constructor");
        }
    }
}
