using System.Text.Json;
using Goulash.Api.Errors;
using Goulash.Application;
using Goulash.Domain;
using Goulash.Infrastructure.Persistence;

namespace Goulash.Api.Providers;

public sealed record DiscoveryRequest(string? Query, DiscoveryFiltersRequest? Filters);
public sealed record DiscoveryFiltersRequest(
    string? City,
    string? Region,
    string? Category,
    string? Product,
    DiscoveryPriceRequest? Price,
    bool? IncludeApproximatePrices,
    int? MaxDeliveryDays,
    DiscoveryMinimumOrderRequest? MinMinimumOrder,
    DiscoveryMinimumOrderRequest? MaxMinimumOrder);
public sealed record DiscoveryPriceRequest(JsonElement? Min, JsonElement? Max, string? Currency, string? Unit);
public sealed record DiscoveryMinimumOrderRequest(JsonElement? Amount, string? Unit);

public static class DiscoveryEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapDiscoveryEndpoints(this RouteGroupBuilder routes)
    {
        routes.MapPost("/discoveries", DiscoverAsync)
            .WithName("DiscoverSuppliers")
            .WithTags("Discoveries")
            .WithSummary("Ищет новых поставщиков, проверяет фильтры и сохраняет до пяти записей")
            .Produces<SupplierDiscoveryResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);
        return routes;
    }

    private static async Task<IResult> DiscoverAsync(DiscoveryRequest? request, HttpContext context,
        ISupplierDiscoveryProvider provider, DiscoveryPersistence persistence, ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return ValidationError(context, "Тело запроса обязательно");

        if (request.Query?.Length > 500)
            return ValidationError(context, "query не должен быть длиннее 500 символов");

        if (!TryBuildFilters(request.Filters, out var filters, out var filterError))
            return ValidationError(context, filterError!);

        var query = request.Query?.Trim() ?? string.Empty;
        if (query.Length == 0 && !HasSubstantiveFilter(filters!))
            return ValidationError(context, "Укажите query или хотя бы один содержательный фильтр");

        var runQuery = JsonSerializer.SerializeToElement(new { query, filters = request.Filters }, JsonOptions);
        var run = new DiscoveryRun(runQuery, DateTimeOffset.UtcNow);
        SupplierDiscoveryResult discovery;
        try
        {
            discovery = await provider.DiscoverAsync(query, filters!, 5, cancellationToken);
            if (discovery is null || discovery.Candidates is null || discovery.RejectedRecordCount < 0)
                throw new AiProviderException(ProviderFailureCode.InvalidResponse);
        }
        catch (AiProviderException exception)
        {
            var error = MapProviderFailure(exception.Code);
            run.Fail(error.Code, DateTimeOffset.UtcNow);
            await PersistFailedRunAsync(db, run, cancellationToken);
            return ProblemResponses.Create(context, error.Status, error.Title, error.Code);
        }

        var matching = new List<SupplierDiscoveryCandidate>();
        var rejectedByFilters = 0;
        foreach (var candidate in discovery.Candidates)
        {
            if (candidate is null)
            {
                rejectedByFilters++;
                continue;
            }
            if (!DiscoveryPersistence.IsEvidenceCheckedCandidateForRoute(candidate) ||
                !SupplierDiscoveryMatcher.Matches(candidate, filters!))
                rejectedByFilters++;
            else
                matching.Add(candidate);
        }

        var response = await persistence.SaveAsync(run, matching, discovery.RejectedRecordCount,
            rejectedByFilters, cancellationToken);
        return Results.Ok(response);
    }

    private static bool TryBuildFilters(DiscoveryFiltersRequest? request,
        out SupplierDiscoveryFilters? filters, out string? error)
    {
        filters = null;
        error = null;
        request ??= new DiscoveryFiltersRequest(null, null, null, null, null, null, null, null, null);

        foreach (var (name, value, maxLength) in new[]
        {
            ("filters.city", request.City, 160),
            ("filters.region", request.Region, 160),
            ("filters.category", request.Category, 160),
            ("filters.product", request.Product, 500)
        })
        {
            if (value?.Length > maxLength)
            {
                error = $"{name} не должен быть длиннее {maxLength} символов";
                return false;
            }
        }

        SupplierPriceRange? price = null;
        if (request.Price is not null)
        {
            decimal? minimum = null;
            decimal? maximum = null;
            if (request.Price.Min.HasValue)
            {
                if (!TryReadNonNegative(request.Price.Min.Value, out var value))
                {
                    error = "filters.price.min должен быть неотрицательным числом";
                    return false;
                }
                minimum = value;
            }
            if (request.Price.Max.HasValue)
            {
                if (!TryReadNonNegative(request.Price.Max.Value, out var value))
                {
                    error = "filters.price.max должен быть неотрицательным числом";
                    return false;
                }
                maximum = value;
            }
            if (minimum.HasValue || maximum.HasValue)
            {
                if (string.IsNullOrWhiteSpace(request.Price.Currency) || request.Price.Currency.Trim().Length != 3 ||
                    !request.Price.Currency.Trim().All(char.IsAsciiLetter))
                {
                    error = "Для фильтра цены требуется трёхбуквенный код currency";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(request.Price.Unit) || request.Price.Unit.Trim().Length > 40)
                {
                    error = "Для фильтра цены требуется unit длиной до 40 символов";
                    return false;
                }
                if (minimum.HasValue && maximum.HasValue && minimum > maximum)
                {
                    error = "filters.price.max должен быть не меньше filters.price.min";
                    return false;
                }
                price = new SupplierPriceRange(minimum, maximum,
                    SupplierFactNormalizer.NormalizeCurrency(request.Price.Currency.Trim()).ToUpperInvariant(),
                    SupplierFactNormalizer.NormalizeUnit(request.Price.Unit.Trim()));
            }
        }

        if (request.MaxDeliveryDays is < 0)
        {
            error = "filters.maxDeliveryDays должен быть неотрицательным числом";
            return false;
        }

        if (!TryReadOrderBound("filters.minMinimumOrder", request.MinMinimumOrder, out var minOrder, out error) ||
            !TryReadOrderBound("filters.maxMinimumOrder", request.MaxMinimumOrder, out var maxOrder, out error))
            return false;
        if (minOrder is not null && maxOrder is not null &&
            !string.Equals(minOrder.Unit, maxOrder.Unit, StringComparison.OrdinalIgnoreCase))
        {
            error = "Единицы minMinimumOrder и maxMinimumOrder должны совпадать";
            return false;
        }
        if (minOrder is not null && maxOrder is not null && minOrder.Amount > maxOrder.Amount)
        {
            error = "maxMinimumOrder.amount должен быть не меньше minMinimumOrder.amount";
            return false;
        }

        filters = new SupplierDiscoveryFilters(
            Clean(request.City), Clean(request.Region), Clean(request.Category), Clean(request.Product), price,
            request.IncludeApproximatePrices ?? false, request.MaxDeliveryDays, minOrder, maxOrder);
        return true;
    }

    private static bool TryReadOrderBound(string property, DiscoveryMinimumOrderRequest? request,
        out SupplierMinimumOrderBound? bound, out string? error)
    {
        bound = null;
        error = null;
        if (request is null) return true;
        if (!request.Amount.HasValue || !TryReadNonNegative(request.Amount.Value, out var amount))
        {
            error = $"{property}.amount должен быть неотрицательным числом";
            return false;
        }
        if (string.IsNullOrWhiteSpace(request.Unit) || request.Unit.Trim().Length > 40)
        {
            error = $"{property}.unit обязателен и не должен быть длиннее 40 символов";
            return false;
        }
        bound = new SupplierMinimumOrderBound(amount, SupplierFactNormalizer.NormalizeUnit(request.Unit.Trim()));
        return true;
    }

    private static bool TryReadNonNegative(JsonElement value, out decimal number) =>
        SupplierDiscoveryMatcher.TryDecimal(value, out number) && number >= 0;

    private static bool HasSubstantiveFilter(SupplierDiscoveryFilters filters) =>
        !string.IsNullOrWhiteSpace(filters.City) || !string.IsNullOrWhiteSpace(filters.Region) ||
        !string.IsNullOrWhiteSpace(filters.Category) || !string.IsNullOrWhiteSpace(filters.Product) ||
        filters.Price?.Min.HasValue == true || filters.Price?.Max.HasValue == true || filters.MaxDeliveryDays.HasValue ||
        filters.MinMinimumOrder is not null || filters.MaxMinimumOrder is not null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult ValidationError(HttpContext context, string detail) =>
        ProblemResponses.Create(context, StatusCodes.Status400BadRequest,
            "Некорректные параметры поиска", "VALIDATION_ERROR", detail);

    private static async Task PersistFailedRunAsync(ApplicationDbContext db, DiscoveryRun run,
        CancellationToken cancellationToken)
    {
        db.DiscoveryRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static (int Status, string Code, string Title) MapProviderFailure(ProviderFailureCode code) => code switch
    {
        ProviderFailureCode.NotConfigured => (StatusCodes.Status409Conflict, "PROVIDER_NOT_CONFIGURED", "Провайдер не настроен"),
        ProviderFailureCode.Timeout => (StatusCodes.Status504GatewayTimeout, "PROVIDER_TIMEOUT", "Превышено время ожидания провайдера"),
        ProviderFailureCode.InvalidResponse => (StatusCodes.Status502BadGateway, "PROVIDER_INVALID_RESPONSE", "Провайдер вернул некорректный ответ"),
        ProviderFailureCode.UnsupportedModel => (StatusCodes.Status400BadRequest, "UNSUPPORTED_MODEL", "Модель провайдера не поддерживается"),
        _ => (StatusCodes.Status502BadGateway, "PROVIDER_UNAVAILABLE", "Провайдер временно недоступен")
    };
}
