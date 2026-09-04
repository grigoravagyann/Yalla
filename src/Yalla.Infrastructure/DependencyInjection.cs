using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yalla.Application.Abstractions;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Time;

namespace Yalla.Infrastructure;

/// <summary>Registers everything the infrastructure layer provides.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Wires up the database and the clock. Takes the connection string rather than
    /// <c>IConfiguration</c> so this layer stays unaware of where configuration comes from.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddSingleton<IClock, SystemClock>();

        services.AddDbContext<YallaDbContext>(options =>
            options.UseSqlServer(
                connectionString,
                sql => sql.MigrationsAssembly(typeof(YallaDbContext).Assembly.GetName().Name)));

        return services;
    }
}
