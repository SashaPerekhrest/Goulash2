namespace Goulash.Api.Errors;

public static class ProblemResponses
{
    public static IResult Create(HttpContext context, int status, string title, string code, string? detail = null) =>
        Results.Problem(
            statusCode: status,
            title: title,
            detail: detail,
            type: $"https://example.local/problems/{ToProblemSlug(code)}",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["traceId"] = context.TraceIdentifier
            });

    public static Task WriteAsync(HttpContext context, int status, string title, string code)
    {
        return Create(context, status, title, code).ExecuteAsync(context);
    }

    private static string ToProblemSlug(string code) => code.ToLowerInvariant().Replace('_', '-');
}
