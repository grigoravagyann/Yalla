using Microsoft.EntityFrameworkCore;
using Serilog;
using Yalla.Api.ApplicationExtensions;
using Yalla.Api.Endpoints;
using Yalla.Api.Middleware;
using Yalla.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// CreateBuilder has already loaded, in this order: appsettings.json, the secret-free
// appsettings.{Environment}.json, user secrets in Development, environment variables and the
// command line - which is how real values (connection strings, keys) arrive in a deployed
// environment. Re-adding those sources here would register each file twice and, worse, calling
// Build() would hand the rest of the app a second configuration root while Serilog kept reading
// builder.Configuration - two sources of truth that can disagree. Use the one the host owns.
var configuration = builder.Configuration;

// Logging is configured entirely from the Serilog section, so sinks and levels change per
// environment without a redeploy.
builder.Host.UseSerilog((context, loggerConfiguration) =>
    loggerConfiguration.ReadFrom.Configuration(context.Configuration));

builder.Logging.ClearProviders();
builder.Logging.AddSerilog();

builder.AddServices(configuration);
builder.AddYallaCors();

// Development only, and only when nothing more specific was asked for: bind every interface so a
// phone on the same wifi can reach the API. A launch profile's applicationUrl, a container's
// ASPNETCORE_HTTP_PORTS or an explicit Kestrel section all win over this.
builder.ListenOnAllInterfacesInDevelopment();

// Reads the JWT signing key from user secrets in Development and from configuration elsewhere,
// and fails startup if it is missing - not on the first request that needs to sign something.
//
// Returning a live credential in a response body is a Development-only affordance, so whether it
// is permitted at all is decided here from the environment rather than from configuration, where
// an environment variable could switch it on in production.
builder.Services.AddAuthenticationServices(
    configuration,
    allowDevelopmentSecretsInResponses: builder.Environment.IsDevelopment());

builder.AddYallaAuthentication();

// The first platform admin, from user secrets. In Development a missing email or password fails
// startup here rather than silently creating a default account - except under the EF tooling,
// which builds the host only to read the model and must not need a secret to do it.
builder.Services.AddPlatformAdminSeeding(
    configuration,
    requireConfiguration: builder.Environment.IsDevelopment() && !EF.IsDesignTime);

// The real actor, read from the token's claims: this is what puts a genuine staffMemberId on
// every audit row.
builder.Services.AddScoped<Yalla.Api.Identity.ClaimsCurrentActor>();
builder.Services.AddScoped<Yalla.Application.Abstractions.ICurrentActor>(
    sp => sp.GetRequiredService<Yalla.Api.Identity.ClaimsCurrentActor>());

// The stub that lets you poke the API locally with no token and still land a real staff member in
// the audit log. Never registered outside Development, and a no-op unless DevActor:Enabled is set.
//
// It is composed with the real actor rather than replacing it: the last registration wins, and it
// resolves the stub only for a request that carries no token. Overriding a genuine sign-in was a
// trap - the policies would admit a platform admin and the service would then be told they were
// the seeded waiter.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddDevelopmentActor(configuration);

    builder.Services.AddScoped<Yalla.Application.Abstractions.ICurrentActor>(sp =>
        sp.GetService<Yalla.Infrastructure.Identity.DevCurrentActor>() is { } stub
            ? new Yalla.Api.Identity.DevelopmentActorOrToken(
                sp.GetRequiredService<IHttpContextAccessor>(),
                sp.GetRequiredService<Yalla.Api.Identity.ClaimsCurrentActor>(),
                stub)
            : sp.GetRequiredService<Yalla.Api.Identity.ClaimsCurrentActor>());
}

var app = builder.Build();

// Enriches log events with TraceId and RequestPath. Does NOT catch or log exceptions -
// UnifiedExceptionHandler is the single log point.
app.UseMiddleware<ErrorContextMiddleware>();

// The unified handler owns every environment. No developer exception page anywhere: it preempts
// the handler and leaks stack traces in non-local environments.
app.UseExceptionHandler();

// Off unless Swagger:Enabled says otherwise, and 404 - never 401, never a redirect - when it is
// off or the caller's address is not on the allowlist.
app.UseYallaSwagger();

if (!app.Environment.IsDevelopment() && !app.Environment.IsStaging())
{
    app.UseHsts();
}

// In Development the API also serves plain HTTP on the LAN for phones, which cannot trust the dev
// certificate; redirecting them to HTTPS would defeat the point. Everywhere else HTTPS is the rule.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseYallaCors();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// After authentication, so the limiter can partition by the signed-in caller rather than only by
// address. Production only by default; RateLimiting:Enabled overrides either way.
app.UseYallaRateLimiting();

// No controllers yet - every endpoint below is a minimal API. The MVC pipeline stays wired so
// controllers can be added without touching this file.
app.MapControllers();

app.MapYallaHealthChecks();
app.MapAuthEndpoints();
app.MapTabEndpoints();
app.MapTableStateEndpoints();
app.MapAdminDeviceEndpoints();
app.MapReservationEndpoints();
app.MapPlatformEndpoints();
app.MapVenueAdminEndpoints();

// Development only: on start, log the LAN address with both ports - the value that goes into the
// frontend config - so nobody hunts for it in ipconfig.
app.LogLanAddresses();

// Migrates and seeds the demo branch so the dev actor stub has a real staff member to be.
// Returns false, and does nothing at all, when DevActor:Enabled is off.
if (app.Environment.IsDevelopment() && await app.Services.InitialiseDevelopmentDataAsync())
{
    app.Logger.LogInformation("Development data initialised.");
}

// The way in to a fresh database. Off under the EF tooling and in the test host; migrates first in
// Development so a brand-new machine gets a schema and an admin in one start.
//
// A failure here is logged and does not stop the host. Outside Development this is the only thing
// that touches the database at boot, and a database that is briefly unreachable - or a build
// started before its migration ran - would otherwise crash-loop the whole API over a row that can
// be created on the next restart.
if (!EF.IsDesignTime)
{
    try
    {
        if (await app.Services.SeedPlatformAdminAsync(applyMigrations: app.Environment.IsDevelopment()))
        {
            app.Logger.LogInformation("Platform admin present.");
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(
            ex,
            "The platform admin could not be seeded. The API is starting anyway; if this is a fresh "
            + "database there is no way in until it succeeds. Check the connection and that migrations have run.");
    }
}

app.Run();

/// <summary>
/// Named so <c>WebApplicationFactory&lt;Program&gt;</c> can find the entry point.
/// </summary>
/// <remarks>
/// The tests that matter most in this task - a branch A token refused at branch B, a participant
/// token refused on another tab - are only meaningful end to end, through the real pipeline with
/// the real policies attached. That needs the host, and the host needs a public entry point.
/// </remarks>
public partial class Program;
