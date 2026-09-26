using Goulash.Api.Errors;
using Microsoft.AspNetCore.Antiforgery;

namespace Goulash.Api.Auth;

public sealed class CsrfProtectionMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "HEAD", "OPTIONS", "TRACE"
    };

    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        var endpoint = context.GetEndpoint();
        var isApiEndpoint = endpoint is not null && context.Request.Path.StartsWithSegments("/api/v1");
        if (!isApiEndpoint || SafeMethods.Contains(context.Request.Method))
        {
            await next(context);
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            await ProblemResponses.WriteAsync(context, StatusCodes.Status403Forbidden,
                "CSRF-токен отсутствует или недействителен", "CSRF_INVALID");
            return;
        }

        await next(context);
    }
}
