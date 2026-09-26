namespace Goulash.Api.Errors;

public static class ProblemResponses
{
    public static Task WriteAsync(HttpContext context, int status, string title, string code)
    {
        var problem = Results.Problem(
            statusCode: status,
            title: title,
            type: $"https://example.local/problems/{ToProblemSlug(code)}",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["traceId"] = context.TraceIdentifier
            });

        return problem.ExecuteAsync(context);
    }

    private static string ToProblemSlug(string code) => code.ToLowerInvariant().Replace('_', '-');
}
