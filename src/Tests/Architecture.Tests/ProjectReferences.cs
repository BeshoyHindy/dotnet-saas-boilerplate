namespace Architecture.Tests;

/// <summary>
/// Shared helpers for reading csproj <c>ProjectReference</c> includes.
/// </summary>
internal static class ProjectReferences
{
    // ProjectReference paths use Windows separators (..\Core\Boilerplate.BuildingBlocks.Core.csproj), but GetFileNameWithoutExtension only
    // splits on '\' on Windows — normalize to '/' first so Linux and macOS also get the bare project name.
    public static string GetReferencedProjectName(string includePath) =>
        Path.GetFileNameWithoutExtension(includePath.Replace('\\', '/'));
}
