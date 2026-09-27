using Goulash.Api.Errors;
using Goulash.Api.Auth;
using Goulash.Api.Providers;
using Goulash.Application;
using Goulash.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var adminPassword = builder.Configuration["Admin:InitialPassword"];
if (string.IsNullOrWhiteSpace(adminPassword) || adminPassword.Length < 12)
    throw new InvalidOperationException("Admin:InitialPassword must be configured with at least 12 characters.");

var keyProtector = new AiApiKeyProtector(builder.Configuration["Ai:EncryptionKey"]);
var providerTimeoutSeconds = int.TryParse(builder.Configuration["Ai:ProviderTimeoutSeconds"], out var configuredProviderTimeout)
    ? configuredProviderTimeout
    : 45;
if (providerTimeoutSeconds is < 1 or > 300)
    throw new InvalidOperationException("Ai:ProviderTimeoutSeconds must be between 1 and 300.");

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

builder.Services.AddOpenApi("v1");
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 2; // nginx and Caddy each add one proxy hop.
    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("10.0.0.0/8"));
    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.16.0.0/12"));
    options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("192.168.0.0/16"));
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-Token";
    options.Cookie.Name = "goulash.csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.Cookie.IsEssential = true;
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "goulash.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.Cookie.IsEssential = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = async context =>
        {
            var sessions = context.HttpContext.RequestServices.GetRequiredService<AdminSessionStore>();
            if (context.Principal is null || !sessions.IsActive(context.Principal))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<AdminSessionStore>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("admin-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(15),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddSingleton(new AdminPasswordVerifier(adminPassword));
builder.Services.AddSingleton(keyProtector);
builder.Services.AddHttpClient("Perplexity", client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient("Polza", client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton(sp => new PerplexityProviderAdapter(
    sp.GetRequiredService<IHttpClientFactory>(), providerTimeoutSeconds));
builder.Services.AddSingleton(sp => new PolzaProviderAdapter(
    sp.GetRequiredService<IHttpClientFactory>(), providerTimeoutSeconds));
builder.Services.AddSingleton<IAiProviderAdapter>(sp => sp.GetRequiredService<PerplexityProviderAdapter>());
builder.Services.AddSingleton<ISupplierDiscoveryAdapter>(sp => sp.GetRequiredService<PerplexityProviderAdapter>());
builder.Services.AddSingleton<IAiProviderAdapter>(sp => sp.GetRequiredService<PolzaProviderAdapter>());
builder.Services.AddSingleton<ISupplierDiscoveryAdapter>(sp => sp.GetRequiredService<PolzaProviderAdapter>());
builder.Services.AddSingleton<IAiProviderRegistry, AiProviderRegistry>();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<ISupplierDiscoveryProvider, ConfiguredSupplierDiscoveryProvider>();
builder.Services.AddScoped<DiscoveryPersistence>();

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

app.UseForwardedHeaders();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<CsrfProtectionMiddleware>();

app.MapOpenApi("/openapi/{documentName}.json");

var api = app.MapGroup("/api/v1");

api.MapGet("/health/ready", ReadyAsync)
    .WithName("GetReadiness")
    .WithTags("Health")
    .WithSummary("Проверяет готовность API и доступность PostgreSQL")
    .Produces(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .AllowAnonymous();

api.MapGet("/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { csrfToken = tokens.RequestToken });
    })
    .AllowAnonymous()
    .WithName("GetCsrfToken")
    .WithTags("Authentication")
    .Produces(StatusCodes.Status200OK);

var routes = api.MapGroup("").RequireAuthorization();
routes.MapPost("/auth/login", LoginAsync)
    .AllowAnonymous()
    .RequireRateLimiting("admin-login")
    .WithName("Login")
    .WithTags("Authentication")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status429TooManyRequests);
routes.MapPost("/auth/logout", LogoutAsync)
    .WithName("Logout")
    .WithTags("Authentication")
    .Produces(StatusCodes.Status204NoContent)
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden);
routes.MapGet("/auth/session", (HttpContext context) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new { authenticated = true });
    })
    .WithName("GetSession")
    .WithTags("Authentication")
    .Produces(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status401Unauthorized);

routes.MapAiSettingsEndpoints();
routes.MapDiscoveryEndpoints();
routes.MapSupplierCatalogEndpoints();

app.Run();

static async Task<IResult> LoginAsync(
    LoginRequest request,
    HttpContext context,
    AdminPasswordVerifier passwordVerifier,
    AdminSessionStore sessions)
{
    if (request.Password is null || request.Password.Length > 1024 || !passwordVerifier.IsValid(request.Password))
        return ProblemResponses.Create(context, StatusCodes.Status401Unauthorized,
            "Неверный пароль администратора", "INVALID_CREDENTIALS");

    var identity = sessions.CreateIdentity();
    var principal = new System.Security.Claims.ClaimsPrincipal(identity);
    try
    {
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });
    }
    catch
    {
        sessions.Revoke(principal);
        throw;
    }
    context.Response.Headers.CacheControl = "no-store";
    return Results.NoContent();
}

static async Task<IResult> LogoutAsync(HttpContext context, AdminSessionStore sessions)
{
    sessions.Revoke(context.User);
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    context.Response.Headers.CacheControl = "no-store";
    return Results.NoContent();
}

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
