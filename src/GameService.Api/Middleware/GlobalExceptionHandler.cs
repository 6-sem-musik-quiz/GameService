using GameService.Service.Exceptions;
using Microsoft.AspNetCore.Diagnostics;

namespace GameService.Api.Middleware;

// Maps domain exceptions to 400/502 and any other unhandled exception to a ProblemDetails 500.
public class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            GameValidationException => (StatusCodes.Status400BadRequest, exception.Message),
            GameServiceDrainingException => (StatusCodes.Status503ServiceUnavailable, exception.Message),
            InsufficientSongsException or MusicServiceException =>
                (StatusCodes.Status502BadGateway, "MusicService could not provide the songs for the game."),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
        };

        if (status >= 500)
            logger.LogError(exception, "Unhandled exception for {Method} {Path}",
                httpContext.Request.Method, httpContext.Request.Path);
        else
            logger.LogWarning("{Method} {Path} rejected: {Message}",
                httpContext.Request.Method, httpContext.Request.Path, exception.Message);

        httpContext.Response.StatusCode = status;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Title = title,
                Status = status
            }
        });
    }
}
