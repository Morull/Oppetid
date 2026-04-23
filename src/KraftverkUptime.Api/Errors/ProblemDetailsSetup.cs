using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace KraftverkUptime.Api.Errors;

public static class ProblemDetailsSetup
{
    public static IServiceCollection AddKraftverkProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(o =>
        {
            o.CustomizeProblemDetails = ctx =>
            {
                ctx.ProblemDetails.Extensions["traceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString()
                    ?? ctx.HttpContext.TraceIdentifier;
            };
        });
        services.AddExceptionHandler<GlobalExceptionHandler>();
        return services;
    }
}

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        _logger.LogError(exception, "Unhandled exception on {Path}", httpContext.Request.Path);

        var (status, title) = exception switch
        {
            ArgumentException    => (StatusCodes.Status400BadRequest, "Invalid request"),
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden"),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            _ => (StatusCodes.Status500InternalServerError, "Internal server error"),
        };

        httpContext.Response.StatusCode = status;

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Type = $"https://kraftverkuptime/errors/{status}",
                Title = title,
                Status = status,
                Detail = httpContext.RequestServices
                    .GetRequiredService<IHostEnvironment>().IsDevelopment() ? exception.Message : null
            }
        });
    }
}
