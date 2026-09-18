using System.Reflection;
using Boilerplate.CLI.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Boilerplate.CLI.Commands;

public sealed class InfoCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        string currentVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? "unknown";

        AnsiConsole.MarkupLine($"[bold {AppConstants.AccentColor}]Boilerplate CLI[/]");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .HideHeaders()
            .AddColumn("Key")
            .AddColumn("Value");

        table.AddRow("CLI Version", $"[bold]{currentVersion.EscapeMarkup()}[/]");

        // Run NuGet + template checks in parallel
        Task<string?> latestCliTask = NuGetClient.GetLatestVersionAsync(AppConstants.CliPackageId, cancellationToken);
        Task<string?> templateVersionTask = NuGetClient.GetInstalledTemplateVersionAsync(cancellationToken);
        Task<string?> latestTemplateTask = NuGetClient.GetLatestVersionAsync(AppConstants.TemplatePackageId, cancellationToken);
        Task<(bool, string)> sdkTask = ProcessRunner.CaptureAsync("dotnet", "--version", cancellationToken);

        await Task.WhenAll(latestCliTask, templateVersionTask, latestTemplateTask, sdkTask).ConfigureAwait(false);

        string? latestCli = await latestCliTask.ConfigureAwait(false);
        string? templateVersion = await templateVersionTask.ConfigureAwait(false);
        string? latestTemplate = await latestTemplateTask.ConfigureAwait(false);
        (bool sdkOk, string sdkVersion) = await sdkTask.ConfigureAwait(false);

        // Latest CLI
        if (latestCli is not null)
        {
            bool updateAvailable = VersionComparer.IsNewer(latestCli, currentVersion);
            table.AddRow("Latest CLI", updateAvailable
                ? $"[{AppConstants.WarningColor}]{latestCli} (update available — run 'boilerplate update')[/]"
                : $"[{AppConstants.SuccessColor}]{latestCli} (up to date)[/]");
        }
        else
        {
            table.AddRow("Latest CLI", $"[{AppConstants.DimColor}]could not check[/]");
        }

        // Template version
        string templateDisplay;
        if (templateVersion is null)
            templateDisplay = $"[{AppConstants.DimColor}]not installed[/]";
        else
            templateDisplay = char.IsDigit(templateVersion[0]) ? $"v{templateVersion}" : templateVersion;
        table.AddRow("Template", templateDisplay);

        if (latestTemplate is not null)
        {
            bool updateAvailable = VersionComparer.IsNewer(latestTemplate, templateVersion);
            table.AddRow("Latest Template", updateAvailable
                ? $"[{AppConstants.WarningColor}]{latestTemplate} (update available)[/]"
                : $"[{AppConstants.SuccessColor}]{latestTemplate} (up to date)[/]");
        }

        // .NET SDK
        if (sdkOk)
        {
            table.AddRow(".NET SDK", $"v{sdkVersion}");
        }

        // Links
        table.AddRow("Documentation", $"[link={AppConstants.DocsUrl}]{AppConstants.DocsUrl}[/]");
        table.AddRow("Release Notes", $"[link={AppConstants.ReleaseNotesUrl}]{AppConstants.ReleaseNotesUrl}[/]");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        return 0;
    }
}
