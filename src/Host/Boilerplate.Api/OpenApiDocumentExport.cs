using Boilerplate.BuildingBlocks.Web.Modules;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Boilerplate.Api;

/// <summary>
/// The `--export-openapi &lt;file&gt;` switch (ADR-0004): writes the versioned OpenAPI document to disk
/// and exits, so <c>scripts/export-openapi.sh</c> can keep <c>clients/openapi/v1.json</c> — the
/// contract the console's types are generated from — in the repository.
///
/// <para>
/// It deliberately does NOT run <c>UseHeroPlatform</c>. The document is built from endpoint metadata
/// alone, so the export only needs the modules' routes mapped; the middleware pipeline would also
/// mount the Hangfire dashboard, which resolves <c>JobStorage</c> and opens a PostgreSQL connection.
/// Exporting a contract must not need a database — that is what lets the drift gate (issue #15) run
/// on a bare CI runner.
/// </para>
/// </summary>
internal static class OpenApiDocumentExport
{
    private const string OutputSwitch = "--export-openapi";
    private const string DocumentSwitch = "--openapi-document";
    private const string DefaultDocumentName = "v1";

    /// <summary>True when the host was started to export a document rather than serve traffic.</summary>
    public static bool IsRequested(string[] args) =>
        Array.IndexOf(args, OutputSwitch) >= 0;

    /// <summary>
    /// Maps the module endpoints, serialises the requested document and writes it to the path given
    /// after <c>--export-openapi</c>. The file is written with a trailing newline; ordering and
    /// formatting are normalised afterwards by <c>scripts/export-openapi.sh</c>, which is what makes
    /// the checked-in artifact stable across runs and machines.
    /// </summary>
    public static async Task RunAsync(WebApplication app, string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(args);

        var outputPath = ValueAfter(args, OutputSwitch)
            ?? throw new InvalidOperationException($"{OutputSwitch} requires a file path.");
        var documentName = ValueAfter(args, DocumentSwitch) ?? DefaultDocumentName;

        // Endpoint metadata is the document's only input, so mapping the modules is enough.
        app.MapModules();

        // …but a WebApplication holds its route table privately until the routing middleware claims
        // it. The document is built by the API explorer, which reads the endpoint data sources
        // registered in the container, so the endpoints stay invisible to it until UseRouting and
        // UseEndpoints have run. That is exactly what startup does next; here it costs nothing,
        // because in export mode no other middleware is registered and nothing is ever served.
        app.UseRouting();
        app.UseEndpoints(_ => { });

        var provider = app.Services.GetKeyedService<IOpenApiDocumentProvider>(documentName)
            ?? throw new InvalidOperationException(
                $"No OpenAPI document named '{documentName}' is registered. " +
                "Set OpenApiOptions:Enabled to true and list the version under OpenApiOptions:Versions.");

        var document = await provider.GetOpenApiDocumentAsync(cancellationToken).ConfigureAwait(false);

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(outputPath);
        await document.SerializeAsJsonAsync(stream, OpenApiSpecVersion.OpenApi3_1, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"OpenAPI document '{documentName}' written to {Path.GetFullPath(outputPath)}");
    }

    private static string? ValueAfter(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
