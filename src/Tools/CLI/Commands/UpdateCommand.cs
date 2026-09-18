using Boilerplate.CLI.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Boilerplate.CLI.Commands;

public sealed class UpdateCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"[bold {AppConstants.AccentColor}]Updating Boilerplate tools...[/]");
        AnsiConsole.WriteLine();

        bool cliSuccess = await RunUpdateStepAsync(
            "Updating Boilerplate CLI",
            "dotnet", $"tool update -g {AppConstants.CliPackageId}",
            cancellationToken).ConfigureAwait(false);

        bool templateSuccess = await RunUpdateStepAsync(
            "Updating Boilerplate template",
            "dotnet", $"new install {AppConstants.TemplatePackageId}",
            cancellationToken).ConfigureAwait(false);

        AnsiConsole.WriteLine();

        bool allSuccess = cliSuccess && templateSuccess;
        if (allSuccess)
        {
            AnsiConsole.MarkupLine($"[{AppConstants.SuccessColor}]All updates complete.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[{AppConstants.WarningColor}]Some updates failed. Check the output above.[/]");
        }

        return allSuccess ? 0 : 1;
    }

    private static async Task<bool> RunUpdateStepAsync(
        string description, string command, string arguments, CancellationToken cancellationToken)
    {
        return await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse(AppConstants.AccentColor))
            .StartAsync(description, async _ =>
            {
                int exitCode = await ProcessRunner.RunAsync(
                    command, arguments,
                    showOutput: false,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (exitCode == 0)
                {
                    AnsiConsole.MarkupLine($"  [{AppConstants.SuccessColor}]{description}: done[/]");
                    return true;
                }

                AnsiConsole.MarkupLine($"  [{AppConstants.ErrorColor}]{description}: failed (exit code {exitCode})[/]");
                return false;
            }).ConfigureAwait(false);
    }
}
