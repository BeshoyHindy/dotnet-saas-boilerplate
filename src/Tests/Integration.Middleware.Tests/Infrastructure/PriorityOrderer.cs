using Xunit.Abstractions;
using Xunit.Sdk;

namespace Integration.Middleware.Tests.Infrastructure;

/// <summary>
/// Marks the execution order of a test within its class. Lower runs first.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TestPriorityAttribute : Attribute
{
    public TestPriorityAttribute(int priority) => Priority = priority;

    public int Priority { get; }
}

/// <summary>
/// Orders test cases by <see cref="TestPriorityAttribute"/>, then by method name.
/// <para>
/// Needed where tests in one class share process-wide state that cannot be reset between
/// them — the auth rate limiter, for example, keeps one fixed window per client IP for the
/// lifetime of the host, so a test that deliberately exhausts the window must run after the
/// one that asserts requests inside it succeed. xUnit's default ordering is a hash of the
/// test-case unique ID (which embeds the assembly name), so it silently changes whenever the
/// assembly is renamed; this orderer makes the dependency explicit instead.
/// </para>
/// </summary>
public sealed class PriorityOrderer : ITestCaseOrderer
{
    public const string TypeName = "Integration.Middleware.Tests.Infrastructure.PriorityOrderer";
    public const string AssemblyName = "Boilerplate.Integration.Middleware.Tests";

    public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
        where TTestCase : ITestCase =>
        testCases
            .OrderBy(GetPriority)
            .ThenBy(tc => tc.TestMethod.Method.Name, StringComparer.Ordinal)
            .ToArray();

    private static int GetPriority<TTestCase>(TTestCase testCase)
        where TTestCase : ITestCase =>
        testCase.TestMethod.Method
            .GetCustomAttributes(typeof(TestPriorityAttribute).AssemblyQualifiedName)
            .Select(attr => attr.GetNamedArgument<int>(nameof(TestPriorityAttribute.Priority)))
            .DefaultIfEmpty(0)
            .First();
}
