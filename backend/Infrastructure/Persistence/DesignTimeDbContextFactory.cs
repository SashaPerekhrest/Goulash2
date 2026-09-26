using Goulash.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Goulash.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Environment.GetEnvironmentVariable("Database__Host") ?? "localhost",
            Port = int.TryParse(Environment.GetEnvironmentVariable("Database__Port"), out var port) ? port : 5432,
            Database = Environment.GetEnvironmentVariable("Database__Name") ?? "goulash",
            Username = Environment.GetEnvironmentVariable("Database__User") ?? "goulash",
            Password = Environment.GetEnvironmentVariable("Database__Password") ?? ""
        };

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(builder.ConnectionString, npgsql => npgsql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.GetName().Name))
            .Options;

        return new ApplicationDbContext(options);
    }
}
