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

// Stands in for authentication until the next task. Registration is a no-op unless
// DevActor:Enabled is set, and nothing outside Development calls this.
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

// Development and Staging only.
app.UseYallaSwagger();

if (!app.Environment.IsDevelopment() && !app.Environment.IsStaging())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseYallaCors();

app.UseRouting();

// Production only by default; RateLimiting:Enabled overrides either way.
app.UseYallaRateLimiting();

// No controllers yet - every endpoint below is a minimal API. The MVC pipeline stays wired so
// controllers can be added without touching this file.
app.MapControllers();

app.MapYallaHealthChecks();
app.MapTableStateEndpoints();

// Migrates and seeds the demo branch so the dev actor stub has a real staff member to be.
// Returns false, and does nothing at all, when DevActor:Enabled is off.
if (app.Environment.IsDevelopment() && await app.Services.InitialiseDevelopmentDataAsync())
{
    app.Logger.LogInformation("Development data initialised.");
}

app.Run();
