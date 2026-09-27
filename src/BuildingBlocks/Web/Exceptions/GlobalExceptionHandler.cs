using System.Diagnostics;
using System;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Boilerplate.BuildingBlocks.Web.Exceptions;

public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var problemDetails = new ProblemDetails
        {
            Instance = httpContext.Request.Path
        };

        var statusCode = StatusCodes.Status500InternalServerError;

        if (exception is FluentValidation.ValidationException fluentException)
        {
            statusCode = StatusCodes.Status400BadRequest;

            problemDetails.Status = statusCode;
            problemDetails.Title = "Validation error";
            problemDetails.Detail = "One or more validation errors occurred.";
            problemDetails.Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1";

            var errors = fluentException.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(e => e.ErrorMessage).ToArray());

            problemDetails.Extensions["errors"] = errors;
        }
        else if (exception is CustomException e)
        {
            statusCode = (int)e.StatusCode;

            problemDetails.Status = statusCode;
            // A stable phrase for the status, never the CLR type name: renaming an internal exception
            // must not change the public error shape, and the type name is implementation detail.
            problemDetails.Title = TitleFor(statusCode);
            problemDetails.Detail = e.Message;

            if (e.ErrorMessages is { Count: > 0 })
            {
                problemDetails.Extensions["errors"] = e.ErrorMessages;
            }
        }
        else if (exception is UnauthorizedAccessException)
        {
            statusCode = StatusCodes.Status401Unauthorized;
            problemDetails.Status = statusCode;
            problemDetails.Title = "Unauthorized";
            problemDetails.Detail = exception.Message;
        }
        else if (exception is KeyNotFoundException)
        {
            statusCode = StatusCodes.Status404NotFound;
            problemDetails.Status = statusCode;
            problemDetails.Title = "Not Found";
            problemDetails.Detail = exception.Message;
        }
        else if (exception is BadHttpRequestException badRequest)
        {
            // BadHttpRequestException = malformed request (missing required header/param, unreadable/oversized body).
            // Client error carrying the correct status (usually 400) — honour it instead of falling through to a generic 500.
            statusCode = badRequest.StatusCode;
            problemDetails.Status = statusCode;
            problemDetails.Title = "Bad Request";
            problemDetails.Detail = badRequest.Message;
        }
        else
        {
            statusCode = StatusCodes.Status500InternalServerError;
            problemDetails.Status = statusCode;
            problemDetails.Title = "An unexpected error occurred";
            problemDetails.Detail = "An unexpected error occurred. Please try again later.";
        }

        httpContext.Response.StatusCode = statusCode;

        // Surface trace and correlation IDs so clients/support can correlate errors to traces
        var traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
        problemDetails.Extensions["traceId"] = traceId;

        var correlationId = httpContext.Request.Headers["X-Correlation-ID"].FirstOrDefault()
            ?? httpContext.TraceIdentifier;
        problemDetails.Extensions["correlationId"] = correlationId;

        var path = httpContext.Request.Path.Value?.Replace(Environment.NewLine, string.Empty, StringComparison.Ordinal);

        // A server fault is an incident: log it at Error with the exception, so the line carries the
        // type, message and stack trace. A client mistake (4xx) is routine traffic: Warning, with the
        // exception type and detail but no stack trace, so Error-level volume stays an incident signal.
        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Exception at {Path} - {StatusCode} {Title}", path, statusCode, problemDetails.Title);
        }
        else
        {
            logger.LogWarning(
                "Exception at {Path} - {StatusCode} {Title}: {ExceptionType} {Detail}",
                path, statusCode, problemDetails.Title, exception.GetType().Name, problemDetails.Detail);
        }

        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string TitleFor(int statusCode)
    {
        var phrase = ReasonPhrases.GetReasonPhrase(statusCode);
        return string.IsNullOrEmpty(phrase) ? "Error" : phrase;
    }
}