using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Web.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;

namespace Framework.Tests.Web;

public sealed class GlobalExceptionHandlerTests
{
    private static async Task<HttpContext> HandleAsync(Exception exception, ILogger<GlobalExceptionHandler>? logger = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/tenants/root/auth/forgot-password";
        context.Response.Body = new MemoryStream();

        var handler = new GlobalExceptionHandler(logger ?? NullLogger<GlobalExceptionHandler>.Instance);
        await handler.TryHandleAsync(context, exception, CancellationToken.None);
        return context;
    }

    private static string ReadTitle(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(context.Response.Body);
        return document.RootElement.GetProperty("title").GetString()!;
    }

    // Regression for #1245: a missing required bound parameter surfaces as a
    // BadHttpRequestException during binding and must render with its own status (400), not the
    // generic 500 fallback. (The original trigger — a missing `tenant` header — is gone with
    // ADR-0002; the mapping it exposed still needs guarding.)
    [Fact]
    public async Task TryHandleAsync_Should_Map_BadHttpRequestException_To_ItsStatusCode()
    {
        var exception = new BadHttpRequestException(
            "Required parameter \"string code\" was not provided from query string.");

        var context = await HandleAsync(exception);

        exception.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task TryHandleAsync_Should_Honour_NonDefault_BadHttpRequestException_StatusCode()
    {
        var exception = new BadHttpRequestException(
            "Request body too large.", StatusCodes.Status413PayloadTooLarge);

        var context = await HandleAsync(exception);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact]
    public async Task TryHandleAsync_Should_Map_CustomException_To_ItsStatusCode()
    {
        var context = await HandleAsync(
            new CustomException("conflict", errors: null, HttpStatusCode.Conflict));

        context.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task TryHandleAsync_Should_Default_To_500_For_UnknownException()
    {
        var context = await HandleAsync(new InvalidOperationException("boom"));

        context.Response.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
    }

    // F36 of the scaffold dry run: a handler that honoured a tenant id taken from the request body
    // made Finbuckle refuse the save ("1 added entities with Tenant Id mismatch"). The data stayed
    // safe, but the caller got a 500 naming the exception type. The caller asked for something that
    // cannot be done in its own tenant — a client error — and the answer must name neither the
    // library that refused it nor anything about the tenant the request named.
    [Fact]
    public async Task TryHandleAsync_Should_Map_MultiTenantException_To_400_Without_Naming_It()
    {
        var context = await HandleAsync(
            new Finbuckle.MultiTenant.Abstractions.MultiTenantException("1 added entities with Tenant Id mismatch."));

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        body.ShouldNotContain("MultiTenantException");
        body.ShouldNotContain("Tenant Id mismatch");
    }

    // O1: the one log line a 500 gets must carry the exception (type, message, stack trace).
    [Fact]
    public async Task TryHandleAsync_Should_LogErrorWithTheException_When_TheResponseIs5xx()
    {
        var logger = new RecordingLogger();
        var exception = new InvalidOperationException("boom");

        await HandleAsync(exception, logger);

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Exception.ShouldBeSameAs(exception);
    }

    // O11: a client mistake is not an incident; only >= 500 is logged at Error.
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task TryHandleAsync_Should_LogWarningWithoutAStackTrace_When_TheResponseIs4xx(HttpStatusCode status)
    {
        var logger = new RecordingLogger();

        await HandleAsync(new CustomException("nope", errors: null, status), logger);

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Exception.ShouldBeNull();
    }

    [Fact]
    public async Task TryHandleAsync_Should_LogError_When_ACustomExceptionCarriesA5xx()
    {
        var logger = new RecordingLogger();
        var exception = new CustomException("upstream down", errors: null, HttpStatusCode.ServiceUnavailable);

        await HandleAsync(exception, logger);

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Exception.ShouldBeSameAs(exception);
    }

    // O13: the public Title is a stable phrase for the status, never the CLR type name.
    [Fact]
    public async Task TryHandleAsync_Should_TitleACustomExceptionByItsStatus_NotItsTypeName()
    {
        var context = await HandleAsync(new NotFoundException("Tenant 'acme' not found."));

        ReadTitle(context).ShouldBe("Not Found");
    }

    [Fact]
    public async Task TryHandleAsync_Should_TitleACustomExceptionWithAnUnnamedStatus_Generically()
    {
        var context = await HandleAsync(new CustomException("odd", errors: null, (HttpStatusCode)432));

        ReadTitle(context).ShouldBe("Error");
    }

    private sealed class RecordingLogger : ILogger<GlobalExceptionHandler>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception, formatter(state, exception)));
    }
}
