using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Serilog.Parsing;

namespace Framework.Tests.Web.Logging;

/// <summary>
/// Assertions on the API host's shipped <c>appsettings*.json</c>: what the person running the API reads on stdout is
/// decided there, not in code. Each test names the audit finding it guards.
/// </summary>
public sealed class ApiLoggingConfigurationTests
{
    private static readonly string ApiDirectory = Path.Combine(FindRepoRoot(), "src", "Host", "Boilerplate.Api");

    private static readonly Dictionary<string, string> ExpectedOverrides = new(StringComparer.Ordinal)
    {
        ["Microsoft"] = "Warning",
        ["Microsoft.Hosting.Lifetime"] = "Information",
        ["Microsoft.EntityFrameworkCore"] = "Error",
        ["Hangfire"] = "Warning",
        ["Finbuckle.MultiTenant"] = "Warning",
    };

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Boilerplate.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (src/Boilerplate.slnx) not found.");
    }

    private static IConfigurationRoot Load(string environment) =>
        new ConfigurationBuilder()
            .SetBasePath(ApiDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: false)
            .Build();

    private static LogEvent RequestEvent(Exception? exception = null) =>
        new(
            DateTimeOffset.UtcNow,
            LogEventLevel.Error,
            exception,
            new MessageTemplateParser().Parse("Processed {Thing}"),
            [
                new LogEventProperty("Thing", new ScalarValue("order")),
                new LogEventProperty("Tenant", new ScalarValue("acme")),
                new LogEventProperty("UserId", new ScalarValue("user-42")),
                new LogEventProperty("CorrelationId", new ScalarValue("corr-123")),
            ]);

    private static string Render(ITextFormatter formatter, LogEvent logEvent)
    {
        using var writer = new StringWriter();
        formatter.Format(logEvent, writer);
        return writer.ToString();
    }

    // O2: Production writes one JSON object per event, so every enriched property is on stdout and parseable.
    [Fact]
    public void Production_Console_Should_WriteEveryPropertyAsJson()
    {
        var console = Load("Production").GetSection("Serilog:WriteTo:0");
        console["Name"].ShouldBe("Console");

        var formatterName = console["Args:formatter"];
        formatterName.ShouldNotBeNullOrWhiteSpace();
        var formatterType = Type.GetType(formatterName!, throwOnError: false);
        formatterType.ShouldNotBeNull($"'{formatterName}' must resolve to a formatter type");
        // Serilog's configuration reader constructs the formatter with its optional arguments defaulted.
        var constructor = formatterType.GetConstructors().First(c => c.GetParameters().All(p => p.IsOptional));
        var formatter = (ITextFormatter)constructor.Invoke(constructor.GetParameters().Select(p => p.DefaultValue).ToArray());

        var json = Render(formatter, RequestEvent(new InvalidOperationException("boom")));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("Tenant").GetString().ShouldBe("acme");
        root.GetProperty("UserId").GetString().ShouldBe("user-42");
        root.GetProperty("CorrelationId").GetString().ShouldBe("corr-123");
        root.GetProperty("@x").GetString()!.ShouldContain("InvalidOperationException: boom");
    }

    // O2: Development keeps a readable line, but one that names the tenant, user and correlation id.
    [Fact]
    public void Development_Console_Should_RenderTheRequestContextAndTheException()
    {
        var console = Load("Development").GetSection("Serilog:WriteTo:0");
        console["Name"].ShouldBe("Console");

        var template = console["Args:outputTemplate"];
        template.ShouldNotBeNullOrWhiteSpace();

        var line = Render(
            new MessageTemplateTextFormatter(template!, formatProvider: null),
            RequestEvent(new InvalidOperationException("boom")));

        line.ShouldContain("acme");
        line.ShouldContain("user-42");
        line.ShouldContain("corr-123");
        line.ShouldContain("InvalidOperationException: boom");
    }

    // O4: category levels are configured where Serilog reads them, in every shipped environment.
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void Serilog_Should_CarryTheCategoryOverrides(string environment)
    {
        var overrides = Load(environment).GetSection("Serilog:MinimumLevel:Override");

        foreach (var (category, level) in ExpectedOverrides)
        {
            overrides[category].ShouldBe(level, $"{environment}: Serilog:MinimumLevel:Override:{category}");
        }
    }

    // O4: Serilog replaces the Microsoft.Extensions.Logging providers, so a Logging:LogLevel block
    // would look tunable and do nothing.
    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    [InlineData("appsettings.Production.json")]
    public void Appsettings_Should_NotShipTheInertLoggingSection(string file)
    {
        var configuration = new ConfigurationBuilder().SetBasePath(ApiDirectory).AddJsonFile(file).Build();

        configuration.GetSection("Logging").Exists().ShouldBeFalse($"{file} must not carry a Logging section");
    }

    // O6: OTLP log export is added in code only when an endpoint is resolved; a static sink entry with
    // an empty endpoint would ship every batch to localhost:4317, and twice once export is turned on.
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void Serilog_Should_WriteOnlyToTheConsole(string environment)
    {
        var sinks = Load(environment).GetSection("Serilog:WriteTo").GetChildren().Select(s => s["Name"]).ToList();

        sinks.ShouldBe(["Console"]);
    }

    // The shipped section must be one Serilog's configuration reader accepts as a whole (sink, level,
    // formatter or template together) — a wrong argument fails here rather than at boot.
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void Serilog_Should_BuildALoggerFromTheShippedConfiguration(string environment)
    {
        using var logger = new LoggerConfiguration().ReadFrom.Configuration(Load(environment)).CreateLogger();

        logger.IsEnabled(LogEventLevel.Information).ShouldBeTrue();
    }
}
