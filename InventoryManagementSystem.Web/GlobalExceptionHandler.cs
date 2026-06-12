using System.Net;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace InventoryManagementSystem.Web;

/// <summary>
/// Global exception handler that returns ProblemDetails for API requests
/// and redirects to the error page for MVC requests.
/// </summary>
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Translates unhandled exceptions into either a <c>ProblemDetails</c> JSON response
    /// (for API requests) or a delegated MVC error page (for browser requests).
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="exception">The unhandled exception.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if a response was written; <see langword="false"/> to let the next handler try.</returns>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Unhandled exception: {Message}", exception.Message);

        // Handle FluentValidation errors with field-level detail
        if (exception is ValidationException validationException)
        {
            var errors = validationException.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(e => e.ErrorMessage).ToArray());

            if (httpContext.Request.Path.StartsWithSegments("/api"))
            {
                httpContext.Response.StatusCode = 400;
                httpContext.Response.ContentType = "application/problem+json";

                var validationProblem = new ValidationProblemDetails
                {
                    Status = 400,
                    Title = "Validation failed",
                    Detail = "One or more validation errors occurred",
                    Instance = httpContext.Request.Path
                };

                foreach (var error in errors)
                {
                    validationProblem.Errors.Add(error.Key, error.Value);
                }

                await httpContext.Response.WriteAsJsonAsync(validationProblem, cancellationToken);
                return true;
            }

            // For MVC, store errors in TempData for display
            httpContext.Response.StatusCode = 400;
            return false;
        }

        var (statusCode, title) = exception switch
        {
            ArgumentException => (HttpStatusCode.BadRequest, "Invalid argument"),
            InvalidOperationException => (HttpStatusCode.Conflict, "Operation failed"),
            UnauthorizedAccessException => (HttpStatusCode.Forbidden, "Access denied"),
            KeyNotFoundException => (HttpStatusCode.NotFound, "Resource not found"),
            _ => (HttpStatusCode.InternalServerError, "An unexpected error occurred")
        };

        // For API requests, return ProblemDetails JSON
        if (httpContext.Request.Path.StartsWithSegments("/api"))
        {
            httpContext.Response.StatusCode = (int)statusCode;
            httpContext.Response.ContentType = "application/problem+json";

            var problem = new ProblemDetails
            {
                Status = (int)statusCode,
                Title = title,
                Detail = httpContext.RequestServices
                    .GetRequiredService<IHostEnvironment>().IsDevelopment()
                    ? exception.Message
                    : title,
                Instance = httpContext.Request.Path
            };

            await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
            return true;
        }

        // For MVC requests, let the default exception handler page handle it
        return false;
    }
}
