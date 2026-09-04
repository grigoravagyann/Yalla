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

// The real actor, read from the token's claims: this is what puts a genuine staffMemberId on
// every audit row. Registered before the development stub so that, when the stub is enabled, its
// later registration wins.
builder.Services.AddScoped<Yalla.Application.Abstractions.ICurrentActor, Yalla.Api.Identity.ClaimsCurrentActor>();

// The pre-authentication stub, kept so the existing integration tests and local work against a
// seeded branch keep running. A no-op unless DevActor:Enabled is set, and never registered
// outside Development.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddDevelopmentActor(configuration);
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

app.UseHttpsRedirection();
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

// Migrates and seeds the demo branch so the dev actor stub has a real staff member to be.
// Returns false, and does nothing at all, when DevActor:Enabled is off.
if (app.Environment.IsDevelopment() && await app.Services.InitialiseDevelopmentDataAsync())
{
    app.Logger.LogInformation("Development data initialised.");
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
