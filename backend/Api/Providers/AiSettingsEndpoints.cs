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

        settings.MapGet("/providers", GetProvidersAsync)
            .WithName("GetAiProviders");

        settings.MapGet("/settings", (ApplicationDbContext db, HttpContext context,
                CancellationToken cancellationToken) => GetSettingsAsync(db, context, cancellationToken))
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
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        return routes;
    }

    private static async Task<IResult> GetProvidersAsync(ApplicationDbContext db, IAiProviderRegistry registry,
        HttpContext context, CancellationToken cancellationToken)
    {
        var prompt = await db.AiProviderPromptSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProviderId == "discovery", cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        var items = registry.WebSearchProviders.Select(provider =>
        {
            return new ProviderResponse(provider.Id, provider.DisplayName, true, provider.SupportedModels,
                provider.SupportsFreeformModel, provider.SupportsProviderRouting,
                prompt?.Prompt ?? AiProviderPromptDefaults.Shared, AiProviderPromptDefaults.Shared);
        });
        return Results.Ok(new { items });
    }

    private static async Task<IResult> GetSettingsAsync(ApplicationDbContext db,
        HttpContext context, CancellationToken cancellationToken)
    {
        var setting = await db.AiProviderSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == AiProviderSetting.SingletonId, cancellationToken);
        var promptSetting = await db.AiProviderPromptSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProviderId == "discovery", cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(ToResponse(setting, promptSetting?.Prompt ?? AiProviderPromptDefaults.Shared));
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
        if (!provider.SupportsModel(request.Model))
            return ProblemResponses.Create(context, StatusCodes.Status400BadRequest, "Модель не поддерживается выбранным провайдером", "UNSUPPORTED_MODEL");
        if (request.BasePrompt is { } requestedPrompt &&
            (string.IsNullOrWhiteSpace(requestedPrompt) || requestedPrompt.Length > 8000))
            return ProblemResponses.Create(context, StatusCodes.Status400BadRequest,
                "Промпт должен содержать текст и не превышать 8000 символов", "VALIDATION_ERROR");

        var routeProvider = string.IsNullOrWhiteSpace(request.RouteProvider) ? null : request.RouteProvider.Trim();
        if (routeProvider is { Length: > 120 } || routeProvider?.Any(char.IsControl) == true ||
            (routeProvider is not null && !provider.SupportsProviderRouting))
            return ProblemResponses.Create(context, StatusCodes.Status400BadRequest,
                "Провайдер маршрутизации не поддерживается или указан некорректно", "VALIDATION_ERROR");

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
        var promptSetting = await db.AiProviderPromptSettings
            .SingleOrDefaultAsync(item => item.ProviderId == "discovery", cancellationToken);
        if (AiSettingsKeyPolicy.RequiresNewKey(setting, request.ProviderId, apiKey))
            return ProblemResponses.Create(context, StatusCodes.Status409Conflict,
                "Для этого провайдера требуется новый API-ключ", "API_KEY_REQUIRED");

        var encryptedApiKey = AiSettingsKeyPolicy.SelectEncryptedKey(setting, apiKey, keyProtector);

        var now = DateTimeOffset.UtcNow;
        var basePrompt = request.BasePrompt ?? promptSetting?.Prompt ?? AiProviderPromptDefaults.Shared;
        if (string.IsNullOrWhiteSpace(basePrompt) || basePrompt.Length > 8000)
            return ProblemResponses.Create(context, StatusCodes.Status400BadRequest,
                "Для провайдера не настроен корректный базовый промпт", "VALIDATION_ERROR");

        if (setting is null)
            db.AiProviderSettings.Add(new AiProviderSetting(request.ProviderId, request.Model, encryptedApiKey, now, routeProvider));
        else
            setting.Update(request.ProviderId, request.Model, encryptedApiKey, now, routeProvider);

        if (promptSetting is null)
            db.AiProviderPromptSettings.Add(new AiProviderPromptSetting("discovery", basePrompt, now));
        else if (request.BasePrompt is not null)
            promptSetting.Update(basePrompt, now);

        await db.SaveChangesAsync(cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(ToResponse(setting ?? await db.AiProviderSettings.AsNoTracking()
            .SingleAsync(item => item.Id == AiProviderSetting.SingletonId, cancellationToken), basePrompt));
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
        SupplierDiscoveryService discoveryService,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var setting = await db.AiProviderSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == AiProviderSetting.SingletonId, cancellationToken);
        if (setting?.EncryptedApiKey is null)
            return ProblemResponses.Create(context, StatusCodes.Status409Conflict,
                "Сначала выберите провайдера и сохраните API-ключ", "PROVIDER_NOT_CONFIGURED");

        var provider = registry.FindWebSearchProvider(setting.ProviderId) as IAiSearchTransport;
        if (provider is null)
            return ProblemResponses.Create(context, StatusCodes.Status502BadGateway,
                "Сохранённый адаптер провайдера недоступен на сервере", "PROVIDER_UNAVAILABLE");

        try
        {
            var apiKey = keyProtector.Unprotect(setting.EncryptedApiKey);
            var prompt = await db.AiProviderPromptSettings.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ProviderId == "discovery", cancellationToken);
            var result = await discoveryService.DiscoverAsync(provider, setting.Model, apiKey, setting.RouteProvider,
                prompt?.Prompt ?? AiProviderPromptDefaults.Shared, "поставщик продуктов питания оптом",
                new SupplierDiscoveryFilters(), 1, cancellationToken);
            if (result.EvidenceCount == 0)
                return ProblemResponses.Create(context, StatusCodes.Status502BadGateway,
                    "Провайдер не вернул проверяемые веб-источники", "PROVIDER_UNAVAILABLE");

            return Results.Ok(new ProviderCheckResponse(true, true, DateTimeOffset.UtcNow));
        }
        catch (AiProviderException exception)
        {
            return ToProviderProblem(context, exception.Code);
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

    private static AiSettingsResponse ToResponse(AiProviderSetting? setting, string? basePrompt) => setting is null
        ? new AiSettingsResponse(null, null, null, basePrompt, false, null, null)
        : new AiSettingsResponse(setting.ProviderId, setting.Model, setting.RouteProvider, basePrompt,
            setting.EncryptedApiKey is not null, setting.EncryptedApiKey is null ? null : "••••••••", setting.UpdatedAt);

    private static IResult ToProviderProblem(HttpContext context, ProviderFailureCode code) => code switch
    {
        ProviderFailureCode.NotConfigured => ProblemResponses.Create(context, StatusCodes.Status409Conflict,
            "Сначала выберите провайдера и сохраните API-ключ", "PROVIDER_NOT_CONFIGURED"),
        ProviderFailureCode.UnsupportedModel => ProblemResponses.Create(context, StatusCodes.Status400BadRequest,
            "Модель не поддерживается выбранным провайдером", "UNSUPPORTED_MODEL"),
        ProviderFailureCode.Timeout => ProblemResponses.Create(context, StatusCodes.Status504GatewayTimeout,
            "Время проверки провайдера истекло", "PROVIDER_TIMEOUT"),
        ProviderFailureCode.InvalidResponse => ProblemResponses.Create(context, StatusCodes.Status502BadGateway,
            "Провайдер вернул некорректный ответ", "PROVIDER_INVALID_RESPONSE"),
        _ => ProblemResponses.Create(context, StatusCodes.Status502BadGateway,
            "Не удалось проверить подключение к провайдеру", "PROVIDER_UNAVAILABLE")
    };

    private sealed record ProviderResponse(string Id, string DisplayName, bool SupportsWebSearch,
        IReadOnlyCollection<string> Models, bool SupportsFreeformModel, bool SupportsProviderRouting,
        string BasePrompt, string DefaultBasePrompt);
}

public sealed record UpdateAiSettingsRequest(string? ProviderId, string? Model, JsonElement? ApiKey,
    string? RouteProvider = null, string? BasePrompt = null);
public sealed record AiSettingsResponse(string? ProviderId, string? Model, string? RouteProvider, string? BasePrompt,
    bool HasApiKey, string? ApiKeyMask, DateTimeOffset? UpdatedAt);
public sealed record ProviderCheckResponse(bool Connected, bool WebSearchAvailable, DateTimeOffset CheckedAt);
