using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yalla.Application.Abstractions;
using Yalla.Application.Auth;
using Yalla.Application.BranchSettings;
using Yalla.Application.Diners;
using Yalla.Application.Messaging;
using Yalla.Application.Notifications;
using Yalla.Application.Overview;
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
using Yalla.Application.Venues;
using Yalla.Infrastructure.Identity;
using Yalla.Infrastructure.Media;
using Yalla.Infrastructure.Persistence;
using Yalla.Infrastructure.Services;
using Yalla.Application.Telegram;
using Yalla.Infrastructure.Telegram;
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

        // Who did what, in the same unit of work as the change (docs/activity.md). Scoped: one request's actor and
        // context, read once.
        services.AddScoped<Yalla.Application.Activity.IActivityLog, ActivityLog>();
        services.AddScoped<IDeviceAdminActivity, DeviceAdminActivity>();

        services.AddScoped<ITableStateService, TableStateService>();
        services.AddScoped<IFloorQuery, FloorQuery>();
        services.AddScoped<IReservationService, ReservationService>();
        services.AddScoped<NoShowBlocks>();
        services.AddScoped<Yalla.Application.Reservations.IPreOrderService, PreOrderService>();
        services.AddScoped<Yalla.Application.Takeaway.ITakeawayService, TakeawayService>();
        services.AddScoped<Yalla.Application.Messages.IGuestMessageService, GuestMessageService>();
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

        // The platform console's Home, a venue's booking counts and the activity feed - read models
        // over what the rest already stores, aggregates only.
        services.AddScoped<IPlatformOverviewQuery, PlatformOverviewQuery>();

        // The venue read for the people who work there. Separate from the platform service on
        // purpose: that one is PlatformAdminOnly and carries fields the platform keeps to itself.
        services.AddScoped<IManagedVenueQuery, ManagedVenueQuery>();
        services.AddScoped<IBranchSettingsService, BranchSettingsService>();
        services.AddScoped<IMenuService, MenuService>();

        // The onboarding checklist, answered by the server. Registered before the platform service
        // because that service consults it: a branch cannot go Paid while its menu has holes in it.
        services.AddScoped<IBranchReadinessQuery, BranchReadinessQuery>();

        // The venue console's Overview: one branch, today. Read-only, behind the branch guard.
        services.AddScoped<IBranchOverviewQuery, BranchOverviewQuery>();
        services.AddScoped<Yalla.Application.Staff.IStaffTodayQuery, StaffTodayQuery>();

        // The anonymous surface a link opens, and the reports behind the admin panel. Both are
        // read-only projections over what is already stored; neither writes anything.
        services.AddScoped<IPublicVenueQuery, PublicVenueQuery>();
        services.AddScoped<IPublicListingQuery, PublicListingQuery>();
        services.AddScoped<IBranchListingService, BranchListingService>();
        services.AddScoped<IBranchReviewService, BranchReviewService>();
        services.AddScoped<Yalla.Application.Reviews.IReviewModerationService, ReviewModerationService>();
        services.AddScoped<IDinerOrderQuery, DinerOrderQuery>();

        // The diner's favourites and notifications feed (K11, K12). The feed is written by the services
        // that enqueue its pushes, through DinerNotices; these are the diner's reads and the sweep.
        services.AddScoped<IDinerFavoriteService, DinerFavoriteService>();
        services.AddScoped<IDinerNotificationFeed, DinerNotificationFeed>();
        services.AddScoped<DinerNotificationRetention>();

        // Guest-message threads a year quiet, deleted on the same loop's daily pass (docs/messages.md, history).
        services.AddScoped<ConversationRetention>();

        // The activity log: kept a year, money three, swept on the same daily pass (docs/activity.md). And its reads.
        services.AddScoped<ActivityRetention>();
        services.AddScoped<Yalla.Application.Activity.IActivityQuery, ActivityHistoryQuery>();
        services.AddScoped<Yalla.Application.Activity.IStaffPerformanceQuery, StaffPerformanceReader>();
        services.AddScoped<Yalla.Application.Activity.ITableTransferService, TableTransferService>();
        services.AddScoped<IPublicBookingService, PublicBookingService>();
        services.AddScoped<IReportQuery, ReportQuery>();

        // Photos. The storage root is verified once, at construction, so a folder that cannot be
        // written to stops the process rather than surfacing as a broken menu three screens later.
        //
        // The root is resolved here, once: "~" is this user's Yalla data folder, so Development's
        // "~/photos" is one folder every worktree shares, as they share the database. The sweep
        // service logs the absolute path at startup.
        var photoStorage = Bind<PhotoStorageOptions>(configuration, PhotoStorageOptions.SectionName);
        photoStorage.RootPath = PhotoStorageRoot.Resolve(photoStorage.RootPath);

        services.AddSingleton(photoStorage);
        services.AddSingleton<IPhotoStorage, LocalDiskPhotoStorage>();
        services.AddScoped<IPhotoService, PhotoService>();

        // The orphan sweep, every PhotoStorage:SweepIntervalMinutes (K10). Zero switches it off; a
        // negative value is a typo, and a typo here should not quietly mean "never".
        var photoSweep = Bind<PhotoSweepOptions>(configuration, PhotoSweepOptions.SectionName);

        if (photoSweep.SweepIntervalMinutes < 0)
        {
            throw new InvalidOperationException(
                $"PhotoStorage:SweepIntervalMinutes is {photoSweep.SweepIntervalMinutes}. Use the minutes "
                + "between orphan photo sweeps, or 0 to switch the sweep off.");
        }

        services.AddSingleton(photoSweep);
        services.AddHostedService<PhotoSweepHostedService>();

        // A diner's own account - profile, password, picture. Beside the photo service because
        // the picture goes through the same storage, and apart from the sign-in flows because
        // everything here already holds a token.
        services.AddScoped<IDinerProfileService, DinerProfileService>();

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

        // Short-notice bookings nobody at the venue answered in time, declined with an apology. A loop
        // of its own rather than an outbox message, because the outbox drops what is an hour overdue
        // and this deadline is a promise to the diner however late it is noticed. Zero switches it off
        // (a test host sharing a database); a negative value is a typo and stops startup.
        var approvalExpiry = Bind<ApprovalExpiryOptions>(configuration, ApprovalExpiryOptions.SectionName);

        if (approvalExpiry.SweepIntervalSeconds < 0)
        {
            throw new InvalidOperationException(
                $"ApprovalExpiry:SweepIntervalSeconds is {approvalExpiry.SweepIntervalSeconds}. Use the seconds "
                + "between approval sweeps, or 0 to switch the sweep off.");
        }

        services.AddSingleton(approvalExpiry);
        services.AddScoped<ApprovalExpirySweep>();
        services.AddHostedService<ApprovalExpiryHostedService>();

        // Takeaway orders nobody answered within five minutes, on the same interval and the same switch.
        services.AddScoped<TakeawayExpirySweep>();
        services.AddHostedService<TakeawayExpiryHostedService>();

        services.AddScoped<IOutboxHandler, ReservationReminderHandler>();
        services.AddScoped<IOutboxHandler, ReservationLateNudgeHandler>();
        services.AddScoped<IOutboxHandler, ReservationOnYourWayHandler>();
        services.AddScoped<IOutboxHandler, ReservationDecidedHandler>();
        services.AddScoped<IOutboxHandler, ReservationChangedByLinkHandler>();
        services.AddScoped<IOutboxHandler, ReservationCancelledByLinkHandler>();
        services.AddScoped<IOutboxHandler, DinerBookingsBlockedHandler>();
        services.AddScoped<IOutboxHandler, DinerBookingsUnblockedHandler>();
        services.AddScoped<IOutboxHandler, ReservationNoShowHandler>();
        services.AddScoped<Yalla.Application.Reservations.IBookingBlockService, BookingBlockService>();
        services.AddScoped<IOutboxHandler, TableRequestHandler>();
        services.AddScoped<IOutboxHandler, TableRequestDecidedHandler>();
        services.AddScoped<IOutboxHandler, ParticipantApprovedHandler>();
        services.AddScoped<IOutboxHandler, JoinRequestedHandler>();
        services.AddScoped<IOutboxHandler, OrderReadyHandler>();
        services.AddScoped<IOutboxHandler, TakeawayAcceptedHandler>();
        services.AddScoped<IOutboxHandler, TakeawayReadyHandler>();
        services.AddScoped<IOutboxHandler, TakeawayDeclinedHandler>();
        services.AddScoped<IOutboxHandler, VenueMessageHandler>();

        // Staff push: Web Push to the staff app's phones and tablets. The VAPID identity is configured, generated for
        // the run in Development, or absent - and then every push is logged instead of sent. See WebPushOptions.
        services.AddSingleton(Bind<WebPushOptions>(configuration, WebPushOptions.SectionName));
        services.AddSingleton<VapidKeys>();
        // No redirects: the endpoint was held to the push services' hosts when it was saved, and a redirect would take
        // the POST - and its VAPID signature - somewhere that check never saw.
        services.AddHttpClient(StaffPushSender.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddScoped<StaffPushSender>();
        services.AddScoped<IStaffPushService, StaffPushService>();
        services.AddScoped<IOutboxHandler, StaffPushHandler>();

        // Ordering and the live bill. TabLedger is the shared piece: every mutation to a tab goes
        // through it, so the totals cache and the event stream are written in the same SaveChanges
        // as the change itself and neither can drift from the lines.
        services.AddScoped<TabLedger>();

        // One copy of the branch boundary for every staff write that needs it. It was three
        // identical private helpers, and two services were written without one - see the class.
        services.AddScoped<StaffBranchGuard>();
        services.AddScoped<IStaffBranchGuard>(provider => provider.GetRequiredService<StaffBranchGuard>());
        services.AddScoped<IMenuQuery, MenuQuery>();
        services.AddScoped<ITabOrderService, TabOrderService>();
        services.AddScoped<ITabBillingQuery, TabBillingQuery>();
        services.AddScoped<IServiceRequestService, ServiceRequestService>();
        services.AddScoped<ITabPaymentService, TabPaymentService>();
        services.AddScoped<IStaffManagementService, StaffManagementService>();
        services.AddScoped<IStaffShiftService, StaffShiftService>();

        // The share-link template. Optional in configuration; the default points at the local
        // diner app, which is what a developer with no settings gets.
        services.AddOptions<TabOptions>();
        services.AddOptions<PublicWebOptions>();
        if (configuration is not null)
        {
            services.Configure<TabOptions>(configuration.GetSection(TabOptions.SectionName));
            services.Configure<PublicWebOptions>(configuration.GetSection(PublicWebOptions.SectionName));
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

        // Singleton for the same reason the phone limiter is: the budget is per identifier across
        // every request, and a scoped one would be a fresh budget per attempt.
        services.AddSingleton<PasswordAttemptLimiter>();
        services.AddScoped<TokenIssuer>();
        services.AddScoped<RefreshTokenStore>();

        // The account path both phone doors share - the code and the Telegram bot.
        services.AddScoped<DinerPhoneSignIn>();

        services.AddScoped<IDinerAuthService, DinerAuthService>();
        services.AddScoped<IStaffAuthService, StaffAuthService>();
        services.AddScoped<IVenueUserAuthService, VenueUserAuthService>();

        // A console user's own name and password - the platform admin's, and a venue owner's, general
        // manager's or branch manager's, under one set of rules. Neither address is theirs to change.
        services.AddScoped<OwnAccountChanges>();
        services.AddScoped<IPlatformAdminAccountService, PlatformAdminAccountService>();
        services.AddScoped<IVenueUserAccountService, VenueUserAccountService>();
        services.AddScoped<ITokenRefreshService, TokenRefreshService>();

        // Identity type 1 has no sign-in flow of its own: the tab-scoped token is minted by the tab
        // service when somebody scans a table or redeems an invitation. This is the minting.
        services.AddScoped<TabParticipantTokens>();

        // No real provider ships yet, so one of two stand-ins is registered - and WHICH one is
        // decided by the host, not by configuration, for the same reason
        // ReturnVerificationCodeInResponse is: an environment variable must not be able to switch a
        // credential leak back on in a deployed environment.
        //
        // In Development the senders write the code and the reset link to the log, because that is
        // the only way to exercise the flows with no SMS or email provider attached. Everywhere
        // else they are replaced by senders that log the failure at Error and never the credential:
        // a reset link in a staging log is a working admin password, and the whole point of storing
        // only the token's hash is that possession of the data is not possession of a way in.
        //
        // TryAdd either way, so a real provider registered first simply wins - which is also what
        // these two checks are asking, before the TryAdds make the answer unknowable.
        var realCodeSender = services.Any(d => d.ServiceType == typeof(IVerificationCodeSender));
        var realResetSender = services.Any(d => d.ServiceType == typeof(IPasswordResetSender));
        var standInUsed = !realCodeSender || !realResetSender;

        if (allowDevelopmentSecretsInResponses)
        {
            services.TryAddScoped<IVerificationCodeSender, DevelopmentVerificationCodeSender>();
            services.TryAddScoped<IPasswordResetSender, DevelopmentPasswordResetSender>();
        }
        else
        {
            services.TryAddScoped<IVerificationCodeSender, UnconfiguredVerificationCodeSender>();
            services.TryAddScoped<IPasswordResetSender, UnconfiguredPasswordResetSender>();
        }

        // Published so Program.cs can warn at startup without naming these internal types.
        services.AddSingleton(new CredentialDeliveryReport(
            WritesCredentialsToLog: standInUsed && allowDevelopmentSecretsInResponses,
            DeliversNothing: standInUsed && !allowDevelopmentSecretsInResponses,
            CodesDeliverNothing: !realCodeSender && !allowDevelopmentSecretsInResponses));

        return services;
    }

    /// <summary>
    /// Registers the Telegram verification bot: the options, the Bot API client, the update handler,
    /// the start/claim service and the hosted service that registers the webhook or long-polls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Always registers <see cref="TelegramOptions"/>, so the host can ask whether the door is on and
    /// map its routes only then. Everything else is registered only when <c>Telegram:Enabled</c> is
    /// true - and then the token, the bot username and (in webhook mode) the webhook secret must be
    /// present, or startup fails here with instructions, exactly as a missing signing key does.
    /// Running half-configured would mean a start endpoint that hands out links to a bot that cannot
    /// answer.
    /// </para>
    /// <para>
    /// <paramref name="isDevelopment"/> comes from the host, not from configuration: long polling is
    /// a single-process, no-public-address mode, and two deployed instances polling the same bot
    /// would each get half the conversation. It is refused anywhere else, whatever the setting says.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddTelegramVerification(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment)
    {
        var options = Bind<TelegramOptions>(configuration, TelegramOptions.SectionName);

        services.AddSingleton(options);

        if (!options.Enabled)
        {
            return services;
        }

        if (string.IsNullOrWhiteSpace(options.BotToken))
        {
            throw new InvalidOperationException(
                "Telegram:Enabled is true but Telegram:BotToken is missing. It is a secret and must never be "
                + "committed: set it with `dotnet user-secrets set \"Telegram:BotToken\" \"<token from @BotFather>\" "
                + "--project src/Yalla.Api` for local work, or as the Telegram__BotToken environment variable "
                + "elsewhere. Or set Telegram:Enabled to false to leave the Telegram door unmapped.");
        }

        if (string.IsNullOrWhiteSpace(options.BotUsername) || options.BotUsername.StartsWith('@'))
        {
            throw new InvalidOperationException(
                "Telegram:Enabled is true but Telegram:BotUsername is missing or starts with '@'. It is the "
                + "bot's username without the '@', as @BotFather shows it, and goes into the deep link "
                + "https://t.me/{username}?start=... (Telegram__BotUsername).");
        }

        if (options.IsLongPolling)
        {
            if (!isDevelopment)
            {
                throw new InvalidOperationException(
                    "Telegram:Mode is LongPolling, which is only allowed in the Development environment: it is "
                    + "one process polling api.telegram.org with no public address, and two deployed instances "
                    + "would each receive half the conversation. Use Mode=Webhook with Telegram:WebhookSecret "
                    + "and Telegram:WebhookPublicUrl.");
            }
        }
        else if (!options.IsWebhook)
        {
            throw new InvalidOperationException(
                $"Telegram:Mode is '{options.Mode}'. Use '{TelegramOptions.WebhookMode}' or, in Development only, "
                + $"'{TelegramOptions.LongPollingMode}'.");
        }
        else if (string.IsNullOrWhiteSpace(options.WebhookSecret))
        {
            throw new InvalidOperationException(
                "Telegram:Enabled is true in Webhook mode but Telegram:WebhookSecret is missing. It is the value "
                + "Telegram sends as X-Telegram-Bot-Api-Secret-Token and the webhook refuses calls without it. "
                + "It is a secret: set it with `dotnet user-secrets set \"Telegram:WebhookSecret\" \"<long random "
                + "value>\" --project src/Yalla.Api` for local work, or as the Telegram__WebhookSecret environment "
                + "variable elsewhere. `openssl rand -base64 48` produces a suitable value.");
        }

        if (options.ChallengeLifetime <= TimeSpan.Zero || options.PollAfterMs <= 0)
        {
            throw new InvalidOperationException(
                "Telegram:ChallengeLifetime must be a positive duration and Telegram:PollAfterMs a positive number of milliseconds.");
        }

        // A typed client, like the Expo channel: one HttpClient per call, from the factory's pool.
        services.AddHttpClient<ITelegramBotClient, TelegramBotClient>();
        services.AddScoped<ITelegramUpdateHandler, TelegramUpdateHandler>();
        services.AddScoped<ITelegramVerificationService, TelegramVerificationService>();
        services.AddHostedService<TelegramHostedService>();

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
    /// Registers the development actor stub.
    /// </summary>
    /// <remarks>
    /// Call this from Development only. It is a no-op unless <c>DevActor:Enabled</c> is true, so
    /// switching it on is always deliberate. The stub answers only for requests that carry no
    /// token; see <c>DevelopmentActorOrToken</c> in the API layer. With no pinned id it reports the
    /// staff member the development seed created, so it is only useful alongside
    /// <see cref="AddDevelopmentSeeding"/> - which Development switches on by default.
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

        // Shared with the seeder, which writes the ids the stub reads. TryAdd in both places, so
        // it is one singleton whichever of the two is switched on, or both.
        services.TryAddSingleton<DevSeedRegistry>();

        // Registered as itself, not as ICurrentActor. The host composes it with the real
        // claims-based actor so that a request carrying a token is never overridden by the stub.
        services.AddScoped<DevCurrentActor>();

        return services;
    }

    /// <summary>
    /// Registers the seeder that writes the Development demo data: the demo venue and branch, its
    /// floor, opening hours and two staff, and the listing, reviews and table pins the diner app
    /// shows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this from Development only; the host does, and it calls
    /// <see cref="InitialiseDevelopmentDataAsync"/> only there too. It is a no-op unless
    /// <c>DevSeed:Enabled</c> is true.
    /// </para>
    /// <para>
    /// Deliberately independent of <c>DevActor:Enabled</c>. Real diner and staff sign-in is how the
    /// apps are tested, and that is done with the stub off - which used to take the demo data with
    /// it, leaving an empty database in exactly the mode people test in.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddDevelopmentSeeding(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("DevSeed:Enabled"))
        {
            return services;
        }

        // The seeder publishes what it created here. The stub reads it when that is on; nothing
        // else does, but the seeder cannot be constructed without it.
        services.TryAddSingleton<DevSeedRegistry>();
        services.AddScoped<DevListingSeeder>();
        services.AddScoped<DevDataSeeder>();

        return services;
    }

    /// <summary>
    /// Applies pending migrations and seeds development data. Returns false when development
    /// seeding is not registered - <c>DevSeed:Enabled</c> off, or not Development - in which case
    /// nothing was touched.
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
