using Shouldly;
using System.Xml.Linq;
using Xunit;

namespace Architecture.Tests;

/// <summary>
/// Tests to detect circular project references in the solution.
/// Circular references cause build issues and indicate architectural problems.
/// </summary>
public class CircularReferenceTests
{
    private static readonly string SolutionRoot = ModuleArchitectureTestsFixture.SolutionRoot;

    private const string ModuleProjectPrefix = "Boilerplate.Modules.";
    private const string ContractsSuffix = ".Contracts";

    // Module cycles that exist today. Each entry names one cycle by the set of modules it runs through,
    // sorted and joined with " <-> " (the canonical form the detector reports), so an entry can never
    // cover a cycle it was not written for. These are tracked defects, not sanctioned design: the test
    // fails when an entry stops being a real cycle, so a fix cannot leave a stale exemption behind.
    private static readonly string[] KnownModuleCycles = [
        // Multitenancy -> Billing.Contracts and Billing -> Multitenancy.Contracts.
        // Tracked by issue #5 "Remove Billing, Quota and Webhooks and break the Multitenancy-Billing cycle".
        "Billing <-> Multitenancy",

        // Identity -> Auditing.Contracts and Auditing -> Identity.Contracts.
        // Tracked by issue #33 "Decide the fate of the Auditing-Identity module cycle".
        "Auditing <-> Identity"
    ];

    [Fact]
    public void Solution_Should_Not_Have_Circular_Project_References()
    {
        string srcRoot = Path.Combine(SolutionRoot, "src");

        // Build the dependency graph
        var projectPaths = Directory
            .GetFiles(srcRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains("obj", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var dependencyGraph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectPath in projectPaths)
        {
            string projectName = Path.GetFileNameWithoutExtension(projectPath);
            var dependencies = GetProjectReferences(projectPath);
            dependencyGraph[projectName] = dependencies;
        }

        // Detect cycles using DFS
        var cycles = DetectCycles(dependencyGraph);

        cycles.ShouldBeEmpty(
            $"Circular project references detected: {string.Join("; ", cycles)}");
    }

    [Fact]
    public void Modules_Should_Not_Have_Circular_Dependencies()
    {
        string modulesRoot = Path.Combine(SolutionRoot, "src", "Modules");

        if (!Directory.Exists(modulesRoot))
        {
            return;
        }

        var moduleProjects = Directory
            .GetFiles(modulesRoot, "Boilerplate.Modules.*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains("obj", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        // Key the graph by owning module rather than by csproj: Boilerplate.Modules.X and
        // Boilerplate.Modules.X.Contracts are both module X. Per-csproj nodes can never go red — a
        // ProjectReference cycle does not even build (MSB4006) — so the only module cycles that can
        // exist are the ones that run through a Contracts project, and those need collapsed nodes.
        var dependencyGraph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectPath in moduleProjects)
        {
            string module = OwningModule(Path.GetFileNameWithoutExtension(projectPath));

            if (!dependencyGraph.TryGetValue(module, out var dependencies))
            {
                dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                dependencyGraph[module] = dependencies;
            }

            var moduleDependencies = GetProjectReferences(projectPath)
                .Where(d => d.StartsWith(ModuleProjectPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(OwningModule)
                // A module referencing its own Contracts project collapses onto itself; that is not a cycle.
                .Where(d => !string.Equals(d, module, StringComparison.OrdinalIgnoreCase));

            foreach (var dependency in moduleDependencies)
            {
                dependencies.Add(dependency);
            }
        }

        var cycles = DetectCycles(dependencyGraph)
            .Select(CanonicalModuleCycle)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unexpectedCycles = cycles
            .Where(cycle => !KnownModuleCycles.Contains(cycle, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var staleAllowlistEntries = KnownModuleCycles
            .Where(known => !cycles.Contains(known))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        unexpectedCycles.ShouldBeEmpty(
            $"Circular module dependencies detected: {string.Join("; ", unexpectedCycles)}. " +
            "Modules may only depend on each other in one direction, including through Contracts projects.");

        staleAllowlistEntries.ShouldBeEmpty(
            $"KnownModuleCycles lists cycles that no longer exist: {string.Join("; ", staleAllowlistEntries)}. " +
            "Remove the entry so the allowlist keeps matching reality.");
    }

    [Fact]
    public void BuildingBlocks_Should_Not_Have_Circular_Dependencies()
    {
        string buildingBlocksRoot = Path.Combine(SolutionRoot, "src", "BuildingBlocks");

        if (!Directory.Exists(buildingBlocksRoot))
        {
            return;
        }

        var buildingBlockProjects = Directory
            .GetFiles(buildingBlocksRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains("obj", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var dependencyGraph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectPath in buildingBlockProjects)
        {
            string projectName = Path.GetFileNameWithoutExtension(projectPath);
            var dependencies = GetProjectReferences(projectPath);
            dependencyGraph[projectName] = dependencies;
        }

        var cycles = DetectCycles(dependencyGraph);

        cycles.ShouldBeEmpty(
            $"Circular BuildingBlock dependencies detected: {string.Join("; ", cycles)}");
    }

    [Fact]
    public void Dependency_Graph_Should_Be_Acyclic()
    {
        string srcRoot = Path.Combine(SolutionRoot, "src");

        var projectPaths = Directory
            .GetFiles(srcRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains("obj", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var dependencyGraph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var projectPath in projectPaths)
        {
            string projectName = Path.GetFileNameWithoutExtension(projectPath);
            var dependencies = GetProjectReferences(projectPath);
            dependencyGraph[projectName] = dependencies;
        }

        // Attempt topological sort - will fail if cycles exist
        _ = TopologicalSort(dependencyGraph, out var hasCycle, out var cycleDescription);

        hasCycle.ShouldBeFalse(
            $"Dependency graph is not acyclic. {cycleDescription}");
    }

    private static HashSet<string> GetProjectReferences(string projectPath)
    {
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var document = XDocument.Load(projectPath);

            var projectRefs = document
                .Descendants("ProjectReference")
                .Select(x => (string?)x.Attribute("Include") ?? string.Empty)
                .Where(include => include.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .Select(ProjectReferences.GetReferencedProjectName)
                .Where(name => !string.IsNullOrEmpty(name));

            foreach (var reference in projectRefs)
            {
                references.Add(reference);
            }
        }
        catch (System.Xml.XmlException)
        {
            // Ignore XML parse errors
        }
        catch (IOException)
        {
            // Ignore file IO errors
        }

        return references;
    }

    // Boilerplate.Modules.Billing and Boilerplate.Modules.Billing.Contracts are both module "Billing".
    private static string OwningModule(string projectName)
    {
        string module = projectName[ModuleProjectPrefix.Length..];

        return module.EndsWith(ContractsSuffix, StringComparison.OrdinalIgnoreCase)
            ? module[..^ContractsSuffix.Length]
            : module;
    }

    // DetectCycles reports the walked path ("Billing -> Multitenancy -> Billing"), which depends on the
    // entry point it happened to start from; reduce it to the sorted set of modules involved so that one
    // cycle always has one name to match against KnownModuleCycles.
    private static string CanonicalModuleCycle(string cyclePath) =>
        string.Join(" <-> ", cyclePath
            .Split(" -> ", StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase));

    private static List<string> DetectCycles(Dictionary<string, HashSet<string>> graph)
    {
        var cycles = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recursionStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new List<string>();

        foreach (var node in graph.Keys)
        {
            if (DetectCyclesDfs(node, graph, visited, recursionStack, path, cycles))
            {
                // Found at least one cycle
            }
        }

        return cycles;
    }

    private static bool DetectCyclesDfs(
        string node,
        Dictionary<string, HashSet<string>> graph,
        HashSet<string> visited,
        HashSet<string> recursionStack,
        List<string> path,
        List<string> cycles)
    {
        if (recursionStack.Contains(node))
        {
            // Found a cycle - extract the cycle path
            int cycleStart = path.IndexOf(node);
            if (cycleStart >= 0)
            {
                var cyclePath = path.Skip(cycleStart).Append(node).ToArray();
                cycles.Add(string.Join(" -> ", cyclePath));
            }
            return true;
        }

        if (visited.Contains(node))
        {
            return false;
        }

        visited.Add(node);
        recursionStack.Add(node);
        path.Add(node);

        if (graph.TryGetValue(node, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                DetectCyclesDfs(neighbor, graph, visited, recursionStack, path, cycles);
            }
        }

        path.RemoveAt(path.Count - 1);
        recursionStack.Remove(node);

        return false;
    }

    private static List<string> TopologicalSort(
        Dictionary<string, HashSet<string>> graph,
        out bool hasCycle,
        out string cycleDescription)
    {
        hasCycle = false;
        cycleDescription = string.Empty;

        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var temporaryMark = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in graph.Keys)
        {
            if (!visited.Contains(node) &&
                !TopologicalSortVisit(node, graph, visited, temporaryMark, result, out cycleDescription))
            {
                hasCycle = true;
                return result;
            }
        }

        result.Reverse();
        return result;
    }

    private static bool TopologicalSortVisit(
        string node,
        Dictionary<string, HashSet<string>> graph,
        HashSet<string> visited,
        HashSet<string> temporaryMark,
        List<string> result,
        out string cycleDescription)
    {
        cycleDescription = string.Empty;

        if (temporaryMark.Contains(node))
        {
            cycleDescription = $"Cycle detected at node: {node}";
            return false;
        }

        if (visited.Contains(node))
        {
            return true;
        }

        temporaryMark.Add(node);

        if (graph.TryGetValue(node, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                if (!TopologicalSortVisit(neighbor, graph, visited, temporaryMark, result, out cycleDescription))
                {
                    cycleDescription = $"{node} -> {cycleDescription}";
                    return false;
                }
            }
        }

        temporaryMark.Remove(node);
        visited.Add(node);
        result.Add(node);

        return true;
    }
}