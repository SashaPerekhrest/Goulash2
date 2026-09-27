using System.Globalization;
using System.Text.Json;
using Goulash.Application;
using Goulash.Api.Errors;
using Goulash.Domain;
using Goulash.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Goulash.Api.Providers;

public sealed record SupplierCatalogCard(
    Guid Id,
    string Name,
    string? City,
    IReadOnlyList<string> Products,
    string? PricePreview,
    bool PriceIsApproximate,
    string? DeliveryPreview,
    string? WebsiteUrl,
    string? ContactPreview,
    bool IsFavorite,
    bool HasUnconfirmedData,
    DateTimeOffset? LastDiscoveredAt);

public sealed record SupplierCatalogPage(
    IReadOnlyList<SupplierCatalogCard> Items,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages);

public sealed record SupplierFavoriteRequest(bool? IsFavorite);
public sealed record SupplierNoteRequest(JsonElement Note);

internal sealed record SupplierCatalogFilters(
    string? Query,
    string? City,
    string? Region,
    string? Category,
    string? Product,
    decimal? PriceMinimum,
    decimal? PriceMaximum,
    string? Currency,
    string? Unit,
    bool IncludeApproximatePrices,
    int? MaximumDeliveryDays,
    decimal? MinimumOrderMinimum,
    decimal? MinimumOrderMaximum,
    string? MinimumOrderUnit,
    bool FavoriteOnly,
    string Sort,
    int Page,
    int PageSize);

public static class SupplierCatalogEndpoints
{
    public static RouteGroupBuilder MapSupplierCatalogEndpoints(this RouteGroupBuilder routes)
    {
        routes.MapGet("/suppliers", ListAsync)
            .WithName("ListSuppliers")
            .WithTags("Suppliers")
            .WithSummary("Фильтрует и постранично возвращает сохранённых поставщиков")
            .Produces<SupplierCatalogPage>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        routes.MapGet("/suppliers/{id:guid}", GetAsync)
            .WithName("GetSupplier")
            .WithTags("Suppliers")
            .WithSummary("Возвращает детали поставщика и происхождение его данных")
            .Produces<SupplierDetailsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.MapPut("/suppliers/{id:guid}/favorite", SetFavoriteAsync)
            .WithName("SetSupplierFavorite")
            .WithTags("Suppliers")
            .WithSummary("Устанавливает состояние избранного для поставщика")
            .Produces<SupplierFavoriteResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        routes.MapPut("/suppliers/{id:guid}/note", SetNoteAsync)
            .WithName("SetSupplierNote")
            .WithTags("Suppliers")
            .WithSummary("Сохраняет или удаляет общую заметку о поставщике")
            .Produces<SupplierNoteResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    private static async Task<IResult> ListAsync(HttpContext context, ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        if (!TryBuildFilters(context.Request.Query, out var filters, out var error))
            return ValidationError(context, error!);

        // The unfiltered path counts and pages in PostgreSQL. Filtered requests scan ordered IDs in
        // bounded batches, because fact matching uses the same normalization as discovery.
        var candidates = db.Suppliers.AsNoTracking().AsQueryable();
        if (filters!.FavoriteOnly) candidates = candidates.Where(supplier => supplier.IsFavorite);
        var orderedIds = filters.Sort switch
        {
            "created_asc" => candidates.OrderBy(supplier => supplier.CreatedAt).ThenBy(supplier => supplier.Id)
                .Select(supplier => supplier.Id),
            "name_asc" => candidates.OrderBy(supplier => supplier.Name.ToLower()).ThenBy(supplier => supplier.Id)
                .Select(supplier => supplier.Id),
            "name_desc" => candidates.OrderByDescending(supplier => supplier.Name.ToLower())
                .ThenByDescending(supplier => supplier.Id).Select(supplier => supplier.Id),
            _ => candidates.OrderByDescending(supplier => supplier.CreatedAt).ThenByDescending(supplier => supplier.Id)
                .Select(supplier => supplier.Id)
        };
        var requestedOffset = ((long)filters.Page - 1) * filters.PageSize;
        var hasFactFilters = filters.Query is not null || filters.City is not null || filters.Region is not null ||
            filters.Category is not null || filters.Product is not null || filters.PriceMinimum.HasValue ||
            filters.PriceMaximum.HasValue || filters.MaximumDeliveryDays.HasValue ||
            filters.MinimumOrderMinimum.HasValue || filters.MinimumOrderMaximum.HasValue;

        int totalItems;
        var pageItems = new List<SupplierCatalogCard>(filters.PageSize);
        if (!hasFactFilters)
        {
            totalItems = await candidates.CountAsync(cancellationToken);
            if (requestedOffset <= int.MaxValue)
            {
                var ids = await orderedIds.Skip((int)requestedOffset).Take(filters.PageSize)
                    .ToArrayAsync(cancellationToken);
                var suppliers = await LoadCardSuppliersAsync(db, ids, cancellationToken);
                pageItems.AddRange(ids.Select(id => SupplierCatalogMapper.ToCard(suppliers[id])));
            }
        }
        else
        {
            const int batchSize = 100;
            totalItems = 0;
            for (var offset = 0; ; offset += batchSize)
            {
                var ids = await orderedIds.Skip(offset).Take(batchSize).ToArrayAsync(cancellationToken);
                if (ids.Length == 0) break;
                var suppliers = await LoadCardSuppliersAsync(db, ids, cancellationToken,
                    includeSearchEvidence: filters.Query is not null);
                foreach (var id in ids)
                {
                    var supplier = suppliers[id];
                    if (!SupplierCatalogMapper.Matches(supplier, filters)) continue;
                    if (totalItems >= requestedOffset && pageItems.Count < filters.PageSize)
                        pageItems.Add(SupplierCatalogMapper.ToCard(supplier));
                    totalItems++;
                }
                if (ids.Length < batchSize) break;
            }
        }

        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)filters.PageSize);
        return Results.Ok(new SupplierCatalogPage(pageItems, filters.Page, filters.PageSize, totalItems, totalPages));
    }

    private static async Task<Dictionary<Guid, Supplier>> LoadCardSuppliersAsync(ApplicationDbContext db,
        Guid[] ids, CancellationToken cancellationToken, bool includeSearchEvidence = false)
    {
        if (ids.Length == 0) return new Dictionary<Guid, Supplier>();
        IQueryable<Supplier> query = db.Suppliers.AsNoTracking().AsSplitQuery()
            .Where(supplier => ids.Contains(supplier.Id))
            .Include(supplier => supplier.Facts)
            .Include(supplier => supplier.Products)
                .ThenInclude(product => product.Prices)
                    .ThenInclude(price => price.Fact);
        if (includeSearchEvidence)
        {
            query = query.Include(supplier => supplier.Sources)
                .Include(supplier => supplier.Facts)
                    .ThenInclude(fact => fact.FactSources)
                        .ThenInclude(link => link.Source);
        }
        return await query.ToDictionaryAsync(supplier => supplier.Id, cancellationToken);
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext context, ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var supplier = await db.Suppliers.AsNoTracking().AsSplitQuery()
            .Include(item => item.Sources)
            .Include(item => item.Facts)
                .ThenInclude(fact => fact.FactSources)
                    .ThenInclude(link => link.Source)
            .Include(item => item.Products)
                .ThenInclude(product => product.Prices)
                    .ThenInclude(price => price.Fact)
                        .ThenInclude(fact => fact.FactSources)
                            .ThenInclude(link => link.Source)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

        return supplier is null
            ? ProblemResponses.Create(context, StatusCodes.Status404NotFound, "Поставщик не найден", "NOT_FOUND")
            : Results.Ok(SupplierCatalogMapper.ToDetails(supplier));
    }

    private static async Task<IResult> SetFavoriteAsync(Guid id, SupplierFavoriteRequest? request,
        HttpContext context, ApplicationDbContext db, CancellationToken cancellationToken)
    {
        if (request is null)
            return ValidationError(context, "Тело запроса обязательно");
        if (!request.IsFavorite.HasValue)
            return ValidationError(context, "isFavorite должен быть логическим значением");

        var supplier = await db.Suppliers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (supplier is null)
            return ProblemResponses.Create(context, StatusCodes.Status404NotFound, "Поставщик не найден", "NOT_FOUND");

        supplier.SetFavorite(request.IsFavorite.Value, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new SupplierFavoriteResponse(supplier.Id, supplier.IsFavorite));
    }

    private static async Task<IResult> SetNoteAsync(Guid id, SupplierNoteRequest? request,
        HttpContext context, ApplicationDbContext db, CancellationToken cancellationToken)
    {
        if (request is null)
            return ValidationError(context, "Тело запроса обязательно");
        if (request.Note.ValueKind == JsonValueKind.Undefined)
            return ValidationError(context, "Поле note обязательно");
        if (request.Note.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
            return ValidationError(context, "note должен быть строкой или null");
        var note = request.Note.ValueKind == JsonValueKind.Null ? null : request.Note.GetString();
        if (note?.Length > 2000)
            return ValidationError(context, "note не должен быть длиннее 2000 символов");

        var supplier = await db.Suppliers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (supplier is null)
            return ProblemResponses.Create(context, StatusCodes.Status404NotFound, "Поставщик не найден", "NOT_FOUND");

        supplier.SetNote(note, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new SupplierNoteResponse(supplier.Id, supplier.Note));
    }

    private static bool TryBuildFilters(IQueryCollection query, out SupplierCatalogFilters? filters, out string? error)
    {
        filters = null;
        error = null;

        string? Value(string key) => query.TryGetValue(key, out var values) ? values.ToString() : null;
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        static bool Fail(string detail, out string? validationError)
        {
            validationError = detail;
            return false;
        }

        var textFields = new[]
        {
            (Name: "q", Value: Value("q"), Maximum: 500),
            (Name: "city", Value: Value("city"), Maximum: 160),
            (Name: "region", Value: Value("region"), Maximum: 160),
            (Name: "category", Value: Value("category"), Maximum: 160),
            (Name: "product", Value: Value("product"), Maximum: 500)
        };
        foreach (var field in textFields)
        {
            if (field.Value?.Length > field.Maximum)
                return Fail($"{field.Name} не должен быть длиннее {field.Maximum} символов", out error);
        }

        if (!TryReadBoolean("includeApproximatePrices", false, out var includeApproximatePrices) ||
            !TryReadBoolean("favoriteOnly", false, out var favoriteOnly))
            return Fail("Параметры includeApproximatePrices и favoriteOnly должны быть true или false", out error);

        if (!TryReadInteger("page", 1, out var page) || page < 1)
            return Fail("page должен быть целым числом не меньше 1", out error);
        if (!TryReadInteger("pageSize", 20, out var pageSize) || pageSize is < 10 or > 50)
            return Fail("pageSize должен быть целым числом от 10 до 50", out error);

        var sort = Clean(Value("sort")) ?? "created_desc";
        if (sort is not ("created_desc" or "created_asc" or "name_asc" or "name_desc"))
            return Fail("sort должен быть одним из created_desc, created_asc, name_asc, name_desc", out error);

        if (!TryReadDecimal("priceMin", out var priceMinimum) || !TryReadDecimal("priceMax", out var priceMaximum))
            return Fail("priceMin и priceMax должны быть неотрицательными десятичными числами", out error);
        var currency = Clean(Value("currency"));
        var unit = Clean(Value("unit"));
        if (priceMinimum.HasValue || priceMaximum.HasValue)
        {
            if (currency is null || currency.Length != 3 || !currency.All(char.IsAsciiLetter))
                return Fail("Для фильтра цены требуется трёхбуквенный код currency", out error);
            if (unit is null || unit.Length > 40)
                return Fail("Для фильтра цены требуется unit длиной до 40 символов", out error);
            if (priceMinimum.HasValue && priceMaximum.HasValue && priceMinimum > priceMaximum)
                return Fail("priceMax должен быть не меньше priceMin", out error);
            currency = SupplierFactNormalizer.NormalizeCurrency(currency).ToUpperInvariant();
            unit = SupplierFactNormalizer.NormalizeUnit(unit);
        }

        int? maximumDeliveryDays = null;
        var deliveryDaysText = Clean(Value("maxDeliveryDays"));
        if (deliveryDaysText is not null)
        {
            if (!int.TryParse(deliveryDaysText, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days < 0)
                return Fail("maxDeliveryDays должен быть неотрицательным целым числом", out error);
            maximumDeliveryDays = days;
        }

        if (!TryReadDecimal("minMinimumOrder", out var orderMinimum) ||
            !TryReadDecimal("maxMinimumOrder", out var orderMaximum))
            return Fail("minMinimumOrder и maxMinimumOrder должны быть неотрицательными десятичными числами", out error);
        var orderUnit = Clean(Value("minimumOrderUnit"));
        if (orderMinimum.HasValue || orderMaximum.HasValue)
        {
            if (orderUnit is null || orderUnit.Length > 40)
                return Fail("Для фильтра минимального заказа требуется minimumOrderUnit длиной до 40 символов", out error);
            if (orderMinimum.HasValue && orderMaximum.HasValue && orderMinimum > orderMaximum)
                return Fail("maxMinimumOrder должен быть не меньше minMinimumOrder", out error);
            orderUnit = SupplierFactNormalizer.NormalizeUnit(orderUnit);
        }

        filters = new SupplierCatalogFilters(
            Clean(Value("q")), Clean(Value("city")), Clean(Value("region")), Clean(Value("category")),
            Clean(Value("product")), priceMinimum, priceMaximum, currency, unit, includeApproximatePrices,
            maximumDeliveryDays, orderMinimum, orderMaximum, orderUnit, favoriteOnly, sort, page, pageSize);
        return true;

        bool TryReadBoolean(string name, bool defaultValue, out bool result)
        {
            result = defaultValue;
            var value = Clean(Value(name));
            if (value is null) return true;
            return bool.TryParse(value, out result);
        }

        bool TryReadInteger(string name, int defaultValue, out int result)
        {
            result = defaultValue;
            var value = Clean(Value(name));
            if (value is null) return true;
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
        }

        bool TryReadDecimal(string name, out decimal? result)
        {
            result = null;
            var value = Clean(Value(name));
            if (value is null) return true;
            if (!decimal.TryParse(value, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite |
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
                return false;
            result = parsed;
            return true;
        }
    }

    private static IResult ValidationError(HttpContext context, string detail) =>
        ProblemResponses.Create(context, StatusCodes.Status400BadRequest,
            "Некорректные параметры каталога поставщиков", "VALIDATION_ERROR", detail);
}

public sealed record SupplierFavoriteResponse(Guid Id, bool IsFavorite);
public sealed record SupplierNoteResponse(Guid Id, string? Note);
