using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Application.BranchSettings;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Infrastructure.Messaging;
using Yalla.Infrastructure.Notifications;
using Yalla.Application.Media;
using Yalla.Application.Menus;
using Yalla.Application.Platform;
using Yalla.Application.Public;
using Yalla.Application.Reports;
using Yalla.Application.Ordering;
using Yalla.Application.Reservations;
using Yalla.Application.Staff;
using Yalla.Application.Tabs;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Media;
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

        // The lifecycle checks a stateless token cannot make about itself, cached for seconds.
        services.AddMemoryCache();
        services.AddScoped<ITokenAuthorityCheck, TokenAuthorityCheck>();
        services.AddScoped<ITabQuery, TabQuery>();
        services.AddScoped<ITabService, TabService>();

        // The platform tier and venue configuration: everything needed to onboard a venue through
        // the API instead of by hand in SQL.
        services.AddScoped<IPlatformService, PlatformService>();
        services.AddScoped<IBranchSettingsService, BranchSettingsService>();
        services.AddScoped<IMenuService, MenuService>();

        // The onboarding checklist, answered by the server. Registered before the platform service
        // because that service consults it: a branch cannot go Paid while its menu has holes in it.
        services.AddScoped<IBranchReadinessQuery, BranchReadinessQuery>();

        // The anonymous surface a link opens, and the reports behind the admin panel. Both are
        // read-only projections over what is already stored; neither writes anything.
        services.AddScoped<IPublicVenueQuery, PublicVenueQuery>();
        services.AddScoped<IReportQuery, ReportQuery>();

        // Photos. The storage root is verified once, at construction, so a folder that cannot be
        // written to stops the process rather than surfacing as a broken menu three screens later.
        services.AddSingleton(Bind<PhotoStorageOptions>(configuration, PhotoStorageOptions.SectionName));
        services.AddSingleton<IPhotoStorage, LocalDiskPhotoStorage>();
        services.AddScoped<IPhotoService, PhotoService>();

        // TimeProvider is what PeriodicTimer in the outbox loop reads, so a test advances a fake one
        // rather than waiting. IClock stays the domain-facing seam - see SystemClock.
        services.TryAddSingleton(TimeProvider.System);

        // The outbox: written with its cause, dispatched by a loop, leased so two dispatchers can
        // share the work.
        services.AddSingleton(Bind<OutboxOptions>(configuration, OutboxOptions.SectionName));
        services.AddScoped<IOutbox, Outbox>();
        services.AddScoped<OutboxDispatcher>();
        services.AddHostedService<OutboxHostedService>();

        // Notifications. The channel is selected by configuration, the same way Prompt 3 selects its
        // verification-code sender - and the default writes to the log, so the whole scheduler runs
        // and is testable with no phone attached to the machine.
        var notifications = Bind<NotificationOptions>(configuration, NotificationOptions.SectionName);

        services.AddSingleton(notifications);
        services.AddScoped<IDinerDeviceService, DinerDeviceService>();

        if (string.Equals(notifications.Channel, "Expo", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<INotificationChannel, ExpoNotificationChannel>();
        }
        else
        {
            services.AddScoped<INotificationChannel, LoggingNotificationChannel>();
        }

        services.AddScoped<IOutboxHandler, ReservationReminderHandler>();
        services.AddScoped<IOutboxHandler, ReservationLateNudgeHandler>();
        services.AddScoped<IOutboxHandler, ReservationDecidedHandler>();
        services.AddScoped<IOutboxHandler, ParticipantApprovedHandler>();
        services.AddScoped<IOutboxHandler, OrderReadyHandler>();

        // Ordering and the live bill. TabLedger is the shared piece: every mutation to a tab goes
        // through it, so the totals cache and the event stream are written in the same SaveChanges
        // as the change itself and neither can drift from the lines.
        services.AddScoped<TabLedger>();
        services.AddScoped<IMenuQuery, MenuQuery>();
        services.AddScoped<ITabOrderService, TabOrderService>();
        services.AddScoped<ITabBillingQuery, TabBillingQuery>();
        services.AddScoped<IServiceRequestService, ServiceRequestService>();
        services.AddScoped<ITabPaymentService, TabPaymentService>();
        services.AddScoped<IStaffManagementService, StaffManagementService>();

        // The share-link template. Optional in configuration; the default points at the local
        // diner app, which is what a developer with no settings gets.
        services.AddOptions<TabOptions>();
        if (configuration is not null)
        {
            services.Configure<TabOptions>(configuration.GetSection(TabOptions.SectionName));
        }

        // Both are plain settings objects rather than IOptions: they are read on nearly every
        // booking, they never change per request, and binding them once here keeps the
        // application layer free of a configuration dependency.
        services.AddSingleton(Bind<NoShowPolicy>(configuration, NoShowPolicy.SectionName));
        services.AddSingleton(Bind<BookingLockOptions>(configuration, BookingLockOptions.SectionName));

        // The table lock and the one writer allowed to insert a booking. Scoped, because both hold
        // the request's DbContext and the lock lives inside that context's connection.
        services.AddScoped<TableLock>();
        services.AddScoped<ReservationWriter>();

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
        services.AddScoped<ITokenRefreshService, TokenRefreshService>();

        // Identity type 1 has no sign-in flow of its own: the tab-scoped token is minted by the tab
        // service when somebody scans a table or redeems an invitation. This is the minting.
        services.AddScoped<TabParticipantTokens>();

        // The only implementations that ship. Both write a live credential to the log, which is
        // what makes them useful locally and what makes replacing them a release blocker. They
        // are registered with TryAdd so a real provider registered first simply wins.
        services.TryAddScoped<IVerificationCodeSender, DevelopmentVerificationCodeSender>();
        services.TryAddScoped<IPasswordResetSender, DevelopmentPasswordResetSender>();

        return services;
    }

    /// <summary>
    /// Registers the first platform admin's configuration and the seeder that creates them.
    /// </summary>
    /// <remarks>
    /// With <paramref name="requireConfiguration"/> - Development - a missing or blank
    /// <c>PlatformAdmin:Email</c> / <c>PlatformAdmin:Password</c> fails startup with instructions,
    /// rather than silently creating a default account that ends up in production.
    /// </remarks>
    public static IServiceCollection AddPlatformAdminSeeding(
        this IServiceCollection services,
        IConfiguration configuration,
        bool requireConfiguration)
    {
        var section = configuration.GetSection(PlatformAdminOptions.SectionName);
        var options = section.Get<PlatformAdminOptions>() ?? new PlatformAdminOptions();

        if (requireConfiguration && !options.IsConfigured)
        {
            throw new InvalidOperationException(
                "PlatformAdmin:Email and PlatformAdmin:Password are not configured. There would be no way in "
                + "to a fresh database. They are secrets: set them with "
                + "`dotnet user-secrets set \"PlatformAdmin:Email\" \"you@yalla.app\" --project src/Yalla.Api` and "
                + "`dotnet user-secrets set \"PlatformAdmin:Password\" \"<long random value>\" --project src/Yalla.Api`, "
                + "or as the PlatformAdmin__Email / PlatformAdmin__Password environment variables elsewhere.");
        }

        services.Configure<PlatformAdminOptions>(section);
        services.AddScoped<PlatformAdminSeeder>();

        return services;
    }

    /// <summary>
    /// Creates the configured platform admin if they do not exist. Returns false when seeding is
    /// switched off or nothing is configured.
    /// </summary>
    public static async Task<bool> SeedPlatformAdminAsync(
        this IServiceProvider services,
        bool applyMigrations,
        CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();

        var options = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<PlatformAdminOptions>>().Value;

        if (!options.SeedOnStartup || !options.IsConfigured)
        {
            return false;
        }

        if (applyMigrations)
        {
            var db = scope.ServiceProvider.GetRequiredService<YallaDbContext>();
            await db.Database.MigrateAsync(cancellationToken);
        }

        var seeder = scope.ServiceProvider.GetRequiredService<PlatformAdminSeeder>();

        return await seeder.SeedAsync(cancellationToken) is not null;
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
    /// switching it on is always deliberate. The stub answers only for requests that carry no
    /// token; see <c>DevelopmentActorOrToken</c> in the API layer.
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

        // Registered as itself, not as ICurrentActor. The host composes it with the real
        // claims-based actor so that a request carrying a token is never overridden by the stub.
        services.AddScoped<DevCurrentActor>();

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
