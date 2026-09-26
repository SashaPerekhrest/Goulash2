using Goulash.Api.Errors;
using Goulash.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

builder.Services.AddOpenApi("v1");
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

await ApplyMigrationsAsync(app);

app.UseExceptionHandler(exceptionHandler => exceptionHandler.Run(async context =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Goulash.Api.Errors");
    logger.LogError("Unhandled API request failed. TraceId={TraceId}", context.TraceIdentifier);

    await ProblemResponses.WriteAsync(context, StatusCodes.Status500InternalServerError,
        "Внутренняя ошибка сервера", "INTERNAL_ERROR");
}));

app.UseStatusCodePages(async statusCodeContext =>
{
    var context = statusCodeContext.HttpContext;
    var status = context.Response.StatusCode;
    var (title, code) = status switch
    {
        StatusCodes.Status400BadRequest => ("Некорректный запрос", "VALIDATION_ERROR"),
        StatusCodes.Status401Unauthorized => ("Требуется авторизация", "UNAUTHORIZED"),
        StatusCodes.Status403Forbidden => ("Доступ запрещён", "FORBIDDEN"),
        StatusCodes.Status404NotFound => ("Ресурс не найден", "NOT_FOUND"),
        StatusCodes.Status409Conflict => ("Конфликт запроса", "CONFLICT"),
        StatusCodes.Status429TooManyRequests => ("Слишком много запросов", "RATE_LIMITED"),
        StatusCodes.Status503ServiceUnavailable => ("Сервис временно недоступен", "SERVICE_UNAVAILABLE"),
        _ => ("Ошибка запроса", "HTTP_ERROR")
    };

    await ProblemResponses.WriteAsync(context, status, title, code);
});

app.MapOpenApi("/openapi/{documentName}.json");

app.MapGet("/api/v1/health/ready", ReadyAsync)
    .WithName("GetReadiness")
    .WithTags("Health")
    .WithSummary("Проверяет готовность API и доступность PostgreSQL")
    .Produces(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .AllowAnonymous();

app.Run();

static async Task<IResult> ReadyAsync(ApplicationDbContext db, HttpContext context, CancellationToken cancellationToken)
{
    try
    {
        if (!await db.Database.CanConnectAsync(cancellationToken))
            throw new InvalidOperationException("Database is unavailable.");

        await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
        return Results.Ok(new { status = "ready" });
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Goulash.Api.Readiness");
        logger.LogWarning("Readiness probe failed. TraceId={TraceId}", context.TraceIdentifier);
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "База данных недоступна", type: "https://example.local/problems/database-unavailable",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "DATABASE_UNAVAILABLE",
                ["traceId"] = context.TraceIdentifier
            });
    }
}

static async Task ApplyMigrationsAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    try
    {
        await db.Database.MigrateAsync();
        app.Logger.LogInformation("Database migrations are up to date.");
    }
    catch
    {
        app.Logger.LogCritical("Database migration failed; API startup is stopping.");
        throw new InvalidOperationException("Database migrations could not be applied. Check database availability and configuration.");
    }
}
