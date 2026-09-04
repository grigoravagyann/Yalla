using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
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
        services.AddScoped<IAuthorizationQueries, AuthorizationQueries>();
        services.AddScoped<ITabQuery, TabQuery>();

        // Both are plain settings objects rather than IOptions: they are read on nearly every
        // booking, they never change per request, and binding them once here keeps the
        // application layer free of a configuration dependency.
        services.AddSingleton(Bind<NoShowPolicy>(configuration, NoShowPolicy.SectionName));
        services.AddSingleton(Bind<BookingLockOptions>(configuration, BookingLockOptions.SectionName));

        return services;
    }

    /// <summary>
    /// Registers the four sign-in flows, token minting and the development senders.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The signing key is validated here rather than on the first request that needs it. A
    /// process that starts without a key would run happily until somebody tried to sign in, and
    /// then fail in a way that looks like a client bug.
    /// </para>
    /// <para>
    /// <paramref name="allowDevelopmentSecretsInResponses"/> is passed in by the host, not read
    /// from configuration here, so that <c>Auth:ReturnVerificationCodeInResponse</c> cannot be
    /// switched on outside Development by anyone with an environment variable.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddAuthenticationServices(
        this IServiceCollection services,
        IConfiguration configuration,
        bool allowDevelopmentSecretsInResponses)
    {
        var jwtSection = configuration.GetSection(JwtOptions.SectionName);
        var signingKey = jwtSection.GetValue<string>("SigningKey");

        if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey is missing or shorter than 32 bytes. It is a secret and must never "
                + "be committed: set it with `dotnet user-secrets set \"Jwt:SigningKey\" \"<value>\"` "
                + "for local work, or as the Jwt__SigningKey environment variable elsewhere. "
                + "`openssl rand -base64 48` produces a suitable value.");
        }

        services.Configure<JwtOptions>(jwtSection);

        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));
        services.PostConfigure<AuthOptions>(options =>
            options.ReturnVerificationCodeInResponse =
                options.ReturnVerificationCodeInResponse && allowDevelopmentSecretsInResponses);

        services.AddSingleton<SecretHasher>();
        services.AddSingleton<PhoneCodeRateLimiter>();
        services.AddScoped<TokenIssuer>();
        services.AddScoped<RefreshTokenStore>();

        services.AddScoped<IDinerAuthService, DinerAuthService>();
        services.AddScoped<IStaffAuthService, StaffAuthService>();
        services.AddScoped<IVenueUserAuthService, VenueUserAuthService>();
        services.AddScoped<ITabParticipantAuthService, TabParticipantAuthService>();
        services.AddScoped<ITokenRefreshService, TokenRefreshService>();

        // The only implementations that ship. Both write a live credential to the log, which is
        // what makes them useful locally and what makes replacing them a release blocker. They
        // are registered with TryAdd so a real provider registered first simply wins.
        services.TryAddScoped<IVerificationCodeSender, DevelopmentVerificationCodeSender>();
        services.TryAddScoped<IPasswordResetSender, DevelopmentPasswordResetSender>();

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
