using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Yalla.Application.Abstractions;
using Yalla.Application.Reservations;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;
using Yalla.Infrastructure.Time;

namespace Yalla.Infrastructure;

/// <summary>Registers everything the infrastructure layer provides.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Wires up the database, the clock and the domain services.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString,
        IConfiguration? configuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddSingleton<IClock, SystemClock>();

        services.AddDbContext<YallaDbContext>(options =>
            options.UseSqlServer(
                connectionString,
                sql => sql.MigrationsAssembly(typeof(YallaDbContext).Assembly.GetName().Name)));

        services.AddScoped<ITableStateService, TableStateService>();
        services.AddScoped<IFloorQuery, FloorQuery>();
        services.AddScoped<IReservationService, ReservationService>();
        services.AddScoped<IAvailabilityQuery, AvailabilityQuery>();

        // Both are plain settings objects rather than IOptions: they are read on nearly every
        // booking, they never change per request, and binding them once here keeps the
        // application layer free of a configuration dependency.
        services.AddSingleton(Bind<NoShowPolicy>(configuration, NoShowPolicy.SectionName));
        services.AddSingleton(Bind<BookingLockOptions>(configuration, BookingLockOptions.SectionName));

        return services;
    }

    /// <summary>
    /// Binds one settings section, or ships the defaults when there is no configuration - which is
    /// what tests and the design-time factory get.
    /// </summary>
    private static T Bind<T>(IConfiguration? configuration, string sectionName)
        where T : class, new()
    {
        var settings = new T();
        configuration?.GetSection(sectionName).Bind(settings);

        return settings;
    }

    /// <summary>
    /// Registers the development actor stub and the seeder that gives it a real staff member.
    /// </summary>
    /// <remarks>
    /// Call this from Development only. It is a no-op unless <c>DevActor:Enabled</c> is true, so
    /// switching it on is always deliberate. When authentication arrives, the real
    /// <see cref="ICurrentActor"/> registration replaces this call and nothing else changes.
    /// </remarks>
    public static IServiceCollection AddDevelopmentActor(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(DevActorOptions.SectionName);

        services.Configure<DevActorOptions>(section);

        if (!section.GetValue<bool>("Enabled"))
        {
            return services;
        }

        services.AddSingleton<DevSeedRegistry>();
        services.AddScoped<DevDataSeeder>();
        services.AddScoped<ICurrentActor, DevCurrentActor>();

        return services;
    }

    /// <summary>
    /// Applies pending migrations and seeds development data. Returns false when the dev actor is
    /// switched off, in which case nothing was touched.
    /// </summary>
    public static async Task<bool> InitialiseDevelopmentDataAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();

        var seeder = scope.ServiceProvider.GetService<DevDataSeeder>();
        if (seeder is null)
        {
            return false;
        }

        var db = scope.ServiceProvider.GetRequiredService<YallaDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
        await seeder.SeedAsync(cancellationToken);

        return true;
    }
}
