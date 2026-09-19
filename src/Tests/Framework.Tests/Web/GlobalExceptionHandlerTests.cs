using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Web.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;

namespace Framework.Tests.Web;

public sealed class GlobalExceptionHandlerTests
{
    private static async Task<HttpContext> HandleAsync(Exception exception)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/v1/tenants/root/auth/forgot-password";
        context.Response.Body = new MemoryStream();

        var handler = new GlobalExceptionHandler(NullLogger<GlobalExceptionHandler>.Instance);
        await handler.TryHandleAsync(context, exception, CancellationToken.None);
        return context;
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
}
