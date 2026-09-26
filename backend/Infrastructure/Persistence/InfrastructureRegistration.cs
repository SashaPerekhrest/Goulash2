using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Goulash.Infrastructure.Persistence;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var database = configuration.GetSection("Database");
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = database["Host"] ?? "localhost",
            Port = int.TryParse(database["Port"], out var port) ? port : 5432,
            Database = database["Name"] ?? "goulash",
            Username = database["User"] ?? "goulash",
            Password = database["Password"] ?? string.Empty,
            Timeout = 15,
            CommandTimeout = 30,
            ApplicationName = "Goulash.Api"
        }.ConnectionString;

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString, provider =>
                provider.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.GetName().Name)));
        services.AddScoped<SupplierIdentityLock>();
        services.AddScoped<SupplierDataTransaction>();
        return services;
    }
}
