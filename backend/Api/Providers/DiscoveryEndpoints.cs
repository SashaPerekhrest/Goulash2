using System.Text.Json;
using Goulash.Api.Errors;
using Goulash.Application;
using Goulash.Domain;
using Goulash.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
public sealed record DiscoveryJobAccepted(Guid DiscoveryId, string Status);
public sealed record DiscoveryJobStatusResponse(Guid DiscoveryId, string Status, string Stage, int CandidateCount,
    int CompletedCandidates, int AcceptedCount, int FailedProfileCount, string? ErrorCode,
    SupplierDiscoveryResponse? Result);

public static class DiscoveryEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapDiscoveryEndpoints(this RouteGroupBuilder routes)
    {
        routes.MapPost("/discoveries", DiscoverAsync)
            .WithName("DiscoverSuppliers")
            .WithTags("Discoveries")
            .WithSummary("Ищет поставщиков с учётом фильтров и сохраняет профили сайтов")
            .Produces<DiscoveryJobAccepted>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);
        routes.MapGet("/discoveries/{id:guid}", GetDiscoveryAsync)
            .WithName("GetDiscovery")
            .WithTags("Discoveries")
            .Produces<DiscoveryJobStatusResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
        return routes;
    }

    private static async Task<IResult> DiscoverAsync(DiscoveryRequest? request, HttpContext context,
        DiscoveryJobQueue queue, ApplicationDbContext db,
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

        var runQuery = JsonSerializer.SerializeToElement(new { query, filters }, JsonOptions);
        var run = new DiscoveryRun(runQuery, DateTimeOffset.UtcNow);
        await db.DiscoveryRuns.AddAsync(run, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await queue.EnqueueAsync(run.Id);
        return Results.Accepted($"/api/v1/discoveries/{run.Id}", new DiscoveryJobAccepted(run.Id, run.Status));
    }

    private static async Task<IResult> GetDiscoveryAsync(Guid id, HttpContext context, ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var run = await db.DiscoveryRuns.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (run is null)
            return ProblemResponses.Create(context, StatusCodes.Status404NotFound, "Поиск не найден", "NOT_FOUND");

        SupplierDiscoveryResponse? result = null;
        if (!string.IsNullOrWhiteSpace(run.ResultJson))
            result = JsonSerializer.Deserialize<SupplierDiscoveryResponse>(run.ResultJson, JsonOptions);
        return Results.Ok(new DiscoveryJobStatusResponse(run.Id, run.Status, run.Stage,
            run.CandidateCount, run.CompletedCandidates, run.AcceptedCount, run.FailedProfileCount, run.ErrorCode, result));
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
        SupplierFactNormalizer.TryDecimal(value, out number) && number >= 0;

    private static bool HasSubstantiveFilter(SupplierDiscoveryFilters filters) =>
        !string.IsNullOrWhiteSpace(filters.City) || !string.IsNullOrWhiteSpace(filters.Region) ||
        !string.IsNullOrWhiteSpace(filters.Category) || !string.IsNullOrWhiteSpace(filters.Product) ||
        filters.Price?.Min.HasValue == true || filters.Price?.Max.HasValue == true || filters.MaxDeliveryDays.HasValue ||
        filters.MinMinimumOrder is not null || filters.MaxMinimumOrder is not null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult ValidationError(HttpContext context, string detail) =>
        ProblemResponses.Create(context, StatusCodes.Status400BadRequest,
            "Некорректные параметры поиска", "VALIDATION_ERROR", detail);

}
