using System.Text.Json;
using Goulash.Api.Errors;
using Goulash.Domain;
using Goulash.Infrastructure.Persistence;
using Goulash.Application;
using Microsoft.EntityFrameworkCore;

namespace Goulash.Api.Providers;

public static class AiSettingsEndpoints
{
    public static RouteGroupBuilder MapAiSettingsEndpoints(this RouteGroupBuilder routes)
    {
        var settings = routes.MapGroup("/ai").WithTags("AI settings");

        settings.MapGet("/providers", (IAiProviderRegistry registry) =>
                Results.Ok(new { items = registry.WebSearchProviders.Select(provider => new ProviderResponse(provider.Id, provider.DisplayName, true)) }))
            .WithName("GetAiProviders");

        settings.MapGet("/settings", (ApplicationDbContext db, HttpContext context, CancellationToken cancellationToken) =>
                GetSettingsAsync(db, context, cancellationToken))
            .WithName("GetAiSettings")
            .Produces<AiSettingsResponse>(StatusCodes.Status200OK);

        settings.MapPut("/settings", UpdateSettingsAsync)
            .WithName("UpdateAiSettings")
            .Produces<AiSettingsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        settings.MapDelete("/settings/key", DeleteApiKeyAsync)
            .WithName("DeleteAiApiKey")
            .Produces(StatusCodes.Status204NoContent);

        settings.MapPost("/settings/check", CheckSettingsAsync)
            .WithName("CheckAiSettings")
            .Produces<ProviderCheckResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        return routes;
    }

    private static async Task<IResult> GetSettingsAsync(ApplicationDbContext db, HttpContext context, CancellationToken cancellationToken)
    {
        var setting = await db.AiProviderSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == AiProviderSetting.SingletonId, cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(ToResponse(setting));
    }

    private static async Task<IResult> UpdateSettingsAsync(
        UpdateAiSettingsRequest request,
        ApplicationDbContext db,
        IAiProviderRegistry registry,
        AiApiKeyProtector keyProtector,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderId) || request.ProviderId.Length > 120)
            return ProblemResponses.Create(context, StatusCodes.Status400BadRequest, "Укажите поддерживаемого провайдера", "VALIDATION_ERROR");
        if (string.IsNullOrWhiteSpace(request.Model) || request.Model.Length > 200)
            return ProblemResponses.Create(context, StatusCodes.Status400BadRequest, "Укажите модель длиной не более 200 символов", "VALIDATION_ERROR");

        var provider = registry.FindWebSearchProvider(request.ProviderId);
        if (provider is null)
            return ProblemResponses.Create(context, StatusCodes.Status400BadRequest, "Неизвестный или неподдерживаемый провайдер", "UNSUPPORTED_PROVIDER");

        string? apiKey = null;
        if (request.ApiKey.HasValue)
        {
            if (request.ApiKey.Value.ValueKind != JsonValueKind.String)
                return ProblemResponses.Create(context, StatusCodes.Status400BadRequest, "apiKey должен быть непустой строкой", "VALIDATION_ERROR");

            apiKey = request.ApiKey.Value.GetString();
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 8192)
                return ProblemResponses.Create(context, StatusCodes.Status400BadRequest, "apiKey должен быть непустой строкой длиной не более 8192 символов", "VALIDATION_ERROR");
        }

        var setting = await db.AiProviderSettings
            .SingleOrDefaultAsync(item => item.Id == AiProviderSetting.SingletonId, cancellationToken);
        if (AiSettingsKeyPolicy.RequiresNewKey(setting, request.ProviderId, apiKey))
            return ProblemResponses.Create(context, StatusCodes.Status409Conflict,
                "Для этого провайдера требуется новый API-ключ", "API_KEY_REQUIRED");

        var encryptedApiKey = AiSettingsKeyPolicy.SelectEncryptedKey(setting, apiKey, keyProtector);

        var now = DateTimeOffset.UtcNow;
        if (setting is null)
            db.AiProviderSettings.Add(new AiProviderSetting(request.ProviderId, request.Model, encryptedApiKey, now));
        else
            setting.Update(request.ProviderId, request.Model, encryptedApiKey, now);

        await db.SaveChangesAsync(cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(ToResponse(setting ?? await db.AiProviderSettings.AsNoTracking()
            .SingleAsync(item => item.Id == AiProviderSetting.SingletonId, cancellationToken)));
    }

    private static async Task<IResult> DeleteApiKeyAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var setting = await db.AiProviderSettings
            .SingleOrDefaultAsync(item => item.Id == AiProviderSetting.SingletonId, cancellationToken);
        if (setting is not null && setting.EncryptedApiKey is not null)
        {
            setting.RemoveApiKey(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> CheckSettingsAsync(
        ApplicationDbContext db,
        IAiProviderRegistry registry,
        AiApiKeyProtector keyProtector,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var setting = await db.AiProviderSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == AiProviderSetting.SingletonId, cancellationToken);
        if (setting?.EncryptedApiKey is null)
            return ProblemResponses.Create(context, StatusCodes.Status409Conflict,
                "Сначала выберите провайдера и сохраните API-ключ", "PROVIDER_NOT_CONFIGURED");

        var provider = registry.FindWebSearchProvider(setting.ProviderId);
        if (provider is null)
            return ProblemResponses.Create(context, StatusCodes.Status502BadGateway,
                "Сохранённый адаптер провайдера недоступен на сервере", "PROVIDER_UNAVAILABLE");

        try
        {
            var apiKey = keyProtector.Unprotect(setting.EncryptedApiKey);
            var result = await provider.CheckConnectionAsync(setting.Model, apiKey, cancellationToken);
            if (!result.Connected || !result.WebSearchAvailable)
                return ProblemResponses.Create(context, StatusCodes.Status502BadGateway,
                    "Провайдер не подтвердил соединение и доступ к веб-поиску", "PROVIDER_UNAVAILABLE");

            return Results.Ok(new ProviderCheckResponse(true, true, DateTimeOffset.UtcNow));
        }
        catch (TimeoutException)
        {
            return ProblemResponses.Create(context, StatusCodes.Status504GatewayTimeout,
                "Время проверки провайдера истекло", "PROVIDER_TIMEOUT");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ProblemResponses.Create(context, StatusCodes.Status502BadGateway,
                "Не удалось проверить подключение к провайдеру", "PROVIDER_UNAVAILABLE");
        }
    }

    private static AiSettingsResponse ToResponse(AiProviderSetting? setting) => setting is null
        ? new AiSettingsResponse(null, null, false, null, null)
        : new AiSettingsResponse(setting.ProviderId, setting.Model, setting.EncryptedApiKey is not null,
            setting.EncryptedApiKey is null ? null : "••••••••", setting.UpdatedAt);

    private sealed record ProviderResponse(string Id, string DisplayName, bool SupportsWebSearch);
}

public sealed record UpdateAiSettingsRequest(string? ProviderId, string? Model, JsonElement? ApiKey);
public sealed record AiSettingsResponse(string? ProviderId, string? Model, bool HasApiKey, string? ApiKeyMask, DateTimeOffset? UpdatedAt);
public sealed record ProviderCheckResponse(bool Connected, bool WebSearchAvailable, DateTimeOffset CheckedAt);
